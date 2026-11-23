using System.Globalization;

namespace CodeNetix.Contracts;

/// <summary>
/// Entrega de un webhook de Sentry tal como llega a <c>:5001/webhooks/sentry</c> (FR-7): cabeceras relevantes y el cuerpo
/// crudo (la firma se calcula sobre estos bytes exactos). Todo es dato no confiable hasta verificar la firma.
/// </summary>
/// <param name="Resource"><c>Sentry-Hook-Resource</c> (<c>issue</c>, <c>installation</c>, <c>event_alert</c>…).</param>
/// <param name="RequestId"><c>Request-ID</c> (UUID de la entrega).</param>
/// <param name="Signature"><c>Sentry-Hook-Signature</c> (hex del HMAC SHA-256).</param>
/// <param name="ContentType"><c>Content-Type</c>.</param>
public sealed record SentryWebhookDelivery(
    string? Resource,
    string? RequestId,
    string? Signature,
    string? ContentType,
    ReadOnlyMemory<byte> Body);

public enum SentryWebhookOutcome
{
    /// <summary>Sin secreto configurado: el webhook de Sentry está desactivado.</summary>
    Disabled,

    /// <summary>Firma ausente, mal formada o incorrecta. Nada se procesa.</summary>
    InvalidSignature,

    /// <summary>Firma válida pero el cuerpo no es <c>application/json</c>.</summary>
    UnsupportedContentType,

    /// <summary>Firma válida pero el JSON es ilegible o le faltan datos obligatorios.</summary>
    InvalidPayload,

    /// <summary>Entrega legítima que no dispara trabajo (otro recurso o acción). Nada persistido.</summary>
    Ignored,

    /// <summary>El proyecto no tiene Repositorio objetivo: Evento <c>Ignored</c> persistido (o ya existente).</summary>
    Unmapped,

    /// <summary>Incidencia aplicable encolada para procesarla de forma asíncrona.</summary>
    Queued,

    /// <summary>La misma entrega (<c>Request-ID</c>) ya está en cola.</summary>
    Duplicate,

    /// <summary>La cola está llena: no se acepta.</summary>
    QueueFull,
}

/// <summary>Resultado síncrono de la recepción; <see cref="Reason"/> es un motivo en español sin secretos.</summary>
/// <param name="EventId">Solo en <see cref="SentryWebhookOutcome.Unmapped"/>: el Evento <c>Ignored</c>.</param>
public sealed record SentryWebhookReceipt(SentryWebhookOutcome Outcome, string? Reason = null, Guid? EventId = null);

/// <summary>
/// Recepción de webhooks de Sentry (AD-4, FR-7, FR-8). Verifica la firma, filtra y resuelve el Repositorio objetivo con la
/// Configuración de instancia, sin llamar a GitHub: un proyecto sin correspondencia queda como Evento <c>Ignored</c> y una
/// incidencia aplicable se encola (la issue de seguimiento y el Evento <c>Accepted</c> se crean de forma asíncrona).
/// </summary>
public interface ISentryWebhookIngestion
{
    /// <summary>Hay secreto configurado.</summary>
    bool Enabled { get; }

    /// <summary>Tamaño máximo del cuerpo que se acepta (bytes).</summary>
    int MaxBodyBytes { get; }

    /// <summary>Firma, filtro, correspondencia y encolado; solo toca <c>ing_events</c> para registrar un proyecto sin correspondencia.</summary>
    Task<SentryWebhookReceipt> ReceiveAsync(SentryWebhookDelivery delivery, CancellationToken cancellationToken);
}

/// <summary>
/// Claves de un Evento de Sentry (AD-4): <c>sentry:&lt;issueId&gt;</c> para una incidencia nueva y
/// <c>sentry:&lt;issueId&gt;:regression:&lt;n&gt;</c> para su regresión número <c>n</c>. Sin Repositorio objetivo, el objeto
/// del Evento es la propia incidencia (<c>sentry:&lt;issueId&gt;</c>).
/// </summary>
public static class SentryEventKey
{
    public const string Prefix = "sentry:";

    public const string RegressionSegment = ":regression:";

    /// <summary>Etiqueta de las issues de seguimiento (no es la etiqueta de disparo: el sondeo no las recoge).</summary>
    public const string TrackingLabel = "codenetix-sentry";

    public static string Format(string issueId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(issueId);
        return Prefix + issueId.Trim();
    }

    public static string FormatRegression(string issueId, int regression)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(regression);
        return string.Create(CultureInfo.InvariantCulture, $"{Format(issueId)}{RegressionSegment}{regression}");
    }

    /// <summary>Número de regresión de <paramref name="dedupKey"/> si es una clave de regresión de <paramref name="issueId"/>.</summary>
    public static bool TryParseRegression(string issueId, string dedupKey, out int regression)
    {
        regression = 0;
        var prefix = Format(issueId) + RegressionSegment;
        return dedupKey is not null
            && dedupKey.StartsWith(prefix, StringComparison.Ordinal)
            && int.TryParse(dedupKey.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out regression)
            && regression > 0;
    }
}

/// <summary>
/// Datos de una incidencia de Sentry para su issue de seguimiento en GitHub. Todo menos <see cref="Owner"/>,
/// <see cref="Repo"/> y <see cref="SentryIssueId"/> es texto no confiable que el módulo GitHub sanea antes de publicar.
/// </summary>
public sealed record SentryTrackingIssueRequest(
    string Owner,
    string Repo,
    string SentryIssueId,
    string? ShortId,
    string Title,
    string? Culprit,
    string? Level,
    string ProjectSlug,
    string? Permalink);

public enum SentryTrackingIssueStatus
{
    /// <summary>Ya existía una issue con el marcador de la incidencia.</summary>
    Found,

    /// <summary>Se ha creado la issue.</summary>
    Created,

    /// <summary>GitHub no responde (red, 5xx, rate limit): reintentar más tarde.</summary>
    Unavailable,

    /// <summary>GitHub rechaza la operación de forma definitiva (repositorio inexistente, sin permiso…).</summary>
    Rejected,
}

/// <param name="Number">Número de la issue en <c>Found</c> y <c>Created</c>.</param>
/// <param name="Failure">Motivo (sin secretos) en <c>Unavailable</c> y <c>Rejected</c>.</param>
public sealed record SentryTrackingIssueResult(SentryTrackingIssueStatus Status, long? Number, GitHubQueryFailure? Failure)
{
    public static SentryTrackingIssueResult Found(long number) => new(SentryTrackingIssueStatus.Found, number, null);

    public static SentryTrackingIssueResult Created(long number) => new(SentryTrackingIssueStatus.Created, number, null);

    public static SentryTrackingIssueResult Unavailable(GitHubQueryFailure failure) => new(SentryTrackingIssueStatus.Unavailable, null, failure);

    public static SentryTrackingIssueResult Rejected(GitHubQueryFailure failure) => new(SentryTrackingIssueStatus.Rejected, null, failure);
}

/// <summary>
/// Issue de seguimiento de una incidencia de Sentry en su Repositorio objetivo (AD-8: solo lo implementa el módulo GitHub).
/// Idempotente por <see cref="SentryTrackingIssueRequest.SentryIssueId"/>: busca la issue con su marcador (etiqueta
/// <see cref="SentryEventKey.TrackingLabel"/>) antes de crearla. Nunca lanza por errores de GitHub.
/// </summary>
public interface ISentryTrackingIssues
{
    Task<SentryTrackingIssueResult> EnsureAsync(SentryTrackingIssueRequest request, CancellationToken cancellationToken);
}
