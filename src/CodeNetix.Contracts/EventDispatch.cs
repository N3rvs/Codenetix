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

/// <summary>
/// Aviso de que el Dispatcher de Pipeline ha terminado de orquestar una Ejecución: el orquestador ha vuelto (avisos en la
/// issue y liberación del Sandbox incluidos) o ha lanzado y la Ejecución se ha intentado cerrar. Lo usa
/// <c>codenetix action</c> para saber cuándo puede terminar el proceso. No debe bloquear ni lanzar.
/// </summary>
public interface IRunCompletionObserver
{
    void OnOrchestrationCompleted(Guid runId);
}

/// <summary>Estado de un Evento tal como lo ve la API; <see cref="RunId"/> solo si ya se ha despachado.</summary>
public sealed record EventStatusInfo(Guid EventId, EventStatus Status, string? Reason, Guid? RunId);

/// <summary>Consulta de Eventos (implementada por Ingestion).</summary>
public interface IEventReader
{
    Task<EventStatusInfo?> FindAsync(Guid eventId, CancellationToken cancellationToken);
}

/// <summary>Evento <see cref="EventStatus.Ignored"/> tal como lo muestra el Panel (FR-8: motivo visible).</summary>
public sealed record IgnoredEventInfo(Guid EventId, EventSource Source, string ObjectKey, string? Reason, DateTimeOffset ReceivedAt);

/// <summary>Eventos ignorados recientes (implementada por Ingestion).</summary>
public interface IIgnoredEventReader
{
    /// <summary>Hasta <paramref name="max"/> Eventos <c>Ignored</c>, del más reciente al más antiguo.</summary>
    Task<IReadOnlyList<IgnoredEventInfo>> ListIgnoredAsync(int max, CancellationToken cancellationToken);
}
