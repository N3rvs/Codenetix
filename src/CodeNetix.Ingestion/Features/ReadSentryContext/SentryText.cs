using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeNetix.Contracts;

namespace CodeNetix.Ingestion.Features.ReadSentryContext;

/// <summary>
/// Saneado del texto de Sentry antes de que llegue al Analista: una línea, sin caracteres de control ni de formato
/// Unicode, acotada; URLs sin query ni fragmento, query strings sueltas ocultas, emails → <c>[email]</c> e IPs →
/// <c>[ip]</c>. Los secretos los quita <see cref="ISecretRedactor"/> sobre cada campo crudo, <b>antes</b> de acotarlo (un
/// secreto partido por el recorte ya no se reconocería), y otra vez sobre el bloque completo.
/// </summary>
internal static partial class SentryText
{
    public static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? Text(value)
            : null;

    /// <summary>
    /// El texto de una cadena JSON, o <c>null</c> si no es cadena o no se puede decodificar (p. ej. un sustituto UTF-16
    /// suelto escapado como <c>\ud800</c>).
    /// </summary>
    public static string? Text(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        try
        {
            return value.GetString();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Ruta de un marco sin datos del usuario: una ruta absoluta (<c>/</c>, <c>\</c>, <c>X:\</c>, <c>file://</c>…) cambia
    /// <c>/home/&lt;u&gt;</c>, <c>/Users/&lt;u&gt;</c> o <c>X:\Users\&lt;u&gt;</c> por <c>~</c> y, si no hay carpeta de usuario,
    /// conserva solo los 3 últimos segmentos. Una ruta relativa se deja igual.
    /// </summary>
    public static string? SafePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var value = path.Trim();
        var rooted = value.StartsWith('/') || value.StartsWith('\\') || value.Contains("://", StringComparison.Ordinal)
            || (value.Length >= 3 && char.IsAsciiLetter(value[0]) && value[1] == ':' && value[2] is '\\' or '/');
        if (!rooted)
        {
            return value;
        }

        var home = HomePattern().Match(value);
        if (home.Success)
        {
            return "~" + value[home.Length..];
        }

        var segments = value.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        return ".../" + string.Join('/', segments.Skip(Math.Max(0, segments.Length - 3)));
    }

    /// <summary>Primera propiedad presente de <paramref name="names"/> (Sentry usa camelCase en la API y snake_case en el evento crudo).</summary>
    public static JsonElement? Property(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>Cadena o número como texto (para líneas, columnas y valores de etiquetas).</summary>
    public static string? Scalar(JsonElement? element) => element switch
    {
        { ValueKind: JsonValueKind.String } e => Text(e),
        { ValueKind: JsonValueKind.Number } e => e.GetRawText(),
        { ValueKind: JsonValueKind.True } => "true",
        { ValueKind: JsonValueKind.False } => "false",
        _ => null,
    };

    /// <summary>
    /// Texto en una línea, sin controles ni caracteres de formato, con espacios colapsados y acotado. Con
    /// <paramref name="redactor"/>, los secretos se enmascaran sobre el texto crudo antes de acotarlo.
    /// </summary>
    public static string? Line(string? value, int maxLength, ISecretRedactor? redactor = null)
    {
        if (value is null)
        {
            return null;
        }

        if (redactor is not null)
        {
            value = redactor.Redact(value);
        }

        var builder = new StringBuilder(Math.Min(value.Length, maxLength + 16));
        var lastSpace = true;
        foreach (var c in value)
        {
            var category = char.GetUnicodeCategory(c);
            var blank = char.IsControl(c) || char.IsWhiteSpace(c) || category is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;
            if (blank)
            {
                if (!lastSpace)
                {
                    builder.Append(' ');
                    lastSpace = true;
                }

                continue;
            }

            builder.Append(c);
            lastSpace = false;
            if (builder.Length > maxLength * 2 + 64)
            {
                break; // ya sobra: el recorte final lo deja en maxLength
            }
        }

        var clean = builder.ToString().Trim();
        return clean.Length == 0 ? null : TextLimits.Truncate(clean, maxLength);
    }

    /// <summary>
    /// <see cref="Line"/> + quitar datos personales (URLs con query, emails, IPs) para el LLM. Los secretos se enmascaran con
    /// <paramref name="redactor"/> sobre el texto crudo, antes de cualquier recorte.
    /// </summary>
    public static string? Scrub(string? value, int maxLength, ISecretRedactor? redactor = null)
    {
        var line = Line(value, maxLength * 2 + 64, redactor);
        if (line is null)
        {
            return null;
        }

        line = UrlWithQueryPattern().Replace(line, "$1");
        line = LooseQueryPattern().Replace(line, "?[query]");
        line = EmailPattern().Replace(line, "[email]");
        line = IPv4Pattern().Replace(line, "[ip]");
        line = IPv6FullPattern().Replace(line, "[ip]");
        line = IPv6CompressedPattern().Replace(line, "[ip]");
        return TextLimits.Truncate(line, maxLength);
    }

    // Carpeta de usuario al principio de una ruta absoluta: /home/<u>, /Users/<u>, X:\Users\<u> (con file:// opcional).
    [GeneratedRegex(@"^(?:file:/{2,3})?(?:/home/[^/\\]+|/Users/[^/\\]+|/[A-Za-z]:[\\/]Users[\\/][^/\\]+|[A-Za-z]:[\\/]Users[\\/][^/\\]+)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex HomePattern();

    // http(s)://host/ruta?query#fragmento → http(s)://host/ruta
    [GeneratedRegex(@"(\b[a-zA-Z][a-zA-Z0-9+.-]*://[^\s?#]*)[?#]\S*", RegexOptions.CultureInvariant)]
    private static partial Regex UrlWithQueryPattern();

    // /ruta?clave=valor (sin esquema) → /ruta?[query]
    [GeneratedRegex(@"\?[^\s?#]*=\S*", RegexOptions.CultureInvariant)]
    private static partial Regex LooseQueryPattern();

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"(?<![\w.])(?:\d{1,3}\.){3}\d{1,3}(?![\w.])", RegexOptions.CultureInvariant)]
    private static partial Regex IPv4Pattern();

    [GeneratedRegex(@"(?<![\w:])(?:[0-9A-Fa-f]{1,4}:){7}[0-9A-Fa-f]{1,4}(?![\w:])", RegexOptions.CultureInvariant)]
    private static partial Regex IPv6FullPattern();

    // Forma comprimida: obligatoriamente con "::" y solo grupos hexadecimales (no toca "std::vector" ni horas "12:30:45").
    [GeneratedRegex(@"(?<![\w:])(?=[0-9A-Fa-f:]*::)(?:[0-9A-Fa-f]{1,4}(?::[0-9A-Fa-f]{1,4}){0,6})?::(?:[0-9A-Fa-f]{1,4}(?::[0-9A-Fa-f]{1,4}){0,6})?(?![\w:])", RegexOptions.CultureInvariant)]
    private static partial Regex IPv6CompressedPattern();
}
