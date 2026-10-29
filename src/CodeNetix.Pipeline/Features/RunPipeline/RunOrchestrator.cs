using CodeNetix.Contracts;
using CodeNetix.Pipeline.Data;
using CodeNetix.Pipeline.Domain;
using CodeNetix.Pipeline.Events;
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
/// <item>Puente provisional hasta la Épica 2: tras la Spec, <c>Failed(StageError, <see cref="Detail"/>)</c>.</item>
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
    IIssueNotifier notifier,
    IRunEventSink events,
    ISandboxManager sandboxes,
    IOptions<InstanceLimits> limits,
    ILogger<RunOrchestrator> logger) : IRunOrchestrator
{
    /// <summary>Detalle del puente provisional tras una Spec válida (hasta que exista el Programador, Épica 2).</summary>
    public const string Detail = "Pruebas no disponibles";

    public const string IssueUnreadablePrefix = "no se pudo leer la issue: ";

    public const string ProviderErrorPrefix = "Proveedor: ";

    public const string SandboxFailedPrefix = "no se pudo crear el Sandbox: ";

    /// <summary>Longitud máxima del detalle de un <c>codenetix.yml</c> inválido.</summary>
    public const int MaxDetailLength = 1000;

    public async Task RunAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var run = await context.Runs.SingleAsync(r => r.Id == runId, cancellationToken).ConfigureAwait(false);
        if (run.State.IsTerminal())
        {
            return;
        }

        ISandboxSession? sandbox = null;
        var completed = false;
        try
        {
            if (run.State == RunState.Received)
            {
                await PrepareRepositoryAsync(context, run, cancellationToken).ConfigureAwait(false);
            }

            if (run.State == RunState.Analyzing)
            {
                sandbox = await CreateSandboxAsync(context, run, cancellationToken).ConfigureAwait(false);
            }

            if (run.State == RunState.Analyzing)
            {
                await AnalyzeAsync(context, run, cancellationToken).ConfigureAwait(false);
            }

            if (run.State == RunState.Analyzing)
            {
                await log.AddTransitionAsync(context, run, run.StartCoding(), cancellationToken).ConfigureAwait(false);
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            if (run.State == RunState.Coding)
            {
                await CodeAsync(context, run, sandbox, cancellationToken).ConfigureAwait(false);

            }

            completed = true;
        }
        finally
        {
            // AD-16 paso 8: el Sandbox se libera siempre (cualquier Estado, también al cancelar) y antes del árbol.
            if (sandbox is not null)
            {
                await ReleaseSandboxAsync(run.Id, sandbox).ConfigureAwait(false);
            }

            // Una cancelación (apagado) deja la Ejecución en Received para reanudarla; aun así no se deja el árbol:
            // la reanudación vuelve a clonar desde cero.
            if (!completed || run.State.IsTerminal())
            {
                await workspace.DeleteAsync(runId, CancellationToken.None).ConfigureAwait(false);
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
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
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
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch
        {
            // Sin evento persistido no hay quien la libere: se libera aquí.
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Libera la sesión (nunca lanza) y registra <see cref="SandboxReleased"/>; un fallo al registrarlo solo se anota en el log.</summary>
    private async Task ReleaseSandboxAsync(Guid runId, ISandboxSession sandbox)
    {
        await sandbox.DisposeAsync().ConfigureAwait(false);
        try
        {
            await events.AppendAsync(runId, null, new SandboxReleased(), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            LogSandboxReleasedNotRecorded(RunShortId.Of(runId), ex.Message);
        }
    }

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
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                break;

            case AnalysisResult.NotAutomatable rejected:
                // AnalysisRejected y Failed(NotAutomatable) en el mismo SaveChanges; nunca StartCoding ni run_specs.
                await log.AddAsync(context, run.Id, new AnalysisRejected(rejected.Category, rejected.Attempts, rejected.Model), cancellationToken).ConfigureAwait(false);
                await FailAsync(context, run, FailureReason.NotAutomatable, Truncate($"{rejected.Category.ToWireName()}: {rejected.Explanation}"), cancellationToken).ConfigureAwait(false);

                // Solo tras el commit: el aviso en la issue (AD-8). Su resultado no cambia el Estado terminal.
                await NotifyNotAutomatableAsync(run, owner, repo, number, rejected, cancellationToken).ConfigureAwait(false);
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
    private async Task NotifyNotAutomatableAsync(
        PipelineRun run,
        string owner,
        string repo,
        long number,
        AnalysisResult.NotAutomatable rejected,
        CancellationToken cancellationToken)
    {
        const NotificationKind Kind = NotificationKind.NotAutomatable;
        var body = NotAutomatableComment.Build(run.ShortId, rejected.Category, rejected.Explanation);

        RunEvent outcome;
        try
        {
            var result = await notifier.NotifyAsync(run.Id, owner, repo, number, Kind, body, cancellationToken).ConfigureAwait(false);
            outcome = result.Status == NotificationStatus.Posted
                ? new IssueCommented(Kind, result.CommentUrl ?? string.Empty)
                : new NotificationFailed(Kind, result.Error ?? "error desconocido");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            outcome = new NotificationFailed(Kind, StuckRunRecovery.ShortReason(ex));
        }

        await events.AppendAsync(run.Id, null, outcome, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// AD-16 paso 4: bucle de intentos del Programador. Tras un intento exitoso, hoy se detiene en el puente
    /// provisional a la historia 2.4 (Failed(StageError, "Pruebas no disponibles")).
    /// </summary>
    private async Task CodeAsync(PipelineDbContext context, PipelineRun run, ISandboxSession? sandbox, CancellationToken cancellationToken)
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

        string? feedback = null;
        while (run.State == RunState.Coding && run.CodingAttempts < config.MaxCodingAttempts)
        {
            var attemptNumber = run.CodingAttempts + 1;
            var input = new CodingInput(run.Id, attemptNumber, issue.Title, issue.Body, spec, config, sandbox, feedback);
            var result = await coder.RunAsync(input, cancellationToken).ConfigureAwait(false);

            switch (result)
            {
                case CodingResult.Completed:
                    run.CompleteCodingAttempt();
                    await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                    // Puente provisional hasta la historia 2.4 (ITestRunner):
                    await FailAsync(context, run, FailureReason.StageError, Detail, cancellationToken).ConfigureAwait(false);
                    return;

                case CodingResult.Failed failed:
                    var attemptFailedEvent = run.RecordFailedCodingAttempt(failed.Reason);
                    await log.AddAsync(context, run.Id, attemptFailedEvent, cancellationToken).ConfigureAwait(false);
                    await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                    feedback = failed.Reason;
                    if (run.CodingAttempts >= config.MaxCodingAttempts)
                    {
                        await FailAsync(context, run, FailureReason.TestsFailed, Truncate(failed.Reason), cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    break;

                default:
                    throw new InvalidOperationException($"Resultado del Programador desconocido: {result.GetType().Name}.");
            }
        }

        if (run.State == RunState.Coding)
        {
            await FailAsync(context, run, FailureReason.TestsFailed, "reintentos agotados", cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task FailAsync(PipelineDbContext context, PipelineRun run, FailureReason reason, string detail, CancellationToken cancellationToken)
    {
        var change = run.Fail(reason, detail);
        await log.AddTransitionAsync(context, run, change, cancellationToken).ConfigureAwait(false);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Truncate(string value) => TextLimits.Truncate(value, MaxDetailLength);
}
