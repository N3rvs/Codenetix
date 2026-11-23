using System.Globalization;
using System.Text.RegularExpressions;

namespace CodeNetix.GitHub;

/// <summary>Lectura de la cabecera <c>Link</c> de paginación de la API de GitHub (solo el número de página, nunca la URL).</summary>
internal static partial class GitHubLinkHeader
{
    /// <summary>¿Trae <c>rel="next"</c>?</summary>
    public static bool HasNext(string? link) => Page(link, "next") is not null;

    /// <summary><c>&lt;…&amp;page=5&gt;; rel="last"</c> → 5; <c>null</c> si no hay <c>rel="last"</c> legible.</summary>
    public static long? LastPage(string? link) => Page(link, "last");

    private static long? Page(string? link, string rel)
    {
        if (string.IsNullOrEmpty(link))
        {
            return null;
        }

        var marker = $"rel=\"{rel}\"";
        foreach (var part in link.Split(','))
        {
            if (!part.Contains(marker, StringComparison.Ordinal))
            {
                continue;
            }

            var match = PageParameter().Match(part);
            return match.Success && long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var page) && page >= 1
                ? page
                : null;
        }

        return null;
    }

    [GeneratedRegex(@"[?&]page=(\d{1,9})(?:[&>]|$)", RegexOptions.None, 1000)]
    private static partial Regex PageParameter();
}
