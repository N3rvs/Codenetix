namespace CodeNetix.Contracts;

public enum SentryContextStatus
{
    /// <summary>El Evento de la Ejecución no viene de una incidencia de Sentry: no se consulta Sentry.</summary>
    NotApplicable,

    /// <summary>Contexto obtenido: <see cref="SentryContextResult.Context"/> tiene valor.</summary>
    Attached,

    /// <summary>
    /// El Evento es de Sentry pero no hay contexto (Sentry sin configurar, no responde, rechaza el token o limita las
    /// peticiones): el análisis sigue sin él y <see cref="SentryContextResult.Reason"/> dice por qué.
    /// </summary>
    Unavailable,
}

/// <summary>
/// Contexto de una incidencia de Sentry para el Analista (FR-5, AD-5). <see cref="Context"/> es texto de terceros ya
/// saneado (sin cabeceras, cookies, usuario, IP, query strings ni variables), acotado y redactado; solo se usa con
/// <see cref="UntrustedText.RenderAsDataBlock"/>. <see cref="Reason"/> es un motivo en español sin secretos ni texto de Sentry.
/// </summary>
/// <param name="SentryIssueId">Id numérico de la incidencia (en <c>Attached</c> y <c>Unavailable</c>).</param>
/// <param name="Frames">Marcos de la traza incluidos.</param>
/// <param name="Breadcrumbs">Migas incluidas.</param>
/// <param name="Truncated">Algo se ha recortado por los límites de tamaño.</param>
public sealed record SentryContextResult(
    SentryContextStatus Status,
    string? SentryIssueId = null,
    UntrustedText? Context = null,
    int Frames = 0,
    int Breadcrumbs = 0,
    int Characters = 0,
    bool Truncated = false,
    string? Reason = null)
{
    public static readonly SentryContextResult NotApplicable = new(SentryContextStatus.NotApplicable);

    public static SentryContextResult Unavailable(string? sentryIssueId, string reason) =>
        new(SentryContextStatus.Unavailable, sentryIssueId, Reason: reason);
}

/// <summary>
/// Contexto de Sentry del Evento de una Ejecución (AD-16 paso 3). Lo implementa Ingestion (dueño de la Fuente Sentry y de
/// <c>ing_events</c>; único módulo que llama a la API de Sentry) y lo usa el orquestador de Pipeline una vez por Ejecución.
/// Nunca lanza por errores de Sentry: devuelve <see cref="SentryContextStatus.Unavailable"/> con el motivo.
/// </summary>
public interface ISentryContextProvider
{
    Task<SentryContextResult> GetForEventAsync(Guid eventId, CancellationToken cancellationToken);
}
