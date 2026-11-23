using System.Text.RegularExpressions;

namespace CodeNetix.Ingestion.Features.ReadSentryContext;

/// <summary>
/// API de Sentry (historia 7.4, OQ-5). Las rellena Host desde <c>CODENETIX__Sentry__AuthToken</c>,
/// <c>CODENETIX__Sentry__Organization</c> y <c>CODENETIX__Sentry__BaseUrl</c>. Sin token u organización, el contexto de
/// Sentry y <c>codenetix fix &lt;url-de-sentry&gt;</c> están desactivados.
/// </summary>
public sealed partial class SentryApiOptions
{
    public const string DefaultBaseUrl = "https://sentry.io";

    /// <summary>Tamaño máximo de una respuesta de la API (un evento con muchos marcos puede ser grande).</summary>
    public const int DefaultMaxResponseBytes = 4 * 1024 * 1024;

    public const string NotConfiguredReason =
        "Sentry no está configurado: define CODENETIX__Sentry__AuthToken y CODENETIX__Sentry__Organization";

    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(10);

    public static readonly TimeSpan DefaultTotalTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Token de API (recortado); <c>null</c> = desactivado. Nunca se registra ni sale de <see cref="BaseUrl"/>.</summary>
    public string? AuthToken { get; set; }

    /// <summary>Slug de la organización, en minúsculas.</summary>
    public string? Organization { get; set; }

    /// <summary>Base de la API (sin <c>/api/0</c>): <c>https://sentry.io</c> o la de un Sentry autoalojado.</summary>
    public string BaseUrl { get; set; } = DefaultBaseUrl;

    public TimeSpan RequestTimeout { get; set; } = DefaultRequestTimeout;

    public TimeSpan TotalTimeout { get; set; } = DefaultTotalTimeout;

    public int MaxResponseBytes { get; set; } = DefaultMaxResponseBytes;

    public bool Configured => !string.IsNullOrEmpty(AuthToken) && !string.IsNullOrEmpty(Organization);

    /// <summary><see cref="BaseUrl"/> sin barra final.</summary>
    public string NormalizedBaseUrl => (string.IsNullOrWhiteSpace(BaseUrl) ? DefaultBaseUrl : BaseUrl.Trim()).TrimEnd('/');

    /// <summary>Slug de organización de Sentry: letras minúsculas, dígitos, <c>-</c> y <c>_</c>, hasta 100 caracteres.</summary>
    public static bool IsOrganizationSlug(string? value) => value is not null && SlugPattern().IsMatch(value);

    /// <summary>URL absoluta <c>http(s)</c> sin credenciales, query ni fragmento.</summary>
    public static bool IsValidBaseUrl(string? value) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
        && uri.UserInfo.Length == 0
        && uri.Query.Length == 0
        && uri.Fragment.Length == 0;

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,99}$", RegexOptions.CultureInvariant)]
    private static partial Regex SlugPattern();
}
