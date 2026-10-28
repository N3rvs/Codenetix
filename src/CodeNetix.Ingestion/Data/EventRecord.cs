using CodeNetix.Contracts;

namespace CodeNetix.Ingestion.Data;

/// <summary>Fila de <c>ing_events</c>: la forma persistida de un <see cref="EventEnvelope"/>.</summary>
internal sealed class EventRecord
{
    public Guid Id { get; set; }

    public EventSource Source { get; set; }

    public string ObjectKey { get; set; } = string.Empty;

    public string DedupKey { get; set; } = string.Empty;

    public string? Actor { get; set; }

    public string PayloadJson { get; set; } = string.Empty;

    public EventStatus Status { get; set; }

    public string? IgnoredReason { get; set; }

    public DateTimeOffset ReceivedAt { get; set; }

    /// <summary>Cuándo lo recogió el Dispatcher (Ejecución creada o Evento ignorado); <c>null</c> si está pendiente.</summary>
    public DateTimeOffset? DispatchedAt { get; set; }

    /// <summary>Ejecución creada a partir de este Evento, si la hay.</summary>
    public Guid? RunId { get; set; }

    public static EventRecord FromEnvelope(EventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return new EventRecord
        {
            Id = envelope.Id,
            Source = envelope.Source,
            ObjectKey = envelope.ObjectKey,
            DedupKey = envelope.DedupKey,
            Actor = envelope.Actor,
            PayloadJson = envelope.Payload,
            Status = envelope.Status,
            IgnoredReason = envelope.IgnoredReason,
            ReceivedAt = envelope.ReceivedAt,
        };
    }
}
