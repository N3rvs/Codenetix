using System.Globalization;
using System.Text.Json;
using CodeNetix.Contracts;
using CodeNetix.Ingestion.Features.ReceiveSentryWebhook;

namespace CodeNetix.Ingestion.Features.ReadSentryContext;

/// <summary>
/// Datos de una incidencia según <c>GET /api/0/organizations/{org}/issues/{id}/</c>. Todo salvo <see cref="Id"/> y
/// <see cref="ProjectSlug"/> (validados) es texto no confiable, ya limpio de caracteres de control y acotado.
/// </summary>
internal sealed record SentryIssueInfo(
    string Id,
    string? ShortId,
    string Title,
    string? Culprit,
    string? Level,
    string? ProjectSlug,
    string? Permalink,
    string? Count,
    string? UserCount,
    string? FirstSeen,
    string? LastSeen)
{
    public static SentryIssueInfo Parse(string issueId, JsonElement issue, ISecretRedactor? redactor = null)
    {
        var slug = issue.TryGetProperty("project", out var project) && project.ValueKind == JsonValueKind.Object
            ? SentryText.String(project, "slug")?.Trim().ToLowerInvariant()
            : null;
        var permalink = SentryText.String(issue, "permalink")?.Trim();
        return new SentryIssueInfo(
            issueId,
            SentryText.Line(SentryText.String(issue, "shortId"), 100, redactor),
            SentryText.Line(SentryText.String(issue, "title"), 1000, redactor) ?? string.Empty,
            SentryText.Line(SentryText.String(issue, "culprit"), 1000, redactor),
            SentryText.Line(SentryText.String(issue, "level"), 50, redactor),
            SentryWebhookOptions.IsProjectSlug(slug) ? slug : null,
            permalink is { Length: <= 500 } && Uri.TryCreate(permalink, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0
                ? permalink
                : null,
            Number(issue, "count"),
            Number(issue, "userCount"),
            SentryText.Line(SentryText.String(issue, "firstSeen"), 40, redactor),
            SentryText.Line(SentryText.String(issue, "lastSeen"), 40, redactor));
    }

    /// <summary>Contador de Sentry (cadena o número), solo si son dígitos.</summary>
    private static string? Number(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number when value.TryGetInt64(out var n) && n >= 0 => n.ToString(CultureInfo.InvariantCulture),
            _ => null,
        };
        return text is { Length: > 0 and <= 20 } && text.All(char.IsAsciiDigit) ? text : null;
    }
}
