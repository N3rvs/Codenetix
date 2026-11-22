using CodeNetix.Contracts;
using CodeNetix.Pipeline.Data;
using CodeNetix.Pipeline.Domain;
using CodeNetix.Pipeline.Events;
using CodeNetix.Pipeline.Features.RunPipeline;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeNetix.Pipeline.Features.CancelRun;

/// <summary>
/// Implementación de <see cref="IRunControl"/> (FR-23, AD-2).
/// Si la Ejecución está activa en <see cref="RunOrchestrator"/>, dispara su token cooperativo a través de
/// <see cref="RunCancellationRegistry"/>; si está pendiente en base de datos en un estado cancelable,
/// adquiere el cerrojo por <c>runId</c>, aplica <see cref="PipelineRun.Cancel"/> directamente, publica
/// <see cref="StateChanged"/> y notifica <see cref="NotificationKind.RunCancelled"/> en la issue de GitHub.
/// </summary>
internal sealed partial class RunControl(
    IDbContextFactory<PipelineDbContext> contextFactory,
    RunCancellationRegistry registry,
    RunEventLog log,
    IIssueNotifier notifier,
    IRunEventSink events,
    ILogger<RunControl> logger) : IRunControl
{
    private static readonly TimeSpan TerminalNotificationTimeout = TimeSpan.FromSeconds(15);

    public async Task<CancelRunResult> RequestCancelAsync(string id, CancellationToken cancellationToken)
    {
        var found = await FindRunNoTrackingAsync(id, cancellationToken).ConfigureAwait(false);
        if (found is null)
        {
            return new CancelRunResult(
                CancelRunOutcome.NotFound,
                Detail: "No existe ninguna Ejecución con ese id (GUID o shortId de 8 caracteres).");
        }

        if (TryBuildConflict(found.Id, found.ShortId, found.State) is { } conflict)
        {
            return conflict;
        }

        if (registry.TryRequestCancel(found.Id, out var inDeciding))
        {
            return new CancelRunResult(CancelRunOutcome.Accepted, found.Id, found.ShortId, found.State);
        }

        if (inDeciding)
        {
            return BuildDecidingConflict(found.Id, found.ShortId);
        }

        // La Ejecución no está activa en memoria: serializar por runId con RunOrchestrator y otros POST /cancel.
        PipelineRun? cancelledInDb;
        using (await registry.AcquireRunGateAsync(found.Id, cancellationToken).ConfigureAwait(false))
        {
            if (registry.TryRequestCancel(found.Id, out var lateInDeciding))
            {
                return new CancelRunResult(CancelRunOutcome.Accepted, found.Id, found.ShortId, found.State);
            }

            if (lateInDeciding)
            {
                return BuildDecidingConflict(found.Id, found.ShortId);
            }

            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var tracked = await context.Runs.SingleOrDefaultAsync(r => r.Id == found.Id, cancellationToken).ConfigureAwait(false);
            if (tracked is null)
            {
                return new CancelRunResult(
                    CancelRunOutcome.NotFound,
                    Detail: "No existe ninguna Ejecución con ese id (GUID o shortId de 8 caracteres).");
            }

            if (TryBuildConflict(tracked.Id, tracked.ShortId, tracked.State) is { } reloadedConflict)
            {
                return reloadedConflict;
            }

            try
            {
                var change = tracked.Cancel();
                await log.AddTransitionAsync(context, tracked, change, CancellationToken.None).ConfigureAwait(false);
                await log.SaveAndPublishAsync(context, CancellationToken.None).ConfigureAwait(false);
                cancelledInDb = tracked;
            }
            catch (Exception ex) when (ex is DbUpdateConcurrencyException || (ex is DbUpdateException dbEx && RunEventLog.IsUniqueViolation(dbEx)))
            {
                // Carrera defensiva con otra escritura en BD: recargar estado actual y devolver 409 Conflict.
                var latest = await FindRunNoTrackingAsync(found.Id.ToString("D"), CancellationToken.None).ConfigureAwait(false);
                if (latest is not null && TryBuildConflict(latest.Id, latest.ShortId, latest.State) is { } latestConflict)
                {
                    return latestConflict;
                }

                return new CancelRunResult(
                    CancelRunOutcome.Conflict,
                    found.Id,
                    found.ShortId,
                    latest?.State ?? found.State,
                    $"La Ejecución #{found.ShortId} cambió de estado concurrentemente y no puede cancelarse.");
            }
        }

        // La notificación a GitHub ocurre fuera del cerrojo de runId para no bloquear otras operaciones.
        await NotifyCancelledAsync(cancelledInDb).ConfigureAwait(false);
        return new CancelRunResult(CancelRunOutcome.Accepted, cancelledInDb.Id, cancelledInDb.ShortId, RunState.Cancelled);
    }

    private async Task<PipelineRun?> FindRunNoTrackingAsync(string id, CancellationToken cancellationToken)
    {
        var trimmed = id?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        IQueryable<PipelineRun> query;
        if (Guid.TryParse(trimmed, out var runId))
        {
            query = context.Runs.Where(r => r.Id == runId);
        }
        else if (IsShortId(trimmed))
        {
            var shortId = trimmed.ToLowerInvariant();
            query = context.Runs.Where(r => r.ShortId == shortId);
        }
        else
        {
            return null;
        }

        return await query.AsNoTracking().FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task NotifyCancelledAsync(PipelineRun run)
    {
        if (!GitHubObjectKey.TryParse(run.ObjectKey, out var owner, out var repo, out var number))
        {
            return;
        }

        var body = TerminalNotificationComment.BuildRunCancelled(run.ShortId, log.Redactor);
        using var notifyCts = new CancellationTokenSource(TerminalNotificationTimeout);

        RunEvent outcome;
        try
        {
            var result = await notifier.NotifyAsync(
                run.Id,
                owner,
                repo,
                number,
                NotificationKind.RunCancelled,
                body,
                notifyCts.Token).ConfigureAwait(false);

            outcome = result.Status == NotificationStatus.Posted
                ? new IssueCommented(NotificationKind.RunCancelled, result.CommentUrl ?? string.Empty)
                : new NotificationFailed(NotificationKind.RunCancelled, log.Redactor.Redact(result.Error ?? "error desconocido"));
        }
        catch (OperationCanceledException)
        {
            outcome = new NotificationFailed(NotificationKind.RunCancelled, "tiempo agotado al publicar el aviso en GitHub");
        }
        catch (Exception ex)
        {
            outcome = new NotificationFailed(NotificationKind.RunCancelled, log.Redactor.Redact(StuckRunRecovery.ShortReason(ex)));
        }

        try
        {
            await events.AppendAsync(run.Id, null, outcome, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            LogNotificationEventNotRecorded(run.ShortId, log.Redactor.Redact(ex.Message));
        }
    }

    private static CancelRunResult? TryBuildConflict(Guid runId, string shortId, RunState state)
    {
        if (state == RunState.Deciding)
        {
            return BuildDecidingConflict(runId, shortId);
        }

        if (state.IsTerminal())
        {
            return new CancelRunResult(
                CancelRunOutcome.Conflict,
                runId,
                shortId,
                state,
                $"La Ejecución #{shortId} ya se encuentra en el estado terminal {state} y no puede cancelarse.");
        }

        return null;
    }

    private static CancelRunResult BuildDecidingConflict(Guid runId, string shortId) => new(
        CancelRunOutcome.Conflict,
        runId,
        shortId,
        RunState.Deciding,
        $"La Ejecución #{shortId} ya está en Deciding (publicando el PR) y no puede cancelarse.");

    private static bool IsShortId(string value) =>
        value.Length == 8 && value.All(c => char.IsAsciiHexDigit(c));

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se ha podido registrar el evento de notificación RunCancelled de la Ejecución {ShortId}: {Reason}.")]
    private partial void LogNotificationEventNotRecorded(string shortId, string reason);
}
