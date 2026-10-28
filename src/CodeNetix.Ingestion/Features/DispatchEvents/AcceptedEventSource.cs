using CodeNetix.Contracts;
using CodeNetix.Ingestion.Data;
using Microsoft.EntityFrameworkCore;

namespace CodeNetix.Ingestion.Features.DispatchEvents;

/// <summary>
/// Cara de Ingestion hacia el Dispatcher (AD-4): Eventos <c>Accepted</c> con <c>dispatched_at</c> nulo,
/// y su marcado como despachados (con su <c>run_id</c>) o como <c>Ignored</c>. También responde
/// <c>GET /api/events/{id}</c>.
/// </summary>
internal sealed class AcceptedEventSource(
    IDbContextFactory<IngestionDbContext> contextFactory,
    TimeProvider timeProvider) : IAcceptedEventSource, IEventReader
{
    public async Task<IReadOnlyList<PendingEvent>> GetPendingAsync(int max, IReadOnlyCollection<Guid> exclude, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(max);
        ArgumentNullException.ThrowIfNull(exclude);

        var excluded = exclude.ToList();
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await context.Events
            .AsNoTracking()
            .Where(e => e.Status == EventStatus.Accepted && e.DispatchedAt == null && !excluded.Contains(e.Id))
            .OrderBy(e => e.ReceivedAt)
            .ThenBy(e => e.Id)
            .Take(max)
            .Select(e => new { e.Id, e.ObjectKey, e.ReceivedAt })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(r => new PendingEvent(r.Id, r.ObjectKey, r.ReceivedAt))];
    }

    public async Task MarkDispatchedAsync(Guid eventId, Guid runId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await FindTrackedAsync(context, eventId, cancellationToken).ConfigureAwait(false);
        row.RunId = runId;
        row.DispatchedAt ??= timeProvider.GetUtcNow();
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkIgnoredAsync(Guid eventId, string reason, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await FindTrackedAsync(context, eventId, cancellationToken).ConfigureAwait(false);
        row.Status = EventStatus.Ignored;
        row.IgnoredReason = reason;
        row.DispatchedAt ??= timeProvider.GetUtcNow();
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<EventStatusInfo?> FindAsync(Guid eventId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await context.Events
            .AsNoTracking()
            .Where(e => e.Id == eventId)
            .Select(e => new { e.Id, e.Status, e.IgnoredReason, e.RunId })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return row is null ? null : new EventStatusInfo(row.Id, row.Status, row.IgnoredReason, row.RunId);
    }

    private static async Task<EventRecord> FindTrackedAsync(IngestionDbContext context, Guid eventId, CancellationToken cancellationToken) =>
        await context.Events.FirstOrDefaultAsync(e => e.Id == eventId, cancellationToken).ConfigureAwait(false)
        ?? throw new InvalidOperationException($"No existe el Evento {eventId}.");
}
