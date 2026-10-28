using System.Globalization;
using System.Text.RegularExpressions;

namespace CodeNetix.Ingestion.Features.SubmitManualEvent;

internal enum ManualUrlKind
{
    GitHubIssue,
    Sentry,
    Unsupported,
}

/// <param name="CanonicalUrl">Solo en <see cref="ManualUrlKind.GitHubIssue"/>: <c>https://github.com/owner/repo/issues/n</c>.</param>
/// <param name="ObjectKey">Solo en <see cref="ManualUrlKind.GitHubIssue"/>: <c>github:owner/repo#n</c>.</param>
/// <param name="Owner">Solo en <see cref="ManualUrlKind.GitHubIssue"/>: owner en minúsculas.</param>
/// <param name="Repo">Solo en <see cref="ManualUrlKind.GitHubIssue"/>: repositorio en minúsculas.</param>
internal sealed record ManualUrl(ManualUrlKind Kind, string? CanonicalUrl, string? ObjectKey, string? Owner = null, string? Repo = null)
{
    public static readonly ManualUrl Sentry = new(ManualUrlKind.Sentry, null, null);

    public static readonly ManualUrl Unsupported = new(ManualUrlKind.Unsupported, null, null);
}

/// <summary>
/// Reconoce la URL de <c>codenetix fix</c>. Solo se admiten issues de GitHub
/// (<c>https://github.com/&lt;owner&gt;/&lt;repo&gt;/issues/&lt;n&gt;</c>, con o sin <c>www.</c>, barra final,
/// query o fragmento), que se canonizan con owner y repo en minúsculas. Sentry se reconoce para
/// responder "próximamente". Cualquier otra cosa (PR, commit, otros hosts) se rechaza.
/// </summary>
internal static partial class ManualUrlParser
{
    public const string UnsupportedMessage =
        "URL no admitida. CodeNetix solo acepta issues de GitHub con la forma https://github.com/<owner>/<repo>/issues/<número> (no pull requests, commits ni otros hosts).";

    public const string SentryMessage = "Soporte de Sentry disponible próximamente.";

    public static ManualUrl Parse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)
            || !Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return ManualUrl.Unsupported;
        }

        var host = uri.IdnHost.ToLowerInvariant();
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (host is "github.com" or "www.github.com")
        {
            return ParseGitHub(uri, segments);
        }

        if (IsSentry(host, segments))
        {
            return ManualUrl.Sentry;
        }

        return ManualUrl.Unsupported;
    }

    private static ManualUrl ParseGitHub(Uri uri, string[] segments)
    {
        // Solo https, puerto por defecto y sin credenciales en la URL.
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length > 0)
        {
            return ManualUrl.Unsupported;
        }

        // Sin segmentos vacíos intermedios ("o//r"): la barra final sí se admite.
        if (uri.AbsolutePath.TrimEnd('/').Contains("//", StringComparison.Ordinal))
        {
            return ManualUrl.Unsupported;
        }

        if (segments.Length != 4)
        {
            return ManualUrl.Unsupported;
        }

        // "repo.git" es el mismo repositorio que "repo".
        var repoSegment = segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? segments[1][..^4]
            : segments[1];

        if (!OwnerPattern().IsMatch(segments[0])
            || !RepoPattern().IsMatch(repoSegment)
            || repoSegment is "." or ".."
            || !string.Equals(segments[2], "issues", StringComparison.Ordinal)
            || !NumberPattern().IsMatch(segments[3])
            || !long.TryParse(segments[3], NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            || number <= 0)
        {
            return ManualUrl.Unsupported;
        }

        var owner = segments[0].ToLowerInvariant();
        var repo = repoSegment.ToLowerInvariant();
        var n = number.ToString(CultureInfo.InvariantCulture);
        return new ManualUrl(
            ManualUrlKind.GitHubIssue,
            $"https://github.com/{owner}/{repo}/issues/{n}",
            $"github:{owner}/{repo}#{n}",
            owner,
            repo);
    }

    /// <summary>Host <c>sentry.io</c> o <c>*.sentry.io</c>, o ruta <c>/organizations/&lt;org&gt;/issues/&lt;id&gt;</c> (Sentry autoalojado).</summary>
    private static bool IsSentry(string host, string[] segments) =>
        host == "sentry.io"
        || host.EndsWith(".sentry.io", StringComparison.Ordinal)
        || (segments.Length >= 4
            && string.Equals(segments[0], "organizations", StringComparison.OrdinalIgnoreCase)
            && string.Equals(segments[2], "issues", StringComparison.OrdinalIgnoreCase));

    // Owner de GitHub: alfanumérico y guiones, sin empezar por guion, hasta 39 caracteres.
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9-]{0,38}$", RegexOptions.CultureInvariant)]
    private static partial Regex OwnerPattern();

    // Repositorio de GitHub: alfanumérico, '.', '-' y '_', hasta 100 caracteres.
    [GeneratedRegex("^[A-Za-z0-9._-]{1,100}$", RegexOptions.CultureInvariant)]
    private static partial Regex RepoPattern();

    [GeneratedRegex("^[0-9]{1,18}$", RegexOptions.CultureInvariant)]
    private static partial Regex NumberPattern();
}
