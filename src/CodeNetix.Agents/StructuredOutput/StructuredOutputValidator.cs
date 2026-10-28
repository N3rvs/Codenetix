using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace CodeNetix.Agents.StructuredOutput;

/// <summary>
/// Valida la respuesta de un modelo contra un esquema JSON (2020-12) y unas reglas semánticas, y la convierte en
/// <typeparamref name="T"/>. Los errores citan rutas y palabras clave, nunca valores (podrían repetir texto de la issue):
/// los segmentos de ruta se sanean (<c>[A-Za-z0-9_]{1,40}</c> o <c>[n]</c>), una propiedad no admitida se cita por su
/// objeto padre (el nombre lo elige el modelo), se quitan los caracteres de control y el texto se acota a
/// <see cref="MaxErrorLength"/>.
/// </summary>
internal sealed class StructuredOutputValidator<T>
    where T : class
{
    /// <summary>Respuestas más largas cuentan como intento inválido sin analizarlas.</summary>
    public const int MaxResponseLength = 200_000;

    public const int MaxErrorLength = 500;

    private const int MaxErrors = 5;
    private const int MaxSegmentLength = 40;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly JsonSchema _schema;
    private readonly Func<T, string?> _semanticRules;

    /// <param name="schemaJson">Texto del esquema (el mismo para la petición y para la validación).</param>
    /// <param name="schemaName">Nombre del esquema en la petición estructurada (<c>[a-zA-Z0-9_-]</c>).</param>
    /// <param name="semanticRules">Devuelve el error, o <c>null</c> si el valor es válido.</param>
    /// <param name="referencedSchemas">
    /// Esquemas (con <c>$id</c> absoluto) a los que <paramref name="schemaJson"/> se refiere con <c>$ref</c>. Se registran
    /// para validar sin red y, para el Proveedor, se incrustan en <c>$defs</c> (un único documento autocontenido).
    /// </param>
    public StructuredOutputValidator(string schemaJson, string schemaName, Func<T, string?> semanticRules, params IReadOnlyList<string> referencedSchemas)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaName);
        ArgumentNullException.ThrowIfNull(semanticRules);
        ArgumentNullException.ThrowIfNull(referencedSchemas);

        SchemaJson = schemaJson;
        SchemaName = schemaName;
        _semanticRules = semanticRules;

        // Los referenciados primero: al construirse quedan registrados por su $id y el $ref del principal se resuelve.
        var buildOptions = new BuildOptions { SchemaRegistry = new SchemaRegistry() };
        foreach (var referenced in referencedSchemas)
        {
            JsonSchema.FromText(referenced, buildOptions);
        }

        _schema = JsonSchema.FromText(schemaJson, buildOptions);

        var forRequest = Bundle(schemaJson, referencedSchemas);
        RequestSchema = JsonSerializer.SerializeToElement(forRequest);
        PromptSchemaJson = forRequest.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public string SchemaJson { get; }

    public string SchemaName { get; }

    /// <summary>El esquema tal como se envía en <c>response_format</c> (autocontenido, sin <c>$schema</c> ni <c>$id</c>).</summary>
    public JsonElement RequestSchema { get; }

    /// <summary><see cref="RequestSchema"/> indentado, para el prompt de los Proveedores sin salida estructurada.</summary>
    public string PromptSchemaJson { get; }

    /// <summary>Extrae el primer objeto JSON de <paramref name="responseText"/> y lo valida.</summary>
    public StructuredValidation<T> Validate(string? responseText)
    {
        if (responseText is { Length: > MaxResponseLength })
        {
            return Fail($"la respuesta supera {MaxResponseLength} caracteres");
        }

        var json = JsonObjectExtractor.ExtractFirstObject(responseText);
        if (json is null)
        {
            return Fail("la respuesta no contiene ningún objeto JSON");
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var results = _schema.Evaluate(
                document.RootElement,
                new EvaluationOptions { OutputFormat = OutputFormat.List, IncludeApplicatorErrors = false });
            if (!results.IsValid)
            {
                return Fail(DescribeErrors(results));
            }

            var value = document.RootElement.Deserialize<T>(Json);
            if (value is null)
            {
                return Fail("el objeto JSON está vacío");
            }

            return _semanticRules(value) is { } error
                ? Fail(error)
                : new StructuredValidation<T>(value, null);
        }
        catch (JsonException)
        {
            return Fail("el objeto JSON no es válido");
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or OverflowException or NotSupportedException)
        {
            return Fail("el objeto JSON no se puede interpretar (" + ex.GetType().Name + ")");
        }
    }

    /// <summary>Sin caracteres de control y como mucho <see cref="MaxErrorLength"/> caracteres.</summary>
    internal static string Sanitize(string error)
    {
        var builder = new StringBuilder(Math.Min(error.Length, MaxErrorLength));
        foreach (var c in error)
        {
            builder.Append(char.IsControl(c) ? ' ' : c);
        }

        var text = builder.ToString().Trim();
        return text.Length <= MaxErrorLength ? text : string.Concat(text.AsSpan(0, MaxErrorLength - 1), "…");
    }

    private static StructuredValidation<T> Fail(string error) => StructuredValidation<T>.Fail(Sanitize(error));

    /// <summary>
    /// El esquema principal sin <c>$schema</c> ni <c>$id</c>, con cada esquema referenciado en <c>$defs</c> (también sin
    /// ellos) y cada <c>$ref</c> que apunta a su <c>$id</c> reescrito a <c>#/$defs/&lt;nombre&gt;</c>.
    /// </summary>
    private static JsonObject Bundle(string schemaJson, IReadOnlyList<string> referencedSchemas)
    {
        var root = JsonNode.Parse(schemaJson)!.AsObject();
        var baseUri = root["$id"]?.GetValue<string>() is { } id && Uri.TryCreate(id, UriKind.Absolute, out var parsed) ? parsed : null;
        root.Remove("$schema");
        root.Remove("$id");
        if (referencedSchemas.Count == 0)
        {
            return root;
        }

        var targets = new Dictionary<string, string>(StringComparer.Ordinal);
        var defs = new JsonObject();
        foreach (var text in referencedSchemas)
        {
            var schema = JsonNode.Parse(text)!.AsObject();
            var schemaId = schema["$id"]?.GetValue<string>()
                ?? throw new ArgumentException("Un esquema referenciado necesita un $id absoluto.", nameof(referencedSchemas));
            var uri = new Uri(schemaId, UriKind.Absolute);
            var name = Path.GetFileNameWithoutExtension(uri.AbsolutePath).Replace('.', '_');
            schema.Remove("$schema");
            schema.Remove("$id");
            defs[name] = schema;
            targets[uri.AbsoluteUri] = "#/$defs/" + name;
        }

        RewriteRefs(root, baseUri, targets);
        root["$defs"] = defs;
        return root;
    }

    private static void RewriteRefs(JsonNode? node, Uri? baseUri, Dictionary<string, string> targets)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj.ToList())
                {
                    if (key == "$ref" && value is JsonValue refValue && refValue.TryGetValue<string>(out var reference))
                    {
                        var resolved = baseUri is null
                            ? Uri.TryCreate(reference, UriKind.Absolute, out var absolute) ? absolute : null
                            : new Uri(baseUri, reference);
                        if (resolved is not null && targets.TryGetValue(resolved.AbsoluteUri, out var local))
                        {
                            obj[key] = local;
                        }
                    }
                    else
                    {
                        RewriteRefs(value, baseUri, targets);
                    }
                }

                break;

            case JsonArray array:
                foreach (var item in array)
                {
                    RewriteRefs(item, baseUri, targets);
                }

                break;
        }
    }

    private static string DescribeErrors(EvaluationResults results)
    {
        var entries = new List<(string? Branch, string Keyword, string Text)>();
        foreach (var detail in results.Details ?? [])
        {
            if (detail.Errors is null || detail.Errors.Count == 0)
            {
                continue;
            }

            var branch = OneOfBranch(detail.EvaluationPath.ToString());
            foreach (var (keyword, message) in detail.Errors)
            {
                // El nombre de una propiedad no admitida lo elige el modelo: se cita solo el objeto que la contiene.
                // Con la palabra clave, el error está en el objeto padre; con el esquema "false" de additionalProperties,
                // en la propia propiedad, cuyo segmento se quita.
                var onParent = keyword is "additionalProperties" or "unevaluatedProperties";
                var onChild = !onParent && IsAdditionalProperty(detail);
                var additional = onParent || onChild;
                var path = JsonPath(detail.InstanceLocation.ToString(), dropLast: onChild);
                var text = $"{path}: {(additional ? "propiedad no admitida" : Describe(keyword, message))}";
                entries.Add((branch, keyword, text));
            }
        }

        var messages = new List<string>();
        foreach (var (_, _, text) in RelevantBranches(entries))
        {
            if (!messages.Contains(text, StringComparer.Ordinal))
            {
                messages.Add(text);
            }
        }

        if (messages.Count == 0)
        {
            return "no cumple el esquema";
        }

        var shown = messages.Take(MaxErrors).ToList();
        if (messages.Count > MaxErrors)
        {
            shown.Add($"y {messages.Count - MaxErrors} error(es) más");
        }

        return string.Join("; ", shown);
    }

    /// <summary>
    /// En un <c>oneOf</c> discriminado (cada rama fija un <c>const</c>, como <c>outcome</c>), las ramas cuyo
    /// discriminador no coincide solo añadirían ruido: si alguna rama no tiene errores de <c>const</c>, se descartan las
    /// que sí; si todas los tienen (discriminador desconocido), solo se informa de ese <c>const</c>.
    /// </summary>
    private static IEnumerable<(string? Branch, string Keyword, string Text)> RelevantBranches(
        List<(string? Branch, string Keyword, string Text)> entries)
    {
        var branches = entries.Where(e => e.Branch is not null).Select(e => e.Branch!).Distinct(StringComparer.Ordinal).ToList();
        if (branches.Count == 0)
        {
            return entries;
        }

        var mismatched = entries
            .Where(e => e.Branch is not null && e.Keyword == "const")
            .Select(e => e.Branch!)
            .ToHashSet(StringComparer.Ordinal);
        if (mismatched.Count == 0)
        {
            return entries;
        }

        return mismatched.Count < branches.Count
            ? entries.Where(e => e.Branch is null || !mismatched.Contains(e.Branch))
            : entries.Where(e => e.Branch is null || e.Keyword == "const");
    }

    /// <summary><c>/oneOf/1/properties/spec/$ref/required</c> → <c>/oneOf/1</c> (el primer <c>oneOf</c>), o <c>null</c>.</summary>
    private static string? OneOfBranch(string evaluationPath)
    {
        const string Marker = "/oneOf/";
        var start = evaluationPath.IndexOf(Marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        var end = evaluationPath.IndexOf('/', start + Marker.Length);
        return end < 0 ? evaluationPath : evaluationPath[..end];
    }

    private static bool IsAdditionalProperty(EvaluationResults detail) =>
        detail.EvaluationPath.ToString().Contains("additionalProperties", StringComparison.Ordinal)
        || detail.EvaluationPath.ToString().Contains("unevaluatedProperties", StringComparison.Ordinal);

    /// <summary>Mensaje por palabra clave, sin citar el texto de JsonSchema.Net salvo nombres saneados.</summary>
    private static string Describe(string keyword, string message) => keyword switch
    {
        "required" => "falta la propiedad obligatoria " + RequiredNames(message),
        "type" => "tipo no válido" + ExpectedType(message),
        "minLength" => "no puede estar vacío",
        "maxLength" => "es demasiado largo",
        "minItems" => "tiene demasiados pocos elementos",
        "maxItems" => "tiene demasiados elementos",
        "const" or "enum" => "valor no admitido",
        _ => "no cumple '" + SafeSegment(keyword) + "'",
    };

    /// <summary><c>Value is "string" but should be "array"</c> → <c> (debe ser array)</c>.</summary>
    private static string ExpectedType(string message)
    {
        var marker = message.LastIndexOf("should be \"", StringComparison.Ordinal);
        if (marker < 0)
        {
            return string.Empty;
        }

        var expected = message[(marker + 11)..].TrimEnd('"', '.', ' ');
        return expected is "string" or "array" or "object" or "number" or "integer" or "boolean" or "null"
            ? $" (debe ser {expected})"
            : string.Empty;
    }

    /// <summary><c>Required properties ["problem"] are not present</c> → <c>'problem'</c> (nombres del esquema, saneados).</summary>
    private static string RequiredNames(string message)
    {
        var start = message.IndexOf('[', StringComparison.Ordinal);
        var end = message.LastIndexOf(']');
        if (start < 0 || end <= start)
        {
            return "(desconocida)";
        }

        return string.Join(", ", message[(start + 1)..end]
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(n => $"'{SafeSegment(n.Trim('"'))}'"));
    }

    /// <summary>JSON Pointer (<c>/affectedFiles/0</c>) → ruta legible y saneada (<c>$.affectedFiles[0]</c>).</summary>
    private static string JsonPath(string pointer, bool dropLast)
    {
        var segments = pointer.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (dropLast && segments.Count > 0)
        {
            segments.RemoveAt(segments.Count - 1);
        }

        var builder = new StringBuilder("$");
        foreach (var raw in segments)
        {
            if (raw.Length is > 0 and <= 9 && raw.All(char.IsAsciiDigit))
            {
                builder.Append('[').Append(raw).Append(']');
            }
            else
            {
                builder.Append('.').Append(SafeSegment(raw));
            }
        }

        return builder.ToString();
    }

    /// <summary>El segmento si es <c>[A-Za-z0-9_]{1,40}</c>; si no, <c>?</c>.</summary>
    private static string SafeSegment(string segment) =>
        segment.Length is > 0 and <= MaxSegmentLength && segment.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')
            ? segment
            : "?";
}

/// <summary>Resultado de <see cref="StructuredOutputValidator{T}.Validate"/>: <see cref="Value"/> o <see cref="Error"/>.</summary>
internal sealed record StructuredValidation<T>(T? Value, string? Error)
    where T : class
{
    public static StructuredValidation<T> Fail(string error) => new(null, error);
}
