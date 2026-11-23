using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeNetix.Contracts;

namespace CodeNetix.Ingestion.Features.ReadSentryContext;

/// <summary>Contexto ya saneado, redactado y acotado, con sus cifras (para el <c>RunEvent</c>).</summary>
internal sealed record SentryContextText(string Text, int Frames, int Breadcrumbs, bool Truncated);

/// <summary>
/// Convierte la incidencia y su último evento de Sentry en el texto que recibe el Analista (historia 7.4). Solo copia una
/// lista cerrada de campos: incidencia y frecuencia, release, entorno, etiquetas permitidas, excepciones con sus marcos
/// (primero los <c>in_app</c>) y las últimas migas. Nunca copia <c>request</c> (URL, cabeceras, cookies, query, cuerpo,
/// <c>env</c>), <c>user</c>, <c>contexts</c>, <c>extra</c>, <c>sdk</c>, <c>modules</c>, <c>vars</c>, <c>abs_path</c> ni el
/// <c>data</c> de las migas. Cada campo crudo pasa por <see cref="ISecretRedactor"/> y <see cref="SentryText.Scrub"/>
/// antes de acotarse, y el bloque completo otra vez por <see cref="ISecretRedactor"/>.
/// </summary>
internal static partial class SentryContextBuilder
{
    public const int MaxCharacters = 16_000;
    public const int MaxFrames = 30;
    public const int MaxBreadcrumbs = 20;
    public const int MaxExceptions = 3;
    public const int MaxTags = 15;
    public const int MaxText = 300;
    public const int MaxExceptionValue = 1000;

    /// <summary>Etiquetas que pueden llegar al Analista; cualquier otra (p. ej. <c>user.email</c>, <c>url</c>) se descarta.</summary>
    public static readonly IReadOnlyList<string> AllowedTags =
    [
        "environment", "release", "dist", "level", "handled", "mechanism", "logger", "transaction",
        "runtime", "runtime.name", "os", "os.name", "browser", "browser.name", "device.family",
    ];

    public const string TruncatedNotice = "[... truncated: the Sentry context exceeds the size limit]";

    public static SentryContextText Build(SentryIssueInfo issue, JsonElement latestEvent, ISecretRedactor redactor)
    {
        ArgumentNullException.ThrowIfNull(issue);
        ArgumentNullException.ThrowIfNull(redactor);

        var truncated = false;
        var text = new StringBuilder();
        text.Append("Sentry issue ").Append(Scrub(redactor, issue.ShortId, 100) ?? "?")
            .Append(" (id ").Append(issue.Id).Append(')');
        if (issue.ProjectSlug is { } project)
        {
            text.Append(" in project ").Append(project);
        }

        text.AppendLine();
        AppendField(text, "Title", Scrub(redactor, issue.Title, MaxExceptionValue));
        AppendField(text, "Culprit", Scrub(redactor, issue.Culprit, MaxText));
        AppendField(text, "Level", Scrub(redactor, issue.Level, 50));
        text.Append("Frequency: ").Append(issue.Count ?? "?").Append(" event(s), ")
            .Append(issue.UserCount ?? "?").Append(" user(s) affected");
        if (Scrub(redactor, issue.FirstSeen, 40) is { } first)
        {
            text.Append("; first seen ").Append(first);
        }

        if (Scrub(redactor, issue.LastSeen, 40) is { } last)
        {
            text.Append("; last seen ").Append(last);
        }

        text.AppendLine();

        var eventId = SentryText.String(latestEvent, "eventID") ?? SentryText.String(latestEvent, "id");
        text.Append("Latest event");
        if (eventId is not null && EventIdPattern().IsMatch(eventId))
        {
            text.Append(' ').Append(eventId);
        }

        if (Scrub(redactor, SentryText.String(latestEvent, "dateCreated") ?? SentryText.String(latestEvent, "dateReceived"), 40) is { } date)
        {
            text.Append(" at ").Append(date);
        }

        text.AppendLine();

        var tags = ReadTags(latestEvent);
        AppendField(text, "Release", ReleaseOf(latestEvent, redactor) ?? Tag(tags, "release", redactor));
        AppendField(text, "Environment", Scrub(redactor, SentryText.String(latestEvent, "environment"), 100) ?? Tag(tags, "environment", redactor));
        var allowed = AllowedTags
            .Where(t => t is not ("release" or "environment"))
            .Select(t => (Key: t, Value: Tag(tags, t, redactor)))
            .Where(t => t.Value is not null)
            .Take(MaxTags)
            .ToList();
        if (allowed.Count > 0)
        {
            text.Append("Tags: ").AppendJoin(", ", allowed.Select(t => $"{t.Key}={t.Value}")).AppendLine();
        }

        var frames = AppendExceptions(text, latestEvent, redactor, ref truncated);
        if (frames < 0)
        {
            // Sin excepción: el mensaje del evento (p. ej. captureMessage).
            frames = 0;
            var message = MessageOf(latestEvent, redactor);
            AppendField(text, "Message", message);
        }

        var breadcrumbs = AppendBreadcrumbs(text, latestEvent, redactor, ref truncated);

        var redacted = redactor.Redact(text.ToString().TrimEnd());
        if (redacted.Length > MaxCharacters)
        {
            redacted = redacted[..(MaxCharacters - TruncatedNotice.Length - 1)].TrimEnd() + "\n" + TruncatedNotice;
            truncated = true;
        }

        return new SentryContextText(redacted, frames, breadcrumbs, truncated);
    }

    private static void AppendField(StringBuilder text, string name, string? value)
    {
        if (value is not null)
        {
            text.Append(name).Append(": ").AppendLine(value);
        }
    }

    /// <summary>Excepciones (la principal primero) con sus marcos; devuelve los marcos escritos o -1 si no hay excepción.</summary>
    private static int AppendExceptions(StringBuilder text, JsonElement latestEvent, ISecretRedactor redactor, ref bool truncated)
    {
        var values = EntryValues(latestEvent, "exception") ?? TopLevelValues(latestEvent, "exception");
        if (values is not { Count: > 0 })
        {
            return -1;
        }

        // Sentry ordena las excepciones encadenadas de la más interna a la principal: la principal es la última.
        var ordered = values.AsEnumerable().Reverse().ToList();
        if (ordered.Count > MaxExceptions)
        {
            truncated = true;
        }

        var budget = MaxFrames;
        var written = 0;
        for (var i = 0; i < Math.Min(ordered.Count, MaxExceptions); i++)
        {
            var exception = ordered[i];
            var type = Scrub(redactor, SentryText.String(exception, "type"), 200) ?? "Exception";
            var value = Scrub(redactor, SentryText.String(exception, "value"), MaxExceptionValue);
            text.Append(CultureInfo.InvariantCulture, $"Exception {i + 1} of {ordered.Count}{(i == 0 ? " (main)" : string.Empty)}: {type}");
            if (value is not null)
            {
                text.Append(": ").Append(value);
            }

            text.AppendLine();
            if (SentryText.Property(exception, "mechanism") is { ValueKind: JsonValueKind.Object } mechanism
                && SentryText.Property(mechanism, "handled") is { ValueKind: JsonValueKind.True or JsonValueKind.False } handled)
            {
                text.Append("  Handled: ").AppendLine(handled.ValueKind == JsonValueKind.True ? "yes" : "no");
            }

            var frames = FramesOf(exception);
            if (frames.Count == 0)
            {
                continue;
            }

            // Del más reciente al más antiguo; primero los del código de la aplicación.
            var recentFirst = frames.AsEnumerable().Reverse().Select((f, index) => (Frame: f, Index: index, InApp: IsInApp(f))).ToList();
            var chosen = recentFirst.Where(f => f.InApp)
                .Concat(recentFirst.Where(f => !f.InApp))
                .Take(budget)
                .OrderBy(f => f.InApp ? 0 : 1)
                .ThenBy(f => f.Index)
                .ToList();
            if (chosen.Count < frames.Count)
            {
                truncated = true;
            }

            text.Append(CultureInfo.InvariantCulture, $"  Stack trace (most recent call first, application frames first; {chosen.Count} of {frames.Count} frames):").AppendLine();
            foreach (var (frame, _, inApp) in chosen)
            {
                AppendFrame(text, frame, inApp, redactor);
            }

            budget -= chosen.Count;
            written += chosen.Count;
            if (budget <= 0)
            {
                if (i + 1 < Math.Min(ordered.Count, MaxExceptions))
                {
                    truncated = true;
                }

                break;
            }
        }

        return written;
    }

    private static void AppendFrame(StringBuilder text, JsonElement frame, bool inApp, ISecretRedactor redactor)
    {
        var function = Scrub(redactor, SentryText.String(frame, "function") ?? SentryText.String(frame, "rawFunction") ?? SentryText.String(frame, "raw_function"), 200) ?? "?";
        var module = Scrub(redactor, SentryText.String(frame, "module") ?? SentryText.String(frame, "package"), 200);

        // Nunca abs_path (puede revelar rutas de usuario o URLs con query): el nombre relativo.
        var file = Scrub(redactor, SentryText.SafePath(SentryText.String(frame, "filename")), 200);
        var line = Digits(SentryText.Scalar(SentryText.Property(frame, "lineNo", "lineno")));
        var column = Digits(SentryText.Scalar(SentryText.Property(frame, "colNo", "colno")));

        text.Append("    at ");
        if (module is not null && !function.StartsWith(module, StringComparison.Ordinal))
        {
            text.Append(module).Append('.');
        }

        text.Append(function);
        if (file is not null || line is not null)
        {
            text.Append(" (").Append(file ?? "?");
            if (line is not null)
            {
                text.Append(':').Append(line);
                if (column is not null)
                {
                    text.Append(':').Append(column);
                }
            }

            text.Append(')');
        }

        text.AppendLine(inApp ? " [in-app]" : " [library]");

        if (inApp && ContextLineOf(frame, line, redactor) is { } code)
        {
            text.Append("      | ").AppendLine(code);
        }
    }

    /// <summary><c>context_line</c> (evento crudo) o la entrada de <c>context</c> de la línea del marco (API).</summary>
    private static string? ContextLineOf(JsonElement frame, string? line, ISecretRedactor redactor)
    {
        if (SentryText.String(frame, "context_line") is { } raw)
        {
            return Scrub(redactor, raw, 200);
        }

        if (line is not null && SentryText.Property(frame, "context") is { ValueKind: JsonValueKind.Array } context)
        {
            foreach (var pair in context.EnumerateArray())
            {
                if (pair.ValueKind == JsonValueKind.Array
                    && pair.GetArrayLength() == 2
                    && SentryText.Scalar(pair[0]) == line
                    && pair[1].ValueKind == JsonValueKind.String)
                {
                    return Scrub(redactor, SentryText.Text(pair[1]), 200);
                }
            }
        }

        return null;
    }

    private static int AppendBreadcrumbs(StringBuilder text, JsonElement latestEvent, ISecretRedactor redactor, ref bool truncated)
    {
        var values = EntryValues(latestEvent, "breadcrumbs") ?? TopLevelValues(latestEvent, "breadcrumbs");
        if (values is not { Count: > 0 })
        {
            return 0;
        }

        var last = values.Skip(Math.Max(0, values.Count - MaxBreadcrumbs)).ToList();
        if (last.Count < values.Count)
        {
            truncated = true;
        }

        text.Append(CultureInfo.InvariantCulture, $"Breadcrumbs (last {last.Count} of {values.Count}, oldest first):").AppendLine();
        foreach (var crumb in last)
        {
            // Nunca el "data" de la miga (URL completa, cabeceras, cuerpo…): solo categoría, nivel y mensaje.
            var timestamp = Scrub(redactor, SentryText.Scalar(SentryText.Property(crumb, "timestamp")), 40);
            var category = Scrub(redactor, SentryText.String(crumb, "category") ?? SentryText.String(crumb, "type"), 60);
            var level = Scrub(redactor, SentryText.String(crumb, "level"), 20);
            var message = Scrub(redactor, SentryText.String(crumb, "message"), MaxText);
            text.Append("  ");
            if (timestamp is not null)
            {
                text.Append(timestamp).Append(' ');
            }

            text.Append(category ?? "default");
            if (level is not null)
            {
                text.Append(' ').Append(level);
            }

            if (message is not null)
            {
                text.Append(": ").Append(message);
            }

            text.AppendLine();
        }

        return last.Count;
    }

    /// <summary><c>entries[type=&lt;type&gt;].data.values</c> (formato de la API de eventos).</summary>
    private static List<JsonElement>? EntryValues(JsonElement latestEvent, string type)
    {
        if (SentryText.Property(latestEvent, "entries") is not { ValueKind: JsonValueKind.Array } entries)
        {
            return null;
        }

        foreach (var entry in entries.EnumerateArray())
        {
            if (string.Equals(SentryText.String(entry, "type"), type, StringComparison.Ordinal)
                && SentryText.Property(entry, "data") is { ValueKind: JsonValueKind.Object } data
                && SentryText.Property(data, "values") is { ValueKind: JsonValueKind.Array } values)
            {
                return [.. values.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.Object)];
            }
        }

        return null;
    }

    /// <summary><c>&lt;name&gt;.values</c> (formato del evento crudo).</summary>
    private static List<JsonElement>? TopLevelValues(JsonElement latestEvent, string name) =>
        SentryText.Property(latestEvent, name) is { ValueKind: JsonValueKind.Object } container
            && SentryText.Property(container, "values") is { ValueKind: JsonValueKind.Array } values
            ? [.. values.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.Object)]
            : null;

    private static List<JsonElement> FramesOf(JsonElement exception) =>
        SentryText.Property(exception, "stacktrace", "rawStacktrace") is { ValueKind: JsonValueKind.Object } stack
            && SentryText.Property(stack, "frames") is { ValueKind: JsonValueKind.Array } frames
            ? [.. frames.EnumerateArray().Where(f => f.ValueKind == JsonValueKind.Object)]
            : [];

    private static bool IsInApp(JsonElement frame) =>
        SentryText.Property(frame, "inApp", "in_app") is { ValueKind: JsonValueKind.True };

    private static string? MessageOf(JsonElement latestEvent, ISecretRedactor redactor)
    {
        if (SentryText.Property(latestEvent, "entries") is { ValueKind: JsonValueKind.Array } entries)
        {
            foreach (var entry in entries.EnumerateArray())
            {
                if (string.Equals(SentryText.String(entry, "type"), "message", StringComparison.Ordinal)
                    && SentryText.Property(entry, "data") is { ValueKind: JsonValueKind.Object } data)
                {
                    return Scrub(redactor, SentryText.String(data, "formatted") ?? SentryText.String(data, "message"), MaxExceptionValue);
                }
            }
        }

        return Scrub(redactor, SentryText.String(latestEvent, "message"), MaxExceptionValue);
    }

    private static string? ReleaseOf(JsonElement latestEvent, ISecretRedactor redactor) => SentryText.Property(latestEvent, "release") switch
    {
        { ValueKind: JsonValueKind.String } s => Scrub(redactor, SentryText.Text(s), 100),
        { ValueKind: JsonValueKind.Object } o => Scrub(redactor, SentryText.String(o, "version") ?? SentryText.String(o, "shortVersion"), 100),
        _ => null,
    };

    /// <summary>Etiquetas del evento (<c>[{key, value}]</c> en la API o <c>[[key, value]]</c> en el evento crudo).</summary>
    private static Dictionary<string, string> ReadTags(JsonElement latestEvent)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (SentryText.Property(latestEvent, "tags") is not { ValueKind: JsonValueKind.Array } tags)
        {
            return result;
        }

        foreach (var tag in tags.EnumerateArray())
        {
            string? key = null;
            string? value = null;
            if (tag.ValueKind == JsonValueKind.Object)
            {
                key = SentryText.String(tag, "key");
                value = SentryText.Scalar(SentryText.Property(tag, "value"));
            }
            else if (tag.ValueKind == JsonValueKind.Array && tag.GetArrayLength() == 2)
            {
                key = SentryText.Scalar(tag[0]);
                value = SentryText.Scalar(tag[1]);
            }

            if (key is not null && value is not null && AllowedTags.Contains(key))
            {
                result.TryAdd(key, value);
            }
        }

        return result;
    }

    private static string? Tag(Dictionary<string, string> tags, string key, ISecretRedactor redactor) =>
        tags.TryGetValue(key, out var value) ? Scrub(redactor, value, 100) : null;

    /// <summary>Cada campo crudo se redacta antes de acotarlo (un secreto partido por el recorte ya no se reconocería).</summary>
    private static string? Scrub(ISecretRedactor redactor, string? value, int maxLength) => SentryText.Scrub(value, maxLength, redactor);

    private static string? Digits(string? value) =>
        value is { Length: > 0 and <= 9 } && value.All(char.IsAsciiDigit) ? value : null;

    [GeneratedRegex("^[0-9a-fA-F]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex EventIdPattern();
}
