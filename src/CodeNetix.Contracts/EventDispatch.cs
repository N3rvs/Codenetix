namespace CodeNetix.Contracts;

/// <summary>Evento <see cref="EventStatus.Accepted"/> que todavía no ha despachado el Dispatcher.</summary>
public sealed record PendingEvent(Guid EventId, string ObjectKey, DateTimeOffset ReceivedAt);

/// <summary>
/// Eventos aceptados pendientes de convertirse en Ejecución (AD-4). Lo implementa Ingestion (dueño de
/// <c>ing_events</c>); solo lo usa el Dispatcher de Pipeline.
/// </summary>
public interface IAcceptedEventSource
{
    /// <summary>
    /// Hasta <paramref name="max"/> Eventos <c>Accepted</c> sin despachar, del más antiguo al más reciente,
    /// sin los de <paramref name="exclude"/> (p. ej. los que esperan un reintento): así los nuevos siempre son alcanzables.
    /// </summary>
    Task<IReadOnlyList<PendingEvent>> GetPendingAsync(int max, IReadOnlyCollection<Guid> exclude, CancellationToken cancellationToken);

    /// <summary>Marca el Evento como despachado en la Ejecución <paramref name="runId"/>. Idempotente.</summary>
    Task MarkDispatchedAsync(Guid eventId, Guid runId, CancellationToken cancellationToken);

    /// <summary>Pasa el Evento a <see cref="EventStatus.Ignored"/> con <paramref name="reason"/> y lo da por despachado.</summary>
    Task MarkIgnoredAsync(Guid eventId, string reason, CancellationToken cancellationToken);
}

/// <summary>Aviso al Dispatcher de que hay un Evento aceptado nuevo (lo implementa Pipeline; lo llama Ingestion).</summary>
public interface IEventDispatchSignal
{
    void Notify();
}

/// <summary>Estado de un Evento tal como lo ve la API; <see cref="RunId"/> solo si ya se ha despachado.</summary>
public sealed record EventStatusInfo(Guid EventId, EventStatus Status, string? Reason, Guid? RunId);

/// <summary>Consulta de Eventos (implementada por Ingestion).</summary>
public interface IEventReader
{
    Task<EventStatusInfo?> FindAsync(Guid eventId, CancellationToken cancellationToken);
}
