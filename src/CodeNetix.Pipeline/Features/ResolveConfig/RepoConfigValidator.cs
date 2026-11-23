using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Json.Schema;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace CodeNetix.Pipeline.Features.ResolveConfig;

/// <summary><c>codenetix.yml</c> ya validado (claves snake_case del esquema <c>codenetix.v1.json</c>).</summary>
internal sealed record RepoConfigFile(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("protected_paths")] IReadOnlyList<string>? ProtectedPaths,
    [property: JsonPropertyName("reviewers")] IReadOnlyList<string>? Reviewers,
    [property: JsonPropertyName("max_review_rounds")] int? MaxReviewRounds,
    [property: JsonPropertyName("max_coding_attempts")] int? MaxCodingAttempts,
    [property: JsonPropertyName("budget_usd")] decimal? BudgetUsd,
    [property: JsonPropertyName("models")] RepoConfigModels? Models);

internal sealed record RepoConfigModels(
    [property: JsonPropertyName("analyst")] string? Analyst,
    [property: JsonPropertyName("coder")] string? Coder,
    [property: JsonPropertyName("reviewers")] string? Reviewers);

/// <summary>Resultado de <see cref="RepoConfigValidator.Validate"/>: <see cref="Config"/> o <see cref="Error"/>.</summary>
internal sealed record RepoConfigValidation(RepoConfigFile? Config, string? Error)
{
    public bool IsValid => Config is not null;
}

/// <summary>
/// Valida <c>codenetix.yml</c> (AD-10): YAML → JSON con YamlDotNet y JSON Schema 2020-12 con JsonSchema.Net
/// contra el esquema público <c>schemas/codenetix.v1.json</c> (incrustado). Los errores empiezan por
/// <c>codenetix.yml: </c> y dicen dónde está el problema (línea y columna, o ruta JSON).
/// </summary>
internal static class RepoConfigValidator
{
    public const string FileName = "codenetix.yml";

    /// <summary>Tamaño máximo del archivo: más es un error, no una configuración.</summary>
    public const int MaxLength = 64 * 1024;

    /// <summary>Nodos máximos tras resolver anclas y alias (evita expansiones exponenciales).</summary>
    public const int MaxNodes = 10_000;

    /// <summary>Anidamiento máximo de mapas y listas (muy por debajo del límite de 64 de System.Text.Json).</summary>
    public const int MaxDepth = 32;

    private const int MaxErrors = 5;

    private static readonly Lazy<JsonSchema> Schema = new(LoadSchema);

    private static readonly JsonSerializerOptions Json = new() { NumberHandling = JsonNumberHandling.Strict };

    public static RepoConfigValidation Validate(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);

        if (yaml.Length > MaxLength)
        {
            return Fail($"el archivo supera {MaxLength / 1024} KB");
        }

        JsonNode? document;
        try
        {
            document = YamlToJson(yaml);
        }
        catch (YamlException ex)
        {
            var (line, column) = PositionOf(ex, yaml);
            return Fail(string.Create(CultureInfo.InvariantCulture, $"YAML no válido en la línea {line}, columna {column}: {Describe(ex)}"));
        }
        catch (InvalidRepoConfigException ex)
        {
            return Fail(ex.Message);
        }

        try
        {
            using var instance = JsonDocument.Parse(document?.ToJsonString() ?? "null");

            // Sin los errores de los aplicadores (properties, items…): solo los de las hojas, que dicen la ruta exacta.
            var results = Schema.Value.Evaluate(
                instance.RootElement,
                new EvaluationOptions { OutputFormat = OutputFormat.List, IncludeApplicatorErrors = false });
            if (!results.IsValid)
            {
                return Fail(DescribeErrors(results));
            }

            var config = instance.RootElement.Deserialize<RepoConfigFile>(Json);
            return config is null ? Fail("el archivo está vacío") : new RepoConfigValidation(config, null);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            // Red de seguridad: ningún contenido del repositorio debe acabar en StageError.
            return Fail("no se puede interpretar el archivo (" + ex.GetType().Name + ")");
        }
    }

    private static RepoConfigValidation Fail(string message) => new(null, $"{FileName}: {message}");

    private static JsonSchema LoadSchema()
    {
        using var stream = typeof(RepoConfigValidator).Assembly.GetManifestResourceStream("CodeNetix.Pipeline.codenetix.v1.json")
            ?? throw new InvalidOperationException("No se encuentra el esquema incrustado codenetix.v1.json.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return JsonSchema.FromText(reader.ReadToEnd(), new BuildOptions { SchemaRegistry = new SchemaRegistry() });
    }

    /// <summary>
    /// Posición del error. YamlDotNet sitúa algunos errores de tabulador en el inicio del bloque (1:1): en ese
    /// caso se busca el primer tabulador de la sangría.
    /// </summary>
    private static (long Line, long Column) PositionOf(YamlException ex, string yaml)
    {
        if (ex.Message.Contains("tab", StringComparison.OrdinalIgnoreCase))
        {
            var lines = yaml.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var indent = lines[i].Length - lines[i].TrimStart(' ', '\t').Length;
                var tab = lines[i].IndexOf('\t', StringComparison.Ordinal);
                if (tab >= 0 && tab < indent)
                {
                    return (i + 1, tab + 1);
                }
            }
        }

        return (ex.Start.Line, ex.Start.Column);
    }

    /// <summary>Mensaje de YamlDotNet sin la posición (ya se da en español) ni el prefijo técnico.</summary>
    private static string Describe(YamlException ex)
    {
        var message = ex.Message;
        var marker = message.IndexOf("): ", StringComparison.Ordinal);
        if (message.StartsWith('(') && marker > 0)
        {
            message = message[(marker + 3)..];
        }

        message = string.Join(' ', message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();

        // Los casos más frecuentes, en español; el resto conserva el texto del analizador YAML.
        if (message.Contains("invalid tab", StringComparison.OrdinalIgnoreCase))
        {
            return "tabulador en la sangría (usa espacios)";
        }

        const string DuplicateKey = "Duplicate key ";
        if (message.StartsWith(DuplicateKey, StringComparison.Ordinal))
        {
            return $"clave repetida '{message[DuplicateKey.Length..]}'";
        }

        return message;
    }

    // --- YAML → JSON ----------------------------------------------------------------------

    /// <summary>
    /// Convierte el primer documento YAML en JSON con el esquema core de YAML 1.2 (los escalares sin comillas
    /// <c>true</c>/<c>false</c>, <c>null</c>/<c>~</c> y los números conservan su tipo; los entrecomillados son
    /// cadenas). Un archivo vacío es <c>null</c>. Lanza <see cref="YamlException"/> ante YAML mal formado e
    /// <see cref="InvalidRepoConfigException"/> ante contenido no admitido.
    /// <para>
    /// Se recorre el flujo de eventos del analizador de YamlDotNet de forma iterativa (sin el modelo de
    /// representación, que es recursivo): el anidamiento se corta en <see cref="MaxDepth"/>, los alias solo
    /// pueden apuntar a anclas ya cerradas (sin ciclos) y el total de nodos, alias expandidos incluidos, se
    /// corta en <see cref="MaxNodes"/>. Así ningún archivo puede agotar la pila ni la memoria.
    /// </para>
    /// </summary>
    internal static JsonNode? YamlToJson(string yaml)
    {
        var parser = new Parser(new StringReader(yaml));
        Next(parser);
        if (parser.Current is not StreamStart)
        {
            throw new InvalidRepoConfigException("no es un archivo YAML");
        }

        Next(parser);
        if (parser.Current is StreamEnd)
        {
            return null;
        }

        var root = ReadDocument(parser);

        Next(parser);
        if (parser.Current is DocumentStart)
        {
            throw new InvalidRepoConfigException("solo se admite un documento YAML");
        }

        return root;
    }

    private static void Next(IParser parser)
    {
        if (!parser.MoveNext())
        {
            throw new InvalidRepoConfigException("el archivo YAML termina de forma inesperada");
        }
    }

    /// <summary>Lee un documento (desde <c>DocumentStart</c> hasta <c>DocumentEnd</c>) sin recursión.</summary>
    private static JsonNode? ReadDocument(IParser parser)
    {
        var anchors = new Dictionary<string, (JsonNode? Node, int Size)>(StringComparer.Ordinal);
        var stack = new Stack<Frame>();
        var budget = MaxNodes;
        JsonNode? root = null;
        var hasRoot = false;

        if (parser.Current is not DocumentStart)
        {
            throw new InvalidRepoConfigException("no es un documento YAML");
        }

        while (true)
        {
            Next(parser);
            var current = parser.Current!;
            if (current is DocumentEnd)
            {
                return hasRoot ? root : null;
            }

            var top = stack.Count > 0 ? stack.Peek() : null;

            // En un mapa, el evento que llega sin clave pendiente es la clave: solo se admite texto.
            if (top is { Node: JsonObject map, Key: null })
            {
                if (current is MappingEnd)
                {
                    Close(stack, anchors, budget);
                    if (stack.Count == 0)
                    {
                        (root, hasRoot) = (map, true);
                    }

                    continue;
                }

                if (current is not Scalar { } keyScalar)
                {
                    throw new YamlException(current.Start, current.End, "las claves deben ser texto");
                }

                if (map.ContainsKey(keyScalar.Value))
                {
                    throw new YamlException(current.Start, current.End, $"clave repetida '{keyScalar.Value}'");
                }

                top.Key = keyScalar.Value;
                continue;
            }

            if (current is SequenceEnd)
            {
                var closed = Close(stack, anchors, budget);
                if (stack.Count == 0)
                {
                    (root, hasRoot) = (closed, true);
                }

                continue;
            }

            JsonNode? value;
            switch (current)
            {
                case MappingStart start:
                    Spend(ref budget, 1);
                    Push(stack, new JsonObject(), start.Anchor.IsEmpty ? null : start.Anchor.Value, budget);
                    Attach(top, start.Anchor, anchors, value: stack.Peek().Node, size: 0, deferAnchor: true);
                    continue;

                case SequenceStart start:
                    Spend(ref budget, 1);
                    Push(stack, new JsonArray(), start.Anchor.IsEmpty ? null : start.Anchor.Value, budget);
                    Attach(top, start.Anchor, anchors, value: stack.Peek().Node, size: 0, deferAnchor: true);
                    continue;

                case Scalar scalar:
                    Spend(ref budget, 1);
                    value = ConvertScalar(scalar);
                    Attach(top, scalar.Anchor, anchors, value, size: 1, deferAnchor: false);
                    break;

                case AnchorAlias alias:
                    if (!anchors.TryGetValue(alias.Value.Value, out var anchored))
                    {
                        throw new YamlException(current.Start, current.End, $"alias '*{alias.Value.Value}' sin ancla previa o recursivo");
                    }

                    Spend(ref budget, anchored.Size);
                    value = anchored.Node?.DeepClone();
                    Attach(top, AnchorName.Empty, anchors, value, size: anchored.Size, deferAnchor: false);
                    break;

                default:
                    throw new YamlException(current.Start, current.End, "elemento YAML no admitido");
            }

            if (top is null)
            {
                (root, hasRoot) = (value, true);
            }
        }
    }

    private sealed class Frame(JsonNode node, string? anchor, int budgetAtStart)
    {
        public JsonNode Node { get; } = node;

        public string? Anchor { get; } = anchor;

        public int BudgetAtStart { get; } = budgetAtStart;

        /// <summary>Solo en mapas: la clave cuyo valor se espera.</summary>
        public string? Key { get; set; }
    }

    private static void Push(Stack<Frame> stack, JsonNode node, string? anchor, int budget)
    {
        if (stack.Count >= MaxDepth)
        {
            throw new InvalidRepoConfigException("anidamiento demasiado profundo");
        }

        stack.Push(new Frame(node, anchor, budget));
    }

    /// <summary>Cierra la colección en curso; si tenía ancla, la registra ya completa (con su tamaño en nodos).</summary>
    private static JsonNode Close(Stack<Frame> stack, Dictionary<string, (JsonNode? Node, int Size)> anchors, int budget)
    {
        var frame = stack.Pop();
        if (frame.Anchor is { } anchor)
        {
            anchors[anchor] = (frame.Node, frame.BudgetAtStart - budget + 1);
        }

        return frame.Node;
    }

    /// <summary>
    /// Cuelga <paramref name="value"/> de su contenedor (<paramref name="parent"/>). Las colecciones se cuelgan al
    /// abrirse (se rellenan después); su ancla se registra al cerrarse, así un alias dentro de su propia ancla falla.
    /// </summary>
    private static void Attach(
        Frame? parent,
        AnchorName anchor,
        Dictionary<string, (JsonNode? Node, int Size)> anchors,
        JsonNode? value,
        int size,
        bool deferAnchor)
    {
        switch (parent?.Node)
        {
            case JsonObject map:
                map[parent.Key!] = value;
                parent.Key = null;
                break;
            case JsonArray array:
                array.Add(value);
                break;
        }

        if (!deferAnchor && !anchor.IsEmpty)
        {
            anchors[anchor.Value] = (value, size);
        }
    }

    private static void Spend(ref int budget, int nodes)
    {
        budget -= nodes;
        if (budget < 0)
        {
            throw new InvalidRepoConfigException("el archivo es demasiado grande (demasiados nodos tras resolver anclas y alias)");
        }
    }

    private static JsonNode? ConvertScalar(Scalar scalar)
    {
        var value = scalar.Value ?? string.Empty;
        if (scalar.Style is not ScalarStyle.Plain || (!scalar.Tag.IsEmpty && scalar.Tag.Value.EndsWith(":str", StringComparison.Ordinal)))
        {
            return JsonValue.Create(value);
        }

        switch (value)
        {
            case "" or "~" or "null" or "Null" or "NULL":
                return null;
            case "true" or "True" or "TRUE":
                return JsonValue.Create(true);
            case "false" or "False" or "FALSE":
                return JsonValue.Create(false);
        }

        if (IsYamlNumber(value))
        {
            if (long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer))
            {
                return JsonValue.Create(integer);
            }

            if (!decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                throw new YamlException(scalar.Start, scalar.End, $"número fuera de rango: {value}");
            }

            // 1.0, 2e0…: un decimal sin parte fraccionaria es el entero (version: 1.0 vale como 1).
            if (number == decimal.Truncate(number) && number is >= long.MinValue and <= long.MaxValue)
            {
                return JsonValue.Create((long)number);
            }

            return JsonValue.Create(number);
        }

        return JsonValue.Create(value);
    }

    /// <summary>Entero o decimal del esquema core de YAML 1.2 (sin <c>.inf</c>, <c>.nan</c>, octal ni hexadecimal).</summary>
    private static bool IsYamlNumber(string value)
    {
        var span = value.AsSpan();
        if (span.Length > 0 && span[0] is '-' or '+')
        {
            span = span[1..];
        }

        var digits = 0;
        var dot = false;
        var exponent = false;
        for (var i = 0; i < span.Length; i++)
        {
            var c = span[i];
            if (char.IsAsciiDigit(c))
            {
                digits++;
            }
            else if (c == '.' && !dot && !exponent)
            {
                dot = true;
            }
            else if (c is 'e' or 'E' && digits > 0 && !exponent)
            {
                exponent = true;
                if (i + 1 < span.Length && span[i + 1] is '-' or '+')
                {
                    i++;
                }

                if (i + 1 >= span.Length)
                {
                    return false;
                }
            }
            else
            {
                return false;
            }
        }

        return digits > 0;
    }

    // --- Errores del esquema ---------------------------------------------------------------

    private static string DescribeErrors(EvaluationResults results)
    {
        var messages = new List<string>();
        foreach (var detail in results.Details ?? [])
        {
            if (detail.Errors is null || detail.Errors.Count == 0)
            {
                continue;
            }

            var path = JsonPath(detail.InstanceLocation.ToString());
            foreach (var (keyword, message) in detail.Errors)
            {
                var text = $"{path}: {Translate(keyword, message, detail)}";
                if (!messages.Contains(text, StringComparer.Ordinal))
                {
                    messages.Add(text);
                }
            }
        }

        if (messages.Count == 0)
        {
            return "no cumple el esquema codenetix.v1.json";
        }

        var shown = messages.Take(MaxErrors).ToList();
        if (messages.Count > MaxErrors)
        {
            shown.Add($"y {messages.Count - MaxErrors} error(es) más");
        }

        return string.Join("; ", shown);
    }

    /// <summary>JSON Pointer (<c>/reviewers/0</c>) → ruta legible (<c>$.reviewers[0]</c>).</summary>
    internal static string JsonPath(string pointer)
    {
        var builder = new StringBuilder("$");
        foreach (var raw in pointer.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var segment = raw.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (segment.All(char.IsAsciiDigit))
            {
                builder.Append('[').Append(segment).Append(']');
            }
            else
            {
                builder.Append('.').Append(segment);
            }
        }

        return builder.ToString();
    }

    /// <summary>Mensaje en español para cada palabra clave del esquema (los de JsonSchema.Net están en inglés).</summary>
    private static string Translate(string keyword, string message, EvaluationResults detail)
    {
        var location = detail.EvaluationPath.ToString();
        return keyword switch
        {
            "required" => $"falta la clave obligatoria {QuotedNames(message)}",
            "additionalProperties" or "unevaluatedProperties" => "clave no admitida",
            "" or "$false" or "false" when location.Contains("additionalProperties", StringComparison.Ordinal) => "clave no admitida",
            "const" when location.Contains("/version", StringComparison.Ordinal) => "la versión debe ser 1",
            "const" => "valor no admitido",
            "enum" => "valor no admitido (se admite security, quality o tests)",
            "type" => "tipo no válido: " + ExpectedType(message),
            "minimum" or "maximum" => "fuera de rango: debe ser un entero entre 1 y 5",
            "exclusiveMinimum" => "debe ser mayor que 0",
            "minItems" => "no puede estar vacío",
            "uniqueItems" => "tiene elementos repetidos",
            "minLength" => "no puede estar vacío",
            _ => message,
        };
    }

    /// <summary><c>Required properties ["version"] are not present</c> → <c>'version'</c>.</summary>
    private static string QuotedNames(string message)
    {
        var start = message.IndexOf('[', StringComparison.Ordinal);
        var end = message.LastIndexOf(']');
        if (start < 0 || end <= start)
        {
            return "version";
        }

        var names = message[(start + 1)..end]
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(n => $"'{n.Trim('"')}'");
        return string.Join(", ", names);
    }

    /// <summary><c>Value is "number" but should be "integer"</c> → <c>debe ser un número entero</c>.</summary>
    private static string ExpectedType(string message)
    {
        var marker = message.LastIndexOf("should be \"", StringComparison.Ordinal);
        var expected = marker < 0 ? string.Empty : message[(marker + 11)..].TrimEnd('"', '.', ' ');
        return expected switch
        {
            "integer" => "debe ser un número entero",
            "number" => "debe ser un número",
            "string" => "debe ser texto",
            "array" => "debe ser una lista",
            "object" => "debe ser un mapa de claves (p. ej. version: 1)",
            "boolean" => "debe ser true o false",
            _ => message,
        };
    }
}

/// <summary><c>codenetix.yml</c> no es convertible a JSON (varios documentos, demasiado grande…).</summary>
internal sealed class InvalidRepoConfigException(string message) : Exception(message);
