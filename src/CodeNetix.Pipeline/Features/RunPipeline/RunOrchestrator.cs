using System.Text;
using CodeNetix.Contracts;
using CodeNetix.Pipeline.Data;
using CodeNetix.Pipeline.Domain;
using CodeNetix.Pipeline.Events;
using CodeNetix.Pipeline.Features.CancelRun;
using CodeNetix.Pipeline.Features.DecideMerge;
using CodeNetix.Pipeline.Features.ResolveConfig;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeNetix.Pipeline.Features.RunPipeline;

/// <summary>Orquestador de Ejecuciones (AD-2): único que invoca los métodos de transición del agregado.</summary>
internal interface IRunOrchestrator
{
    Task RunAsync(Guid runId, CancellationToken cancellationToken);
}

/// <summary>
/// Orquestador (AD-16), en el orden canónico:
/// <list type="number">
/// <item>Paso 1: el módulo GitHub clona y fija el <c>BaseSha</c> (<see cref="IRepositoryWorkspace"/>); se valida
/// <c>codenetix.yml</c> leído desde ese commit y se congela la <see cref="EffectiveConfig"/>. El <c>BaseSha</c>, la
/// instantánea, <see cref="RepositoryPrepared"/>, <see cref="RepoConfigResolved"/> y la transición a
/// <c>Analyzing</c> se confirman en el mismo <c>SaveChanges</c>.</item>
/// <item>Paso 2 (AD-6): <see cref="ISandboxManager.CreateForRunAsync"/> con el token de la Ejecución y
/// <see cref="SandboxCreated"/>; si falla, <c>Failed(StageError, "no se pudo crear el Sandbox: …")</c>. La sesión se
/// libera en el <c>finally</c> (antes de borrar el árbol de trabajo) y se registra <see cref="SandboxReleased"/>.</item>
/// <item>Paso 3 (<c>Analyzing</c>): GitHub lee la issue (si falla, <c>Failed(StageError, "no se pudo leer la issue: …")</c>),
/// se listan los archivos del árbol (<c>git ls-files</c>, como mucho <see cref="AnalysisInput.MaxRepositoryFiles"/>) y se
/// llama al Analista. <c>Ok</c> → la fila de <c>run_specs</c> y <see cref="SpecGenerated"/> en el mismo <c>SaveChanges</c>;
/// <c>NotAutomatable</c> → <see cref="AnalysisRejected"/> y <c>Failed(NotAutomatable, "&lt;categoría&gt;: &lt;explicación&gt;")</c>
/// en el mismo <c>SaveChanges</c> y, tras el commit, el comentario en la issue (<see cref="IIssueNotifier"/>) con
/// <see cref="IssueCommented"/> o <see cref="NotificationFailed"/>;
/// <c>SchemaInvalid</c> → <c>Failed(SchemaInvalid, último error)</c>; <c>ProviderError</c> →
/// <c>Failed(StageError, "Proveedor: …")</c> (el detalle se redacta al persistirse).</item>
/// <item>Paso 4 (<c>Coding</c>): captura la línea base una única vez con <see cref="ITestRunner.CaptureBaselineAsync"/>
/// y ejecuta el bucle de Rondas de revisión (<c>1..EffectiveConfig.MaxReviewRounds</c>). En cada ronda concede hasta
/// <c>EffectiveConfig.MaxCodingAttempts</c> intentos del Programador (<see cref="ICodingAgent"/>) + validación
/// obligatoria con <see cref="ITestRunner.RunAsync"/> usando la misma línea base. Al agotar los intentos de una ronda,
/// <c>Failed(TestsFailed)</c>.</item>
/// <item>Paso 5 (<c>Reviewing</c>): exporta <c>/workspace</c>, calcula el diff contra <c>BaseSha</c>, crea una Etapa
/// <c>Review</c> por Revisor activo y ejecuta los Revisores en paralelo persistiendo <c>run_verdicts</c> y
/// <see cref="VerdictRecorded"/>. Evalúa los Veredictos de la ronda: cualquier <c>Rejected</c> →
/// <c>Failed(ReviewRejected)</c>; unanimidad <c>Approved</c> → <c>Reviewing → Deciding</c>, publica el PR vía
/// <see cref="IPullRequestPublisher"/>, emite <see cref="PullRequestOpened"/> y transiciona a
/// <c>PullRequestCreated</c>; <c>ChangesRequested</c> con rondas restantes → <c>Reviewing → Coding</c>
/// (<see cref="PipelineRun.StartReviewRound"/>) pasando los hallazgos como feedback; rondas agotadas sin unanimidad →
/// <c>Failed(ReviewRoundsExhausted)</c>.</item>
/// </list>
/// Errores: clon o credenciales → <c>Failed(CloneFailed)</c>; YAML o esquema inválidos →
/// <c>Failed(InvalidRepoConfig)</c> sin llamar a ningún agente. El árbol de trabajo se borra al terminar
/// (en un <c>finally</c>): la Ejecución acaba en un Estado terminal o, si algo lanza, la cierra el Dispatcher.
/// </summary>
internal sealed partial class RunOrchestrator(
    IDbContextFactory<PipelineDbContext> contextFactory,
    RunEventLog log,
    IRepositoryWorkspace workspace,
    IIssueReader issues,
    IAnalyst analyst,
    ICodingAgent coder,
    ITestRunner testRunner,
    IReviewerAgent reviewer,
    IIssueNotifier notifier,
    IPullRequestPublisher pullRequestPublisher,
    IRunEventSink events,
    IBudgetLedger budgetLedger,
    ISandboxManager sandboxes,
    RunCancellationRegistry cancellationRegistry,
    IOptions<InstanceLimits> limits,
    ILogger<RunOrchestrator> logger) : IRunOrchestrator
{
    public const string IssueUnreadablePrefix = "no se pudo leer la issue: ";

    public const string ProviderErrorPrefix = "Proveedor: ";

    public const string SandboxFailedPrefix = "no se pudo crear el Sandbox: ";

    public const string TestRunnerFailedPrefix = "error del Sandbox ejecutando tests: ";

    public const string DiffFailedPrefix = "no se pudo calcular el diff: ";

    public const string PullRequestFailedPrefix = "no se pudo publicar el PR: ";

    /// <summary>Longitud máxima del detalle de un fallo.</summary>
    public const int MaxDetailLength = 1000;

    /// <summary>Longitud máxima del feedback enviado al Programador entre intentos.</summary>
    private const int MaxFeedbackLength = 8000;

    /// <summary>Timeout acotado para publicar el aviso terminal en GitHub (AD-8, AD-16 paso 7).</summary>
    private static readonly TimeSpan TerminalNotificationTimeout = TimeSpan.FromSeconds(15);

    public async Task RunAsync(Guid runId, CancellationToken stoppingToken)
    {
        // Serializar el arranque con cualquier cancelación directa en BD pendiente para este runId.
        RunCancellationRegistry.ActiveRunHandle cancelHandle;
        using (await cancellationRegistry.AcquireRunGateAsync(runId, stoppingToken).ConfigureAwait(false))
        {
            cancelHandle = cancellationRegistry.BeginRun(runId, stoppingToken);
        }

        using var _ = cancelHandle;
        var runToken = cancelHandle.Token;

        await using var context = await contextFactory.CreateDbContextAsync(stoppingToken).ConfigureAwait(false);
        var run = await context.Runs.SingleAsync(r => r.Id == runId, stoppingToken).ConfigureAwait(false);
        if (run.State.IsTerminal())
        {
            cancelHandle.CompleteCancellablePhase();
            return;
        }

        ISandboxSession? sandbox = null;
        PipelineRun? cancelledRun = null;
        PipelineRun? failedBudgetRun = null;
        var userCancelled = false;
        var budgetExhausted = false;
        var completed = false;
        try
        {
            ThrowIfCancelledOrBudgetExhausted(cancelHandle, runToken);

            if (run.State == RunState.Received)
            {
                await PrepareRepositoryAsync(context, run, runToken).ConfigureAwait(false);
                if (!run.State.IsTerminal())
                {
                    ThrowIfCancelledOrBudgetExhausted(cancelHandle, runToken);
                }
            }

            if (run.State == RunState.Analyzing)
            {
                sandbox = await CreateSandboxAsync(context, run, runToken).ConfigureAwait(false);
                if (!run.State.IsTerminal())
                {
                    ThrowIfCancelledOrBudgetExhausted(cancelHandle, runToken);
                }
            }

            if (run.State == RunState.Analyzing)
            {
                await AnalyzeAsync(context, run, runToken).ConfigureAwait(false);
                if (!run.State.IsTerminal())
                {
                    ThrowIfCancelledOrBudgetExhausted(cancelHandle, runToken);
                }
            }

            if (run.State == RunState.Analyzing)
            {
                await log.AddTransitionAsync(context, run, run.StartCoding(), runToken).ConfigureAwait(false);
                await log.SaveAndPublishAsync(context, runToken).ConfigureAwait(false);
                ThrowIfCancelledOrBudgetExhausted(cancelHandle, runToken);
            }

            if (run.State == RunState.Coding)
            {
                await CodeAsync(context, run, sandbox, runToken, stoppingToken).ConfigureAwait(false);
            }

            completed = true;
            cancelHandle.CompleteCancellablePhase();
        }
        catch (OperationCanceledException) when (cancelHandle.IsBudgetExhausted)
        {
            budgetExhausted = true;
            cancelHandle.CompleteCancellablePhase();
            var detail = string.IsNullOrWhiteSpace(cancelHandle.BudgetExhaustedDetail)
                ? "presupuesto agotado"
                : cancelHandle.BudgetExhaustedDetail;
            failedBudgetRun = await FailBudgetExceededActiveRunAsync(runId, detail).ConfigureAwait(false);
            completed = true;
        }
        catch (OperationCanceledException) when (cancelHandle.IsUserCancelRequested)
        {
            userCancelled = true;
            cancelHandle.CompleteCancellablePhase();
            cancelledRun = await CancelActiveRunAsync(runId).ConfigureAwait(false);
            completed = true;
        }
        finally
        {
            cancelHandle.CompleteCancellablePhase();

            // AD-16 paso 8: el Sandbox se libera siempre (cualquier Estado, también al cancelar o agotar presupuesto) y antes del árbol.
            if (sandbox is not null)
            {
                await ReleaseSandboxAsync(run.Id, sandbox).ConfigureAwait(false);
            }

            // Una cancelación por apagado deja la Ejecución no terminal (el siguiente arranque la cierra como
            // Failed(HostRestarted), AD-18); aun así no se deja el árbol en disco.
            if (!completed || run.State.IsTerminal() || userCancelled || budgetExhausted)
            {
                try
                {
                    await workspace.DeleteAsync(runId, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogWorkspaceDeleteFailed(RunShortId.Of(runId), log.Redactor.Redact(ex.Message));
                }
            }
        }

        if (failedBudgetRun is not null)
        {
            await NotifyTerminalAsync(failedBudgetRun, cancellationToken: CancellationToken.None).ConfigureAwait(false);
        }
        else if (cancelledRun is not null)
        {
            await NotifyTerminalAsync(cancelledRun, cancellationToken: CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static void ThrowIfCancelledOrBudgetExhausted(RunCancellationRegistry.ActiveRunHandle cancelHandle, CancellationToken runToken)
    {
        if (cancelHandle.IsBudgetExhausted || cancelHandle.IsUserCancelRequested)
        {
            throw new OperationCanceledException(runToken);
        }
    }

    private async Task<PipelineRun?> FailBudgetExceededActiveRunAsync(Guid runId, string detail)
    {
        const int maxAttempts = 5;
        for (var attempt = 1; ; attempt++)
        {
            await using var failContext = await contextFactory.CreateDbContextAsync(CancellationToken.None).ConfigureAwait(false);
            var current = await failContext.Runs.SingleOrDefaultAsync(r => r.Id == runId, CancellationToken.None).ConfigureAwait(false);
            if (current is null || current.State.IsTerminal())
            {
                return null;
            }

            var change = current.Fail(FailureReason.BudgetExceeded, Truncate(detail));
            await log.AddTransitionAsync(failContext, current, change, CancellationToken.None).ConfigureAwait(false);
            try
            {
                await log.SaveAndPublishAsync(failContext, CancellationToken.None).ConfigureAwait(false);
                return current;
            }
            catch (DbUpdateException ex) when (attempt < maxAttempts && RunEventLog.IsUniqueViolation(ex))
            {
                // Otro escritor (p. ej. LlmReverseProxy.ChargeAsync) tomó el mismo seq en run_events: reintentar con contexto limpio.
            }
        }
    }

    private async Task<PipelineRun?> CancelActiveRunAsync(Guid runId)
    {
        const int maxAttempts = 5;
        for (var attempt = 1; ; attempt++)
        {
            await using var cancelContext = await contextFactory.CreateDbContextAsync(CancellationToken.None).ConfigureAwait(false);
            var current = await cancelContext.Runs.SingleOrDefaultAsync(r => r.Id == runId, CancellationToken.None).ConfigureAwait(false);
            if (current is null || current.State.IsTerminal() || current.State == RunState.Deciding)
            {
                return null;
            }

            var change = current.Cancel();
            await log.AddTransitionAsync(cancelContext, current, change, CancellationToken.None).ConfigureAwait(false);
            try
            {
                await log.SaveAndPublishAsync(cancelContext, CancellationToken.None).ConfigureAwait(false);
                return current;
            }
            catch (DbUpdateException ex) when (attempt < maxAttempts && RunEventLog.IsUniqueViolation(ex))
            {
                // Otro escritor tomó el mismo seq en run_events: reintentar con contexto limpio.
            }
        }
    }

    /// <summary>AD-16 paso 1: clonar, fijar el <c>BaseSha</c>, validar <c>codenetix.yml</c> y congelar la configuración.</summary>
    private async Task PrepareRepositoryAsync(PipelineDbContext context, PipelineRun run, CancellationToken cancellationToken)
    {
        if (!GitHubObjectKey.TryParse(run.ObjectKey, out var owner, out var repo, out _))
        {
            await FailAsync(context, run, FailureReason.StageError, $"objeto no admitido: {run.ObjectKey}", cancellationToken).ConfigureAwait(false);
            return;
        }

        PreparedRepository prepared;
        try
        {
            prepared = await workspace.PrepareAsync(run.Id, owner, repo, cancellationToken).ConfigureAwait(false);
        }
        catch (RepositoryPreparationException ex)
        {
            await FailAsync(context, run, FailureReason.CloneFailed, ex.Message, cancellationToken).ConfigureAwait(false);
            return;
        }

        await log.AddAsync(context, run.Id, run.RecordBaseSha(prepared.BaseSha), cancellationToken).ConfigureAwait(false);

        RepoConfigFile? file = null;
        if (prepared.RepoConfigYaml is { } yaml)
        {
            var validation = RepoConfigValidator.Validate(yaml);
            if (!validation.IsValid)
            {
                // RepositoryPrepared y el fallo, en el mismo SaveChanges. Ningún agente llega a ejecutarse.
                await FailAsync(context, run, FailureReason.InvalidRepoConfig, Truncate(validation.Error!), cancellationToken).ConfigureAwait(false);
                return;
            }

            file = validation.Config;
        }

        var config = EffectiveConfigCalculator.Calculate(limits.Value, file);
        await log.AddAsync(context, run.Id, run.FreezeConfiguration(config), cancellationToken).ConfigureAwait(false);
        await log.AddTransitionAsync(context, run, run.StartAnalysis(), cancellationToken).ConfigureAwait(false);
        await log.SaveAndPublishAsync(context, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// AD-16 paso 2: crea el Sandbox con el token de la Ejecución (al cancelarse, la sesión detiene el contenedor) y
    /// registra <see cref="SandboxCreated"/>. Si no se puede crear, la Ejecución falla y devuelve <c>null</c>.
    /// </summary>
    private async Task<ISandboxSession?> CreateSandboxAsync(PipelineDbContext context, PipelineRun run, CancellationToken cancellationToken)
    {
        ISandboxSession session;
        try
        {
            session = await sandboxes.CreateForRunAsync(run.Id, cancellationToken).ConfigureAwait(false);
        }
        catch (SandboxCreationException ex)
        {
            await FailAsync(context, run, FailureReason.StageError, Truncate(SandboxFailedPrefix + ex.Message), cancellationToken).ConfigureAwait(false);
            return null;
        }

        try
        {
            await log.AddAsync(context, run.Id, new SandboxCreated(session.Image), cancellationToken).ConfigureAwait(false);
            await log.SaveAndPublishAsync(context, cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch
        {
            // Sin evento persistido no hay quien la libere: se libera aquí.
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Libera la sesión (nunca lanza) y registra <see cref="SandboxReleased"/>; un fallo solo se anota en el log.</summary>
    private async Task ReleaseSandboxAsync(Guid runId, ISandboxSession sandbox)
    {
        try
        {
            await sandbox.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogSandboxDisposeFailed(RunShortId.Of(runId), log.Redactor.Redact(ex.Message));
        }

        try
        {
            await events.AppendAsync(runId, null, new SandboxReleased(), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            LogSandboxReleasedNotRecorded(RunShortId.Of(runId), ex.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se ha podido liberar el Sandbox de la Ejecución {ShortId}: {Reason}.")]
    private partial void LogSandboxDisposeFailed(string shortId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se ha podido borrar el árbol de trabajo de la Ejecución {ShortId}: {Reason}.")]
    private partial void LogWorkspaceDeleteFailed(string shortId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se ha podido registrar SandboxReleased de la Ejecución {ShortId}: {Reason}.")]
    private partial void LogSandboxReleasedNotRecorded(string shortId, string reason);

    /// <summary>
    /// AD-16 paso 3: leer la issue, listar el árbol y pedir la Spec al Analista. Solo con una Spec válida la Ejecución
    /// sigue en <c>Analyzing</c> al volver; en cualquier otro caso queda en <c>Failed</c>.
    /// </summary>
    private async Task AnalyzeAsync(PipelineDbContext context, PipelineRun run, CancellationToken cancellationToken)
    {
        if (!GitHubObjectKey.TryParse(run.ObjectKey, out var owner, out var repo, out var number))
        {
            await FailAsync(context, run, FailureReason.StageError, $"objeto no admitido: {run.ObjectKey}", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (run.GetEffectiveConfig() is not { } config)
        {
            await FailAsync(context, run, FailureReason.StageError, "la Ejecución no tiene configuración efectiva", cancellationToken).ConfigureAwait(false);
            return;
        }

        GitHubIssue issue;
        try
        {
            issue = await issues.GetAsync(owner, repo, number, cancellationToken).ConfigureAwait(false);
        }
        catch (GitHubUnavailableException ex)
        {
            await FailAsync(context, run, FailureReason.StageError, Truncate(IssueUnreadablePrefix + ex.Message), cancellationToken).ConfigureAwait(false);
            return;
        }

        IReadOnlyList<string> files;
        try
        {
            files = await workspace.ListFilesAsync(run.Id, cancellationToken).ConfigureAwait(false);
        }
        catch (RepositoryPreparationException ex)
        {
            await FailAsync(context, run, FailureReason.StageError, Truncate("no se pudo listar el repositorio: " + ex.Message), cancellationToken).ConfigureAwait(false);
            return;
        }

        var (limited, truncated) = AnalysisInput.LimitFiles(files);
        var input = new AnalysisInput(run.Id, issue.Title, issue.Body, limited, truncated, config);
        var result = await analyst.AnalyzeAsync(input, cancellationToken).ConfigureAwait(false);
        if (result is AnalysisResult.BudgetExhausted exhausted)
        {
            await FailAsync(context, run, FailureReason.BudgetExceeded, Truncate(exhausted.Detail), CancellationToken.None).ConfigureAwait(false);
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        switch (result)
        {
            case AnalysisResult.Ok ok:
                // La Spec y su evento, en el mismo SaveChanges.
                context.Specs.Add(new RunSpecRecord
                {
                    RunId = run.Id,
                    SpecJson = SpecSerializer.Serialize(ok.Spec),
                    Model = ok.Model,
                    Attempts = ok.Attempts,
                    CreatedAt = log.UtcNow,
                });
                await log.AddAsync(context, run.Id, new SpecGenerated(ok.Attempts, ok.Model), cancellationToken).ConfigureAwait(false);
                await log.SaveAndPublishAsync(context, cancellationToken).ConfigureAwait(false);
                break;

            case AnalysisResult.NotAutomatable rejected:
                // AnalysisRejected y Failed(NotAutomatable) en el mismo SaveChanges; nunca StartCoding ni run_specs.
                await log.AddAsync(context, run.Id, new AnalysisRejected(rejected.Category, rejected.Attempts, rejected.Model), cancellationToken).ConfigureAwait(false);
                await FailAsync(context, run, FailureReason.NotAutomatable, Truncate($"{rejected.Category.ToWireName()}: {rejected.Explanation}"), cancellationToken).ConfigureAwait(false);

                // Solo tras el commit: el aviso en la issue (AD-8). Su resultado no cambia el Estado terminal.
                await NotifyNotAutomatableAsync(run, owner, repo, number, rejected).ConfigureAwait(false);
                break;

            case AnalysisResult.SchemaInvalid invalid:
                await FailAsync(context, run, FailureReason.SchemaInvalid, Truncate(invalid.LastError), cancellationToken).ConfigureAwait(false);
                break;

            case AnalysisResult.ProviderError provider:
                await FailAsync(context, run, FailureReason.StageError, Truncate(ProviderErrorPrefix + provider.Detail), cancellationToken).ConfigureAwait(false);
                break;

            default:
                throw new InvalidOperationException($"Resultado del Analista desconocido: {result.GetType().Name}.");
        }
    }

    /// <summary>
    /// Publica el comentario de rechazo (idempotente por (<c>runId</c>, <c>NotAutomatable</c>)) y registra
    /// <see cref="IssueCommented"/> o <see cref="NotificationFailed"/> con el sumidero externo. Un fallo no relanza.
    /// </summary>
    private Task NotifyNotAutomatableAsync(
        PipelineRun run,
        string owner,
        string repo,
        long number,
        AnalysisResult.NotAutomatable rejected)
    {
        var body = log.Redactor.Redact(NotAutomatableComment.Build(run.ShortId, rejected.Category, rejected.Explanation));
        return SendNotificationAsync(run.Id, owner, repo, number, NotificationKind.NotAutomatable, body);
    }

    /// <summary>
    /// AD-16 paso 7 (FR-19, AD-8): publica el aviso terminal en la issue de GitHub tras confirmar el Estado terminal
    /// en BD. Omite limpiamente orígenes no GitHub y no duplica el rechazo temprano <c>Failed(NotAutomatable)</c>.
    /// Nunca altera el Estado terminal ni relanza excepciones.
    /// </summary>
    internal Task NotifyTerminalAsync(
        PipelineRun run,
        PullRequestInfo? pullRequest = null,
        PolicyResult? policy = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        _ = cancellationToken; // Tras confirmar el Estado terminal, el envío usa siempre un timeout acotado independiente.

        if (!GitHubObjectKey.TryParse(run.ObjectKey, out var owner, out var repo, out var number))
        {
            return Task.CompletedTask;
        }

        switch (run.State)
        {
            case RunState.PullRequestCreated:
                ArgumentNullException.ThrowIfNull(pullRequest);
                return SendNotificationAsync(
                    run.Id,
                    owner,
                    repo,
                    number,
                    NotificationKind.PullRequestCreated,
                    TerminalNotificationComment.BuildPullRequestCreated(run.ShortId, pullRequest, policy, log.Redactor));

            case RunState.Failed when run.FailureReason is { } reason && reason != FailureReason.NotAutomatable:
                return SendNotificationAsync(
                    run.Id,
                    owner,
                    repo,
                    number,
                    NotificationKind.RunFailed,
                    TerminalNotificationComment.BuildRunFailed(run.ShortId, reason, run.FailureDetail, log.Redactor));

            case RunState.Cancelled:
                return SendNotificationAsync(
                    run.Id,
                    owner,
                    repo,
                    number,
                    NotificationKind.RunCancelled,
                    TerminalNotificationComment.BuildRunCancelled(run.ShortId, log.Redactor));

            default:
                return Task.CompletedTask;
        }
    }

    private async Task SendNotificationAsync(
        Guid runId,
        string owner,
        string repo,
        long number,
        NotificationKind kind,
        string body)
    {
        using var notifyCts = new CancellationTokenSource(TerminalNotificationTimeout);

        RunEvent outcome;
        try
        {
            var result = await notifier.NotifyAsync(runId, owner, repo, number, kind, body, notifyCts.Token).ConfigureAwait(false);
            outcome = result.Status == NotificationStatus.Posted
                ? new IssueCommented(kind, result.CommentUrl ?? string.Empty)
                : new NotificationFailed(kind, log.Redactor.Redact(result.Error ?? "error desconocido"));
        }
        catch (OperationCanceledException)
        {
            outcome = new NotificationFailed(kind, "tiempo agotado al publicar el aviso en GitHub");
        }
        catch (Exception ex)
        {
            outcome = new NotificationFailed(kind, log.Redactor.Redact(StuckRunRecovery.ShortReason(ex)));
        }

        try
        {
            await events.AppendAsync(runId, null, outcome, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            LogNotificationEventNotRecorded(RunShortId.Of(runId), kind, log.Redactor.Redact(ex.Message));
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se ha podido registrar el evento de notificación {Kind} de la Ejecución {ShortId}: {Reason}.")]
    private partial void LogNotificationEventNotRecorded(string shortId, NotificationKind kind, string reason);

    /// <summary>
    /// AD-16 pasos 4 y 5 (FR-11, FR-14, FR-15, FR-16): captura una única línea base de tests y ejecuta el bucle de
    /// Rondas de revisión (<c>1..EffectiveConfig.MaxReviewRounds</c>). Cada ronda concede hasta
    /// <c>EffectiveConfig.MaxCodingAttempts</c> intentos de Programador + <see cref="ITestRunner.RunAsync"/>, seguidos
    /// de revisión en paralelo y evaluación determinista de Veredictos (<c>Rejected</c>, unanimidad <c>Approved</c>,
    /// o <c>ChangesRequested</c> con transición <c>Reviewing → Coding</c> o <c>ReviewRoundsExhausted</c>).
    /// </summary>
    private async Task CodeAsync(
        PipelineDbContext context,
        PipelineRun run,
        ISandboxSession? sandbox,
        CancellationToken cancellationToken,
        CancellationToken stoppingToken)
    {
        if (sandbox is null)
        {
            await FailAsync(context, run, FailureReason.StageError, "el Sandbox no está disponible", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!GitHubObjectKey.TryParse(run.ObjectKey, out var owner, out var repo, out var number))
        {
            await FailAsync(context, run, FailureReason.StageError, $"objeto no admitido: {run.ObjectKey}", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (run.GetEffectiveConfig() is not { } config)
        {
            await FailAsync(context, run, FailureReason.StageError, "la Ejecución no tiene configuración efectiva", cancellationToken).ConfigureAwait(false);
            return;
        }

        GitHubIssue issue;
        try
        {
            issue = await issues.GetAsync(owner, repo, number, cancellationToken).ConfigureAwait(false);
        }
        catch (GitHubUnavailableException ex)
        {
            await FailAsync(context, run, FailureReason.StageError, Truncate(IssueUnreadablePrefix + ex.Message), cancellationToken).ConfigureAwait(false);
            return;
        }

        var specRecord = await context.Specs.SingleOrDefaultAsync(s => s.RunId == run.Id, cancellationToken).ConfigureAwait(false);
        if (specRecord is null)
        {
            await FailAsync(context, run, FailureReason.StageError, "no se encontró la Spec de la Ejecución", cancellationToken).ConfigureAwait(false);
            return;
        }

        Spec spec;
        try
        {
            spec = SpecSerializer.Deserialize(specRecord.SpecJson);
        }
        catch (Exception ex)
        {
            await FailAsync(context, run, FailureReason.StageError, Truncate("error al deserializar la Spec: " + ex.Message), cancellationToken).ConfigureAwait(false);
            return;
        }

        TestBaseline baseline;
        try
        {
            baseline = await testRunner.CaptureBaselineAsync(sandbox, cancellationToken).ConfigureAwait(false);
        }
        catch (SandboxUnavailableException ex)
        {
            await FailAsync(context, run, FailureReason.StageError, Truncate(TestRunnerFailedPrefix + ex.Message), cancellationToken).ConfigureAwait(false);
            return;
        }

        string? reviewFeedback = null;
        var maxRounds = Math.Max(1, config.MaxReviewRounds);
        for (var round = 1; round <= maxRounds && run.State == RunState.Coding; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestReport? passedReport = null;
            string? attemptFeedback = null;
            var roundAttempts = 0;

            while (run.State == RunState.Coding && roundAttempts < config.MaxCodingAttempts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                roundAttempts++;
                var attemptNumber = run.CodingAttempts + 1;
                var feedback = CombineFeedback(reviewFeedback, attemptFeedback);
                var input = new CodingInput(run.Id, attemptNumber, issue.Title, issue.Body, spec, config, sandbox, feedback);
                var result = await coder.RunAsync(input, cancellationToken).ConfigureAwait(false);
                if (result is CodingResult.BudgetExhausted exhausted)
                {
                    await FailAsync(context, run, FailureReason.BudgetExceeded, Truncate(exhausted.Detail), CancellationToken.None).ConfigureAwait(false);
                    return;
                }

                cancellationToken.ThrowIfCancellationRequested();

                switch (result)
                {
                    case CodingResult.Completed:
                        TestReport report;
                        try
                        {
                            report = await testRunner.RunAsync(sandbox, baseline, cancellationToken).ConfigureAwait(false);
                        }
                        catch (SandboxUnavailableException ex)
                        {
                            await FailAsync(context, run, FailureReason.StageError, Truncate(TestRunnerFailedPrefix + ex.Message), cancellationToken).ConfigureAwait(false);
                            return;
                        }

                        cancellationToken.ThrowIfCancellationRequested();

                        report = RedactTestReport(report);
                        await UpsertTestReportAsync(context, run.Id, report, cancellationToken).ConfigureAwait(false);

                        if (report.Passed)
                        {
                            run.CompleteCodingAttempt();
                            await log.SaveAndPublishAsync(context, cancellationToken).ConfigureAwait(false);
                            passedReport = report;
                            break;
                        }

                        var testFeedback = BuildTestFailureFeedback(report);
                        var shortReason = Truncate(testFeedback);
                        var testAttemptFailedEvent = run.RecordFailedCodingAttempt(shortReason);
                        await log.AddAsync(context, run.Id, testAttemptFailedEvent, cancellationToken).ConfigureAwait(false);
                        await log.SaveAndPublishAsync(context, cancellationToken).ConfigureAwait(false);

                        attemptFeedback = TextLimits.Truncate(testFeedback, MaxFeedbackLength);
                        if (roundAttempts >= config.MaxCodingAttempts)
                        {
                            await FailAsync(context, run, FailureReason.TestsFailed, shortReason, cancellationToken).ConfigureAwait(false);
                            return;
                        }
                        break;

                    case CodingResult.Failed failed:
                        var attemptFailedEvent = run.RecordFailedCodingAttempt(failed.Reason);
                        await log.AddAsync(context, run.Id, attemptFailedEvent, cancellationToken).ConfigureAwait(false);
                        await log.SaveAndPublishAsync(context, cancellationToken).ConfigureAwait(false);

                        attemptFeedback = TextLimits.Truncate(log.Redactor.Redact(failed.Reason), MaxFeedbackLength);
                        if (roundAttempts >= config.MaxCodingAttempts)
                        {
                            await FailAsync(context, run, FailureReason.TestsFailed, Truncate(failed.Reason), cancellationToken).ConfigureAwait(false);
                            return;
                        }
                        break;

                    default:
                        throw new InvalidOperationException($"Resultado del Programador desconocido: {result.GetType().Name}.");
                }

                if (passedReport is not null)
                {
                    break;
                }
            }

            if (passedReport is null)
            {
                if (run.State == RunState.Coding)
                {
                    await FailAsync(context, run, FailureReason.TestsFailed, "reintentos agotados", cancellationToken).ConfigureAwait(false);
                }

                return;
            }

            var reviewOutcome = await ReviewAsync(context, run, sandbox, spec, passedReport, config, round, cancellationToken).ConfigureAwait(false);
            if (reviewOutcome is null)
            {
                return;
            }

            var (roundDiff, roundVerdicts) = reviewOutcome.Value;

            // 1. Precedencia máxima: cualquier Rejected termina inmediatamente en Failed(ReviewRejected).
            var rejections = roundVerdicts
                .Where(v => v.Verdict.Decision == VerdictDecision.Rejected)
                .ToList();
            if (rejections.Count > 0)
            {
                await FailAsync(
                    context,
                    run,
                    FailureReason.ReviewRejected,
                    Truncate(BuildRejectionDetail(rejections)),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            // 2. Unanimidad Approved → Deciding → clasificar riesgo (AD-9, FR-18), crear commit único, push y abrir PR (FR-17, AD-8).
            if (roundVerdicts.Count > 0 && roundVerdicts.All(v => v.Verdict.Decision == VerdictDecision.Approved))
            {
                await PublishPullRequestAsync(context, run, owner, repo, number, spec, passedReport, config, roundDiff, roundVerdicts, round, cancellationToken, stoppingToken).ConfigureAwait(false);
                return;
            }

            // 3. Al menos un ChangesRequested sin Rejected: iterar si quedan rondas o agotar rondas.
            var changesRequested = roundVerdicts
                .Where(v => v.Verdict.Decision == VerdictDecision.ChangesRequested)
                .ToList();

            if (changesRequested.Count == 0)
            {
                await FailAsync(context, run, FailureReason.StageError, "veredictos de revisión inválidos o vacíos", cancellationToken).ConfigureAwait(false);
                return;
            }

            if (round >= maxRounds)
            {
                await FailAsync(
                    context,
                    run,
                    FailureReason.ReviewRoundsExhausted,
                    Truncate(BuildRoundsExhaustedDetail(round, changesRequested)),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            reviewFeedback = TextLimits.Truncate(BuildReviewFeedback(round, changesRequested), MaxFeedbackLength);
            await log.AddTransitionAsync(context, run, run.StartReviewRound(), cancellationToken).ConfigureAwait(false);
            await log.SaveAndPublishAsync(context, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// AD-8, AD-11, FR-14, FR-15: exporta <c>/workspace</c> desde el Sandbox, calcula el diff contra <c>BaseSha</c>
    /// en el clon del host, entra en <c>Reviewing</c>, crea una Etapa <c>Review</c> por Revisor activo y ejecuta los
    /// Revisores en paralelo. Persiste <c>run_verdicts</c> y emite <see cref="VerdictRecorded"/>. Devuelve los
    /// Veredictos ya redactados de la ronda, o <c>null</c> si falló antes de revisar.
    /// </summary>
    private async Task<(DiffResult Diff, IReadOnlyList<(ReviewerKind Reviewer, Verdict Verdict)> Verdicts)?> ReviewAsync(
        PipelineDbContext context,
        PipelineRun run,
        ISandboxSession sandbox,
        Spec spec,
        TestReport testReport,
        EffectiveConfig config,
        int round,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(run.BaseSha))
        {
            await FailAsync(context, run, FailureReason.StageError, "la Ejecución no tiene BaseSha para calcular el diff", cancellationToken).ConfigureAwait(false);
            return null;
        }

        DiffResult diff;
        try
        {
            var tarPath = Path.Combine(Path.GetTempPath(), $"codenetix-ws-export-{Guid.NewGuid():N}.tar");
            await using var tarStream = new FileStream(
                tarPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.DeleteOnClose);

            await sandbox.ExportWorkspaceAsync(tarStream, cancellationToken).ConfigureAwait(false);
            await tarStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            tarStream.Position = 0;

            diff = await workspace.ComputeDiffAsync(run.Id, tarStream, run.BaseSha, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SandboxUnavailableException or RepositoryPreparationException or IOException)
        {
            await FailAsync(context, run, FailureReason.StageError, Truncate(DiffFailedPrefix + ex.Message), cancellationToken).ConfigureAwait(false);
            return null;
        }

        var boundedDiff = diff.Truncate(ReviewInput.MaxDiffLength);
        var activeReviewers = (config.Reviewers.IsDefault || config.Reviewers.Length == 0
            ? EffectiveConfig.DefaultReviewers
            : config.Reviewers)
            .Distinct()
            .ToArray();

        await log.AddTransitionAsync(context, run, run.StartReview(), cancellationToken).ConfigureAwait(false);

        var stageStartedAt = log.UtcNow;
        var stages = new List<(ReviewerKind Reviewer, RunStageRecord Stage)>(activeReviewers.Length);
        foreach (var kind in activeReviewers)
        {
            var stage = new RunStageRecord
            {
                Id = Guid.CreateVersion7(stageStartedAt),
                RunId = run.Id,
                Kind = StageKind.Review,
                Round = round,
                Reviewer = kind,
                StartedAt = stageStartedAt,
            };
            context.Stages.Add(stage);
            stages.Add((kind, stage));
        }

        await log.SaveAndPublishAsync(context, cancellationToken).ConfigureAwait(false);

        var tasks = stages.Select(item => RunReviewerSafeAsync(
            new ReviewInput(run.Id, item.Stage.Id, round, item.Reviewer, spec, boundedDiff, testReport, config),
            cancellationToken)).ToArray();

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        var budgetExhaustedReview = results
            .Select(static r => r.Result)
            .FirstOrDefault(static r => r.BudgetExhausted);
        if (budgetExhaustedReview is not null)
        {
            var detail = string.IsNullOrWhiteSpace(budgetExhaustedReview.Verdict.Summary)
                ? "presupuesto agotado"
                : budgetExhaustedReview.Verdict.Summary;
            await FailAsync(context, run, FailureReason.BudgetExceeded, Truncate(detail), CancellationToken.None).ConfigureAwait(false);
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var roundVerdicts = new List<(ReviewerKind Reviewer, Verdict Verdict)>(stages.Count);

        for (var i = 0; i < stages.Count; i++)
        {
            var (kind, stage) = stages[i];
            var (reviewResult, completedAt) = results[i];
            var redactedVerdict = RedactVerdict(reviewResult.Verdict);
            roundVerdicts.Add((kind, redactedVerdict));

            stage.CompletedAt = completedAt;

            context.Verdicts.Add(new RunVerdictRecord
            {
                Id = Guid.CreateVersion7(completedAt),
                RunId = run.Id,
                StageId = stage.Id,
                Round = round,
                Reviewer = kind,
                Decision = redactedVerdict.Decision,
                Summary = redactedVerdict.Summary,
                FindingsJson = VerdictFindingsSerializer.Serialize(redactedVerdict.Findings),
                Model = reviewResult.Model,
                Attempts = reviewResult.Attempts,
                CreatedAt = completedAt,
            });

            await log.AddAsync(
                context,
                run.Id,
                new VerdictRecorded(round, kind, redactedVerdict.Decision, redactedVerdict.Findings.Length, reviewResult.Model),
                cancellationToken,
                stageId: stage.Id).ConfigureAwait(false);
        }

        await log.SaveAndPublishAsync(context, cancellationToken).ConfigureAwait(false);
        return (diff, roundVerdicts);
    }

    private async Task<(ReviewResult Result, DateTimeOffset CompletedAt)> RunReviewerSafeAsync(ReviewInput input, CancellationToken cancellationToken)
    {
        try
        {
            var result = await reviewer.ReviewAsync(input, cancellationToken).ConfigureAwait(false);
            if (result.BudgetExhausted)
            {
                return (result, log.UtcNow);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return (result, log.UtcNow);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            var detail = Truncate($"Error inesperado del Revisor {input.ReviewerKind}: {ex.Message}");
            var fallback = new ReviewResult(
                new Verdict(VerdictDecision.ChangesRequested, detail, []),
                Attempts: 1,
                Model: "error",
                FallbackDueToError: true);
            return (fallback, log.UtcNow);
        }
    }

    private Verdict RedactVerdict(Verdict verdict)
    {
        var redactor = log.Redactor;
        var summary = redactor.Redact(verdict.Summary);
        var findings = verdict.Findings.Length == 0
            ? verdict.Findings
            : [.. verdict.Findings.Select(f => f with
            {
                File = redactor.Redact(f.File),
                Message = redactor.Redact(f.Message),
            })];
        return verdict with { Summary = summary, Findings = findings };
    }

    private TestReport RedactTestReport(TestReport report)
    {
        var redactor = log.Redactor;
        var redactedOutput = report.BuildOutput is null ? null : redactor.Redact(report.BuildOutput);
        var redactedFailures = report.FailedTestCases.Count == 0
            ? report.FailedTestCases
            : report.FailedTestCases
                .Select(tc => tc with
                {
                    ErrorMessage = tc.ErrorMessage is null ? null : redactor.Redact(tc.ErrorMessage),
                    StackTrace = tc.StackTrace is null ? null : redactor.Redact(tc.StackTrace),
                })
                .ToArray();

        return report with
        {
            BuildOutput = redactedOutput,
            FailedTestCases = redactedFailures,
        };
    }

    private async Task UpsertTestReportAsync(PipelineDbContext context, Guid runId, TestReport report, CancellationToken cancellationToken)
    {
        var existing = await context.TestReports.SingleOrDefaultAsync(t => t.RunId == runId, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            context.TestReports.Add(new RunTestReportRecord
            {
                RunId = runId,
                ReportJson = TestReportSerializer.Serialize(report),
                Passed = report.Passed,
                TotalTests = report.TotalTests,
                FailedTests = report.FailedTests,
                NewTestsCount = report.NewTests.Count,
                CreatedAt = log.UtcNow,
            });
        }
        else
        {
            existing.ReportJson = TestReportSerializer.Serialize(report);
            existing.Passed = report.Passed;
            existing.TotalTests = report.TotalTests;
            existing.FailedTests = report.FailedTests;
            existing.NewTestsCount = report.NewTests.Count;
            existing.CreatedAt = log.UtcNow;
        }
    }

    internal static string BuildTestFailureFeedback(TestReport report)
    {
        if (!report.BuildSucceeded)
        {
            var output = string.IsNullOrWhiteSpace(report.BuildOutput)
                ? "sin salida del compilador"
                : report.BuildOutput.Trim();
            return $"La compilación ha fallado:\n{output}";
        }

        var sb = new StringBuilder();
        if (report.FailedTests > 0)
        {
            sb.Append($"Han fallado {report.FailedTests} test(s):");
            foreach (var failed in report.FailedTestCases)
            {
                sb.AppendLine();
                sb.Append("- ").Append(failed.Name);
                if (!string.IsNullOrWhiteSpace(failed.ErrorMessage))
                {
                    sb.Append(": ").Append(failed.ErrorMessage);
                }

                if (!string.IsNullOrWhiteSpace(failed.StackTrace))
                {
                    sb.AppendLine().Append("  ").Append(failed.StackTrace);
                }
            }
        }

        if (report.NewTests.Count == 0)
        {
            if (sb.Length > 0)
            {
                sb.AppendLine();
            }

            sb.Append("No se ha añadido ningún test nuevo que pase y verifique el cambio.");
        }

        return sb.Length > 0 ? sb.ToString() : "Los tests no han superado los criterios de aprobación.";
    }

    internal static string? CombineFeedback(string? reviewFeedback, string? attemptFeedback)
    {
        if (string.IsNullOrEmpty(reviewFeedback))
        {
            return string.IsNullOrEmpty(attemptFeedback)
                ? null
                : TextLimits.Truncate(attemptFeedback, MaxFeedbackLength);
        }

        if (string.IsNullOrEmpty(attemptFeedback))
        {
            return TextLimits.Truncate(reviewFeedback, MaxFeedbackLength);
        }

        return TextLimits.Truncate($"{reviewFeedback}\n\n{attemptFeedback}", MaxFeedbackLength);
    }

    internal static string BuildReviewFeedback(int round, IReadOnlyList<(ReviewerKind Reviewer, Verdict Verdict)> changesRequested)
    {
        var sb = new StringBuilder();
        sb.Append($"Los Revisores han solicitado cambios en la ronda {round}:");
        foreach (var (kind, verdict) in changesRequested)
        {
            sb.Append('\n');
            sb.Append($"[{kind.ToString().ToLowerInvariant()}] {verdict.Summary.Trim()}");
            foreach (var finding in verdict.Findings)
            {
                sb.Append('\n');
                var location = finding.Line is > 0 ? $"{finding.File}:{finding.Line}" : finding.File;
                sb.Append($"- ({finding.Severity}) {location}: {finding.Message.Trim()}");
            }
        }

        return sb.ToString();
    }

    internal static string BuildRejectionDetail(IReadOnlyList<(ReviewerKind Reviewer, Verdict Verdict)> rejections)
    {
        var parts = rejections.Select(r => $"{r.Reviewer.ToString().ToLowerInvariant()}: {NormalizeSingleLine(r.Verdict.Summary)}");
        return "Revisión rechazada: " + string.Join("; ", parts);
    }

    internal static string BuildRoundsExhaustedDetail(int rounds, IReadOnlyList<(ReviewerKind Reviewer, Verdict Verdict)> changesRequested)
    {
        var parts = changesRequested.Select(r => $"{r.Reviewer.ToString().ToLowerInvariant()}: {NormalizeSingleLine(r.Verdict.Summary)}");
        var detail = string.Join("; ", parts);
        return string.IsNullOrEmpty(detail)
            ? $"Rondas de revisión agotadas ({rounds}) sin aprobación unánime."
            : $"Rondas de revisión agotadas ({rounds}): {detail}";
    }

    private static string NormalizeSingleLine(string value) =>
        string.Join(' ', value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    /// <summary>
    /// AD-16 paso 6 (FR-17, FR-18, AD-8, AD-9): transiciona <c>Reviewing → Deciding</c>, clasifica el riesgo de forma
    /// determinista con <see cref="MergePolicy.Classify"/> y emite <see cref="RiskClassified"/>, construye el cuerpo
    /// trazable redactado, publica el PR vía <see cref="IPullRequestPublisher"/>, emite <see cref="PullRequestOpened"/>
    /// y transiciona <c>Deciding → PullRequestCreated</c>. Ante error, transiciona <c>Deciding → Failed(StageError)</c>.
    /// </summary>
    private async Task PublishPullRequestAsync(
        PipelineDbContext context,
        PipelineRun run,
        string owner,
        string repo,
        long issueNumber,
        Spec spec,
        TestReport testReport,
        EffectiveConfig config,
        DiffResult diff,
        IReadOnlyList<(ReviewerKind Reviewer, Verdict Verdict)> roundVerdicts,
        int round,
        CancellationToken runToken,
        CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(run.BaseSha))
        {
            await FailAsync(context, run, FailureReason.StageError, "la Ejecución no tiene BaseSha para publicar el PR", runToken).ConfigureAwait(false);
            return;
        }

        // Barrera atómica (FR-23, AD-2): si se solicitó cancelación antes de Deciding, abortar sin crear PR.
        if (!cancellationRegistry.TryEnterDeciding(run.Id))
        {
            throw new OperationCanceledException(runToken);
        }

        // A partir de aquí ya estamos en Deciding: solo el apagado del Host (stoppingToken) puede cancelar.
        await log.AddTransitionAsync(context, run, run.StartDeciding(), stoppingToken).ConfigureAwait(false);
        var decisionStartedAt = log.UtcNow;
        var decisionStage = new RunStageRecord
        {
            Id = Guid.CreateVersion7(decisionStartedAt),
            RunId = run.Id,
            Kind = StageKind.Decision,
            Round = round,
            Reviewer = null,
            StartedAt = decisionStartedAt,
        };
        context.Stages.Add(decisionStage);

        var protectedPaths = config.ProtectedPaths.IsDefault
            ? EffectiveConfig.DefaultProtectedPaths
            : config.ProtectedPaths;
        var diffLines = MergePolicy.CountDiffLines(diff.UnifiedDiff);
        var policyResult = MergePolicy.Classify(new PolicyInput(
            diff.ChangedFiles,
            diffLines,
            protectedPaths,
            roundVerdicts.Select(static v => v.Verdict.Decision).ToArray(),
            round));

        await log.AddAsync(
            context,
            run.Id,
            new RiskClassified(policyResult.RiskClass, diffLines, policyResult.MatchedProtectedPaths),
            stoppingToken,
            stageId: decisionStage.Id).ConfigureAwait(false);
        await log.SaveAndPublishAsync(context, stoppingToken).ConfigureAwait(false);

        var verdictRecords = await context.Verdicts
            .AsNoTracking()
            .Where(v => v.RunId == run.Id)
            .OrderBy(v => v.Round)
            .ThenBy(v => v.Reviewer)
            .ToListAsync(stoppingToken)
            .ConfigureAwait(false);

        var verdicts = verdictRecords
            .Select(r => new RunVerdict(
                r.StageId,
                r.Round,
                r.Reviewer,
                r.Decision,
                r.Summary,
                VerdictFindingsSerializer.Deserialize(r.FindingsJson),
                r.Model,
                r.Attempts,
                r.CreatedAt))
            .ToArray();

        var runCost = await budgetLedger.GetRunCostAsync(run.Id, stoppingToken).ConfigureAwait(false);
        var title = PullRequestBodyBuilder.BuildTitle(issueNumber, spec.Problem, log.Redactor, policyResult);
        var commitMessage = PullRequestBodyBuilder.BuildCommitMessage(run.Id, issueNumber, spec.Problem, log.Redactor);
        var body = PullRequestBodyBuilder.BuildBody(
            run.Id,
            run.EventId,
            owner,
            repo,
            issueNumber,
            spec,
            verdicts,
            testReport,
            log.Redactor,
            policy: policyResult,
            cost: runCost);

        var request = new PullRequestRequest(run.Id, owner, repo, run.BaseSha, title, body, commitMessage);

        PullRequestInfo pr;
        try
        {
            pr = await pullRequestPublisher.PublishAsync(request, stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is PullRequestPublishException or GitOperationBlockedException or RepositoryPreparationException or GitHubUnavailableException)
        {
            decisionStage.CompletedAt = log.UtcNow;
            await FailAsync(context, run, FailureReason.StageError, Truncate(PullRequestFailedPrefix + ex.Message), stoppingToken).ConfigureAwait(false);
            return;
        }

        // Una vez creado el PR en GitHub, completar con CancellationToken.None para no dejar la Ejecución atascada en Deciding.
        decisionStage.CompletedAt = log.UtcNow;
        await log.AddAsync(
            context,
            run.Id,
            new PullRequestOpened(pr.Number, pr.Url, pr.BranchName, pr.CommitSha),
            CancellationToken.None,
            stageId: decisionStage.Id).ConfigureAwait(false);
        await log.AddTransitionAsync(context, run, run.CompletePullRequest(), CancellationToken.None).ConfigureAwait(false);
        await log.SaveAndPublishAsync(context, CancellationToken.None).ConfigureAwait(false);

        // AD-16 paso 7: aviso terminal en la issue de origen (solo tras confirmar PullRequestCreated en BD).
        await NotifyTerminalAsync(run, pr, policyResult, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task FailAsync(PipelineDbContext context, PipelineRun run, FailureReason reason, string detail, CancellationToken cancellationToken)
    {
        var change = run.Fail(reason, detail);
        await log.AddTransitionAsync(context, run, change, cancellationToken).ConfigureAwait(false);
        await log.SaveAndPublishAsync(context, cancellationToken).ConfigureAwait(false);

        // AD-16 paso 7: NotAutomatable publica su comentario específico en AnalyzeAsync (Historia 1.9); el resto publica RunFailed aquí.
        if (reason != FailureReason.NotAutomatable)
        {
            await NotifyTerminalAsync(run, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    private static string Truncate(string value) => TextLimits.Truncate(value, MaxDetailLength);
}
