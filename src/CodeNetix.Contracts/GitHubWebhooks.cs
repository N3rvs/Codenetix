namespace CodeNetix.Contracts;

/// <summary>
/// Entrega de un webhook de GitHub tal como llega a <c>:5001/webhooks/github</c> (FR-7): cabeceras relevantes y el cuerpo
/// crudo (la firma se calcula sobre estos bytes exactos). Todo es dato no confiable hasta verificar la firma.
/// </summary>
/// <param name="EventName"><c>X-GitHub-Event</c>.</param>
/// <param name="DeliveryId"><c>X-GitHub-Delivery</c>.</param>
/// <param name="Signature256"><c>X-Hub-Signature-256</c> (<c>sha256=&lt;hex&gt;</c>).</param>
/// <param name="ContentType"><c>Content-Type</c>.</param>
public sealed record GitHubWebhookDelivery(
    string? EventName,
    string? DeliveryId,
    string? Signature256,
    string? ContentType,
    ReadOnlyMemory<byte> Body);

public enum GitHubWebhookOutcome
{
    /// <summary>Sin secreto configurado: el webhook de GitHub está desactivado.</summary>
    Disabled,

    /// <summary>Firma ausente, mal formada o incorrecta. Nada se procesa.</summary>
    InvalidSignature,

    /// <summary>Firma válida pero el cuerpo no es <c>application/json</c>.</summary>
    UnsupportedContentType,

    /// <summary>Firma válida pero el JSON es ilegible o le faltan datos obligatorios.</summary>
    InvalidPayload,

    /// <summary><c>ping</c> de GitHub al crear o probar el webhook.</summary>
    Pong,

    /// <summary>Entrega legítima que no dispara trabajo (otro evento, acción, etiqueta, repositorio…).</summary>
    Ignored,

    /// <summary>Disparo aplicable encolado para procesarlo de forma asíncrona.</summary>
    Queued,

    /// <summary>La misma entrega (<c>X-GitHub-Delivery</c>) ya está en cola.</summary>
    Duplicate,

    /// <summary>La cola está llena: no se acepta (GitHub permite reentregar).</summary>
    QueueFull,
}

/// <summary>Resultado síncrono de la recepción; <see cref="Reason"/> es un motivo en español sin secretos.</summary>
public sealed record GitHubWebhookReceipt(GitHubWebhookOutcome Outcome, string? Reason = null);

/// <summary>
/// Recepción de webhooks de GitHub (AD-4, FR-7, FR-26). Verifica la firma, filtra y encola sin llamar a GitHub, de modo
/// que el endpoint responde enseguida; el procesado (evento <c>labeled</c>, permiso y persistencia) es asíncrono.
/// </summary>
public interface IGitHubWebhookIngestion
{
    /// <summary>Hay secreto configurado.</summary>
    bool Enabled { get; }

    /// <summary>Tamaño máximo del cuerpo que se acepta (bytes).</summary>
    int MaxBodyBytes { get; }

    /// <summary>Sin E/S: firma, filtro y encolado.</summary>
    GitHubWebhookReceipt Receive(GitHubWebhookDelivery delivery);
}
