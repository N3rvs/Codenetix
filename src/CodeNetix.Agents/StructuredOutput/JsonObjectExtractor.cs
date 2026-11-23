using System.Text.Json;

namespace CodeNetix.Agents.StructuredOutput;

/// <summary>
/// Extrae el primer objeto JSON de una respuesta de modelo:
/// <list type="number">
/// <item>Si el texto completo (sin espacios) es un objeto JSON, ese.</item>
/// <item>Si no, el primer candidato <c>{…}</c> que <see cref="JsonDocument.Parse(string, JsonDocumentOptions)"/> acepta,
/// buscando primero dentro de las vallas <c>```json</c> (o <c>```</c>) y después en el texto completo. Un candidato que
/// no se cierra o no es JSON válido se descarta y se prueba desde el siguiente <c>{</c>.</item>
/// </list>
/// </summary>
internal static class JsonObjectExtractor
{
    private const string Fence = "```";

    /// <summary>Posiciones <c>{</c> que se prueban como mucho en cada texto.</summary>
    public const int MaxCandidates = 64;

    public static string? ExtractFirstObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.Trim();
        if (trimmed.StartsWith('{') && IsJsonObject(trimmed))
        {
            return trimmed;
        }

        foreach (var fenced in FencedBlocks(text))
        {
            if (Scan(fenced) is { } inFence)
            {
                return inFence;
            }
        }

        return Scan(text);
    }

    private static IEnumerable<string> FencedBlocks(string text)
    {
        var position = 0;
        while (true)
        {
            var open = text.IndexOf(Fence, position, StringComparison.Ordinal);
            if (open < 0)
            {
                yield break;
            }

            // Salta la etiqueta de idioma (```json) hasta el final de la línea.
            var lineEnd = text.IndexOf('\n', open + Fence.Length);
            if (lineEnd < 0)
            {
                yield break;
            }

            var close = text.IndexOf(Fence, lineEnd + 1, StringComparison.Ordinal);
            if (close < 0)
            {
                yield return text[(lineEnd + 1)..];
                yield break;
            }

            yield return text[(lineEnd + 1)..close];
            position = close + Fence.Length;
        }
    }

    /// <summary>El primer <c>{…}</c> equilibrado y válido de <paramref name="text"/>, o <c>null</c>.</summary>
    private static string? Scan(string text)
    {
        // Cada candidato recorre el texto: se acota el número de candidatos para no ser cuadrático.
        var attempts = 0;
        for (var start = text.IndexOf('{', StringComparison.Ordinal);
             start >= 0 && attempts < MaxCandidates;
             start = text.IndexOf('{', start + 1), attempts++)
        {
            if (BalancedFrom(text, start) is { } candidate && IsJsonObject(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Desde <paramref name="start"/> hasta su <c>}</c> de cierre (respetando cadenas y escapes), o <c>null</c>.</summary>
    private static string? BalancedFrom(string text, int start)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                    {
                        return text[start..(i + 1)];
                    }

                    break;
            }
        }

        return null;
    }

    private static bool IsJsonObject(string candidate)
    {
        try
        {
            using var document = JsonDocument.Parse(candidate);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
