using CodeNetix.Contracts;
using CodeNetix.Pipeline.Data;
using CodeNetix.Pipeline.Events;
using CodeNetix.Pipeline.Features.CancelRun;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeNetix.Pipeline.Features.RunPipeline;

/// <summary>
/// Reconciliación de Ejecuciones al arrancar el Host (AD-18 paso 3, FR-24). En la v1 no se reanuda ninguna Etapa:
/// <list type="number">
/// <item>toda Ejecución no terminal (<c>Received</c>, <c>Analyzing</c>, <c>Coding</c>, <c>Reviewing</c> o
/// <c>Deciding</c>) pasa a <c>Failed(HostRestarted)</c> con su <see cref="StateChanged"/> en el mismo
/// <c>SaveChanges</c> (lo que libera el índice único activo del objeto). Cada cierre va bajo el cerrojo por
/// <c>runId</c> de <see cref="RunCancellationRegistry"/> y se reintenta (<see cref="MaxCloseAttempts"/>) releyendo la
/// fila: si otra escritura la dejó terminal se respeta; si sigue activa tras los intentos, se informa en
/// <see cref="RunReconciliationResult.StillActive"/> (el arranque no pasa a <c>Ready</c>);</item>
/// <item>después se publica el aviso <see cref="NotificationKind.RunFailed"/> de cada Ejecución
/// <c>Failed(HostRestarted)</c> que no tenga aún <see cref="IssueCommented"/> (las de ahora y las de arranques
/// anteriores que se quedaron sin aviso: caída entre cerrar y avisar, plazo agotado…), como mucho
/// <see cref="MaxNotificationAttempts"/> veces por Ejecución. <see cref="IIssueNotifier"/> es idempotente por
/// (<c>runId</c>, <c>kind</c>) y adopta un comentario ya publicado por su marcador, así que repetir no duplica.
/// Plazo de <see cref="NotificationTimeout"/> por aviso y <see cref="NotificationsBudget"/> en total; un fallo de
/// GitHub queda como <see cref="NotificationFailed"/> y nunca relanza.</item>
/// </list>
/// Las Ejecuciones terminales (<c>PullRequestCreated</c>, <c>Failed</c>, <c>Cancelled</c>) no cambian de estado.
/// </summary>
internal sealed partial class RunStartupReconciler(
    IDbContextFactory<PipelineDbContext> contextFactory,
    RunEventLog log,
    RunCancellationRegistry registry,
    IIssueNotifier notifier,
    IRunEventSink events,
    ILogger<RunStartupReconciler> logger) : IRunStartupReconciler
{
    public const string HostRestartedDetail = "el host se reinició mientras la Ejecución estaba en curso";

    public const string TimedOutReason = "tiempo agotado al publicar el aviso en GitHub";

    /// <summary>Intentos de cerrar cada Ejecución.</summary>
    internal const int MaxCloseAttempts = 3;

    /// <summary>Avisos fallidos tras los que una Ejecución deja de reintentarse en cada arranque.</summary>
    internal const int MaxNotificationAttempts = 3;

    /// <summary>Plazo de cada aviso en GitHub.</summary>
    internal static readonly TimeSpan NotificationTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Plazo total de todos los avisos: el arranque nunca espera más por GitHub.</summary>
    internal static readonly TimeSpan NotificationsBudget = TimeSpan.FromSeconds(30);

    public async Task<RunReconciliationResult> ReconcileNonTerminalRunsAsync(CancellationToken cancellationToken)
    {
        var closed = 0;
        foreach (var runId in await NonTerminalRunIdsAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await CloseAsync(runId, cancellationToken).ConfigureAwait(false))
            {
                closed++;
            }
        }

        var stillActive = (await NonTerminalRunIdsAsync(cancellationToken).ConfigureAwait(false)).Count;
        var notified = await NotifyPendingAsync(cancellationToken).ConfigureAwait(false);
        return new RunReconciliationResult(closed, stillActive, notified);
    }

    private async Task<List<Guid>> NonTerminalRunIdsAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await context.Runs
            .Where(r => r.State != RunState.PullRequestCreated
                && r.State != RunState.Failed
                && r.State != RunState.Cancelled)
            .OrderBy(r => r.CreatedAt)
            .Select(r => r.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Cierra la Ejecución como <c>Failed(HostRestarted)</c>; <c>true</c> si la cerró este arranque.</summary>
    private async Task<bool> CloseAsync(Guid runId, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using (await registry.AcquireRunGateAsync(runId, cancellationToken).ConfigureAwait(false))
                {
                    // Contexto nuevo en cada intento: tras un conflicto se relee el estado real de la fila.
                    await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                    var run = await context.Runs.SingleOrDefaultAsync(r => r.Id == runId, cancellationToken).ConfigureAwait(false);
                    if (run is null || run.State.IsTerminal())
                    {
                        if (attempt > 1)
                        {
                            LogRunChangedConcurrently(RunShortId.Of(runId));
                        }

                        return false;
                    }

                    var previousState = run.State;
                    var change = run.Fail(FailureReason.HostRestarted, HostRestartedDetail);
                    await log.AddTransitionAsync(context, run, change, cancellationToken).ConfigureAwait(false);
                    await log.SaveAndPublishAsync(context, cancellationToken).ConfigureAwait(false);
                    LogRunReconciled(run.ShortId, previousState);
                    return true;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (attempt >= MaxCloseAttempts)
                {
                    // No impide el arranque, pero StillActive > 0 mantiene la instancia fuera de Ready.
                    LogRunReconcileFailed(ex, RunShortId.Of(runId), attempt);
                    return false;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Avisos <c>RunFailed</c> de las Ejecuciones cerradas por reinicio que aún no constan como publicados.</summary>
    private async Task<int> NotifyPendingAsync(CancellationToken cancellationToken)
    {
        List<PendingNotice> pending;
        await using (var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            pending = await context.Runs
                .Where(r => r.State == RunState.Failed && r.FailureReason == FailureReason.HostRestarted)
                .Where(r => !context.Events.Any(e => e.RunId == r.Id && e.Type == nameof(IssueCommented)))
                .Where(r => context.Events.Count(e => e.RunId == r.Id && e.Type == nameof(NotificationFailed)) < MaxNotificationAttempts)
                .OrderBy(r => r.CreatedAt)
                .Select(r => new PendingNotice(r.Id, r.ShortId, r.ObjectKey, r.FailureDetail))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(NotificationsBudget);
        var notified = 0;
        foreach (var run in pending)
        {
            if (GitHubObjectKey.TryParse(run.ObjectKey, out var owner, out var repo, out var number))
            {
                await NotifyRunFailedAsync(run, owner, repo, number, budget.Token).ConfigureAwait(false);
                notified++;
            }
        }

        return notified;
    }

    private async Task NotifyRunFailedAsync(PendingNotice run, string owner, string repo, long number, CancellationToken budget)
    {
        RunEvent outcome;
        try
        {
            using var notifyCts = CancellationTokenSource.CreateLinkedTokenSource(budget);
            notifyCts.CancelAfter(NotificationTimeout);
            var body = TerminalNotificationComment.BuildRunFailed(run.ShortId, FailureReason.HostRestarted, run.Detail, log.Redactor);
            var result = await notifier.NotifyAsync(run.Id, owner, repo, number, NotificationKind.RunFailed, body, notifyCts.Token).ConfigureAwait(false);
            outcome = result.Status == NotificationStatus.Posted
                ? new IssueCommented(NotificationKind.RunFailed, result.CommentUrl ?? string.Empty)
                : new NotificationFailed(NotificationKind.RunFailed, log.Redactor.Redact(result.Error ?? "error desconocido"));
        }
        catch (OperationCanceledException)
        {
            outcome = new NotificationFailed(NotificationKind.RunFailed, TimedOutReason);
        }
        catch (Exception ex)
        {
            outcome = new NotificationFailed(NotificationKind.RunFailed, log.Redactor.Redact(StuckRunRecovery.ShortReason(ex)));
        }

        try
        {
            await events.AppendAsync(run.Id, null, outcome, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogNotificationEventFailed(run.ShortId, StuckRunRecovery.ShortReason(ex));
        }
    }

    private sealed record PendingNotice(Guid Id, string ShortId, string ObjectKey, string? Detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "La Ejecución {ShortId} quedó en {PreviousState} al reiniciarse el host; se cierra como Failed(HostRestarted).")]
    private partial void LogRunReconciled(string shortId, RunState previousState);

    [LoggerMessage(Level = LogLevel.Information, Message = "La Ejecución {ShortId} cambió de estado durante la reconciliación de arranque; se respeta su estado terminal.")]
    private partial void LogRunChangedConcurrently(string shortId);

    [LoggerMessage(Level = LogLevel.Error, Message = "No se ha podido cerrar la Ejecución {ShortId} durante la reconciliación de arranque tras {Attempts} intento(s).")]
    private partial void LogRunReconcileFailed(Exception exception, string shortId, int attempts);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se ha podido registrar el resultado del aviso de reinicio de la Ejecución {ShortId}: {Reason}.")]
    private partial void LogNotificationEventFailed(string shortId, string reason);
}
