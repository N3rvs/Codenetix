using CodeNetix.Contracts;
using CodeNetix.Pipeline.Data;
using CodeNetix.Pipeline.Domain;
using CodeNetix.Pipeline.Events;
using Microsoft.EntityFrameworkCore;

namespace CodeNetix.Pipeline.Features.DispatchEvents;

internal enum DispatchOutcome
{
    /// <summary>Se ha creado una Ejecución nueva.</summary>
    Created,

    /// <summary>La Ejecución ya existía (reintento tras un corte entre crearla y marcar el Evento).</summary>
    AlreadyDispatched,

    /// <summary>El objeto tiene una Ejecución no terminal: el Evento pasa a <c>Ignored</c>.</summary>
    ObjectBusy,
}

internal sealed record DispatchResult(DispatchOutcome Outcome, Guid? RunId);

/// <summary>Despacho de un Evento aceptado (interfaz para poder sustituirlo en pruebas).</summary>
internal interface IRunDispatchHandler
{
    Task<DispatchResult> DispatchAsync(PendingEvent pending, CancellationToken cancellationToken);
}

/// <summary>Ajustes del <see cref="Dispatcher"/> (fijos en producción; las pruebas los acortan).</summary>
internal sealed class DispatcherOptions
{
    /// <summary>Sondeo cuando no llega ningún aviso.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Espera antes de reintentar un Evento que ha fallado.</summary>
    public TimeSpan RetryBackoff { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Intentos fallidos tras los que el Evento pasa a <c>Ignored</c>.</summary>
    public int MaxAttempts { get; init; } = 5;
}

/// <summary>
/// Convierte un Evento aceptado en una Ejecución (AD-4): como mucho una no terminal por objeto (lo garantiza
/// el índice UNIQUE parcial) y como mucho una por Evento (índice UNIQUE sobre <c>event_id</c>). La Ejecución
/// nace en <c>Received</c> con <c>RunCreated</c> y <c>StateChanged(null → Received)</c> en el mismo
/// <c>SaveChanges</c>; después se marca el Evento como despachado.
/// </summary>
internal sealed class RunDispatchHandler(
    IDbContextFactory<PipelineDbContext> contextFactory,
    RunEventLog log,
    IAcceptedEventSource events) : IRunDispatchHandler
{
    private const int MaxAttempts = 3;

    public static string BusyReason(string shortId) => $"ya hay una Ejecución en curso para este objeto ({shortId})";

    public async Task<DispatchResult> DispatchAsync(PendingEvent pending, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pending);

        for (var attempt = 1; ; attempt++)
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

            var existing = await context.Runs
                .Where(r => r.EventId == pending.EventId)
                .Select(r => (Guid?)r.Id)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            if (existing is { } existingId)
            {
                await events.MarkDispatchedAsync(pending.EventId, existingId, cancellationToken).ConfigureAwait(false);
                return new DispatchResult(DispatchOutcome.AlreadyDispatched, existingId);
            }

            var busy = await context.Runs
                .Where(r => r.ObjectKey == pending.ObjectKey
                    && r.State != RunState.PullRequestCreated
                    && r.State != RunState.Failed
                    && r.State != RunState.Cancelled)
                .Select(r => r.ShortId)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            if (busy is not null)
            {
                await events.MarkIgnoredAsync(pending.EventId, BusyReason(busy), cancellationToken).ConfigureAwait(false);
                return new DispatchResult(DispatchOutcome.ObjectBusy, null);
            }

            var (run, created, initial) = PipelineRun.Create(pending.EventId, pending.ObjectKey, log.UtcNow);
            context.Runs.Add(run);
            await log.AddAsync(context, run.Id, created, cancellationToken).ConfigureAwait(false);
            await log.AddAsync(context, run.Id, initial, cancellationToken).ConfigureAwait(false);
            try
            {
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateException ex) when (attempt < MaxAttempts && RunEventLog.IsUniqueViolation(ex))
            {
                // Carrera por el objeto o por el Evento, o colisión de shortId: se vuelve a evaluar desde el principio.
                continue;
            }

            await events.MarkDispatchedAsync(pending.EventId, run.Id, cancellationToken).ConfigureAwait(false);
            return new DispatchResult(DispatchOutcome.Created, run.Id);
        }
    }
}
