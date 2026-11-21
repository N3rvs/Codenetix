using CodeNetix.Contracts;
using CodeNetix.Pipeline.Data;
using CodeNetix.Pipeline.Events;
using Microsoft.EntityFrameworkCore;

namespace CodeNetix.Pipeline.Features.RunPipeline;

/// <summary>
/// Red de seguridad del orquestador: evita que una Ejecución quede no terminal (y su objeto bloqueado) cuando
/// la orquestación lanza una excepción o el proceso cae entre crear la Ejecución y orquestarla.
/// </summary>
internal sealed class StuckRunRecovery(
    IDbContextFactory<PipelineDbContext> contextFactory,
    RunEventLog log,
    IIssueNotifier notifier,
    IRunEventSink events)
{
    /// <summary>Longitud máxima del motivo que se guarda en el detalle del fallo.</summary>
    public const int MaxReasonLength = 200;

    public const string DetailPrefix = "Error interno de orquestación: ";

    private static readonly TimeSpan NotificationTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Ejecuciones en <c>Received</c>: creadas pero nunca orquestadas.</summary>
    public async Task<IReadOnlyList<Guid>> GetReceivedRunIdsAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await context.Runs
            .Where(r => r.State == RunState.Received)
            .OrderBy(r => r.CreatedAt)
            .Select(r => r.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// En un contexto nuevo: si la Ejecución sigue no terminal, <c>Fail(StageError, detalle)</c> con su
    /// <c>StateChanged</c> en el mismo <c>SaveChanges</c>. El detalle se redacta al persistirse. Tras confirmar el
    /// fallo, publica el aviso <see cref="NotificationKind.RunFailed"/> si el origen es una issue de GitHub (AD-8, FR-19).
    /// </summary>
    public async Task FailIfActiveAsync(Guid runId, Exception error, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(error);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var run = await context.Runs.SingleOrDefaultAsync(r => r.Id == runId, cancellationToken).ConfigureAwait(false);
        if (run is null || run.State.IsTerminal())
        {
            return;
        }

        var change = run.Fail(FailureReason.StageError, DetailPrefix + ShortReason(error));
        await log.AddTransitionAsync(context, run, change, cancellationToken).ConfigureAwait(false);
        await log.SaveAndPublishAsync(context, cancellationToken).ConfigureAwait(false);

        if (GitHubObjectKey.TryParse(run.ObjectKey, out var owner, out var repo, out var number))
        {
            await NotifyRunFailedAsync(run.Id, run.ShortId, owner, repo, number, run.FailureDetail).ConfigureAwait(false);
        }
    }

    private async Task NotifyRunFailedAsync(Guid runId, string shortId, string owner, string repo, long number, string? detail)
    {
        using var notifyCts = new CancellationTokenSource(NotificationTimeout);
        var body = TerminalNotificationComment.BuildRunFailed(shortId, FailureReason.StageError, detail, log.Redactor);

        RunEvent outcome;
        try
        {
            var result = await notifier.NotifyAsync(runId, owner, repo, number, NotificationKind.RunFailed, body, notifyCts.Token).ConfigureAwait(false);
            outcome = result.Status == NotificationStatus.Posted
                ? new IssueCommented(NotificationKind.RunFailed, result.CommentUrl ?? string.Empty)
                : new NotificationFailed(NotificationKind.RunFailed, log.Redactor.Redact(result.Error ?? "error desconocido"));
        }
        catch (OperationCanceledException)
        {
            outcome = new NotificationFailed(NotificationKind.RunFailed, "tiempo agotado al publicar el aviso en GitHub");
        }
        catch (Exception ex)
        {
            outcome = new NotificationFailed(NotificationKind.RunFailed, log.Redactor.Redact(ShortReason(ex)));
        }

        try
        {
            await events.AppendAsync(runId, null, outcome, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // La recuperación nunca debe relanzar al intentar registrar el resultado de la notificación.
        }
    }

    /// <summary>Mensaje de la excepción en una línea y como mucho <see cref="MaxReasonLength"/> caracteres.</summary>
    public static string ShortReason(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var message = string.Join(' ', error.Message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (message.Length == 0)
        {
            message = error.GetType().Name;
        }

        return message.Length <= MaxReasonLength ? message : string.Concat(message.AsSpan(0, MaxReasonLength - 1), "…");
    }
}
