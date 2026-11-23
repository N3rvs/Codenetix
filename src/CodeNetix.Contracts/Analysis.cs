using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace CodeNetix.Contracts;

/// <summary>Título y cuerpo de una issue de GitHub, siempre como <see cref="UntrustedText"/> (AD-5).</summary>
public sealed record GitHubIssue(UntrustedText Title, UntrustedText Body);

/// <summary>Lectura de issues (AD-8: solo la implementa el módulo GitHub).</summary>
public interface IIssueReader
{
    /// <summary>
    /// <c>GET /repos/{owner}/{repo}/issues/{number}</c>. Lanza <see cref="GitHubUnavailableException"/> con un
    /// motivo legible (sin el token) si la issue no existe, no es visible o GitHub no responde.
    /// </summary>
    Task<GitHubIssue> GetAsync(string owner, string repo, long number, CancellationToken cancellationToken);
}

/// <summary>Entrada del Analista (AD-16 paso 3).</summary>
/// <param name="RepositoryFiles">Rutas de <c>git ls-files</c>, como mucho <see cref="MaxRepositoryFiles"/>.</param>
/// <param name="RepositoryFilesTruncated">El repositorio tiene más archivos que los de <paramref name="RepositoryFiles"/>.</param>
/// <param name="SentryContext">Traza, contexto y frecuencia del error de Sentry (historia 7.4), o <c>null</c>.</param>
public sealed record AnalysisInput(
    Guid RunId,
    UntrustedText IssueTitle,
    UntrustedText IssueBody,
    ImmutableArray<string> RepositoryFiles,
    bool RepositoryFilesTruncated,
    EffectiveConfig Config,
    UntrustedText? SentryContext = null)
{
    public const int MaxRepositoryFiles = 2000;

    /// <summary>Recorta <paramref name="files"/> a <see cref="MaxRepositoryFiles"/> e indica si sobraban.</summary>
    public static (ImmutableArray<string> Files, bool Truncated) LimitFiles(IReadOnlyList<string> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        return files.Count <= MaxRepositoryFiles
            ? ([.. files], false)
            : ([.. files.Take(MaxRepositoryFiles)], true);
    }
}

/// <summary>
/// Esquema JSON (2020-12) de la salida del Analista: <c>schemas/analysis.v1.json</c>, incrustado en Contracts. Es un
/// <c>oneOf</c> discriminado por <c>outcome</c>: <c>{"outcome":"spec","spec":&lt;Spec v1&gt;}</c> o
/// <c>{"outcome":"not_automatable","category":…,"explanation":…}</c>. La Spec se referencia con <c>$ref</c> a
/// <see cref="SpecSchema"/> (<see cref="SpecSchema.Id"/>).
/// </summary>
public static class AnalysisSchema
{
    /// <summary>Nombre del esquema en una petición de salida estructurada.</summary>
    public const string Name = "analysis";

    public const string OutcomeSpec = "spec";

    public const string OutcomeNotAutomatable = "not_automatable";

    /// <summary>Longitud máxima de <c>explanation</c>.</summary>
    public const int MaxExplanationLength = 1000;

    private static readonly Lazy<string> Text = new(() => EmbeddedSchemas.Load("analysis.v1.json"), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>El texto del esquema tal cual está en <c>schemas/analysis.v1.json</c>.</summary>
    public static string Json => Text.Value;
}

/// <summary>Por qué una issue no es automatizable (FR-10). Se serializa en snake_case, igual que en el esquema.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<NotAutomatableCategory>))]
public enum NotAutomatableCategory
{
    /// <summary>La issue no dice lo suficiente para saber qué cambiar.</summary>
    [JsonStringEnumMemberName("missing_information")]
    MissingInformation,

    /// <summary>El cambio es demasiado grande o arriesgado para un único PR automático.</summary>
    [JsonStringEnumMemberName("too_large")]
    TooLarge,

    /// <summary>No es un cambio de código .NET de este repositorio.</summary>
    [JsonStringEnumMemberName("out_of_scope")]
    OutOfScope,
}

public static class NotAutomatableCategories
{
    /// <summary>El nombre del esquema (<c>missing_information</c>, <c>too_large</c>, <c>out_of_scope</c>).</summary>
    public static string ToWireName(this NotAutomatableCategory category) => category switch
    {
        NotAutomatableCategory.MissingInformation => "missing_information",
        NotAutomatableCategory.TooLarge => "too_large",
        NotAutomatableCategory.OutOfScope => "out_of_scope",
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, "Categoría desconocida."),
    };

    /// <summary>Inverso de <see cref="ToWireName"/>; <c>false</c> si <paramref name="name"/> no es una categoría.</summary>
    public static bool TryParse(string? name, out NotAutomatableCategory category)
    {
        foreach (var candidate in Enum.GetValues<NotAutomatableCategory>())
        {
            if (string.Equals(candidate.ToWireName(), name, StringComparison.Ordinal))
            {
                category = candidate;
                return true;
            }
        }

        category = default;
        return false;
    }
}

/// <summary>
/// Resultado del Analista: <see cref="Ok"/>, <see cref="NotAutomatable"/>, <see cref="SchemaInvalid"/>,
/// <see cref="ProviderError"/> o <see cref="BudgetExhausted"/>.
/// </summary>
public abstract record AnalysisResult
{
    private AnalysisResult()
    {
    }

    /// <summary>Spec válida tras <paramref name="Attempts"/> intentos con el modelo <paramref name="Model"/>.</summary>
    public sealed record Ok(Spec Spec, int Attempts, string Model) : AnalysisResult;

    /// <summary>
    /// El modelo ha rechazado la issue (FR-10) con una respuesta válida tras <paramref name="Attempts"/> intentos.
    /// <paramref name="Explanation"/> la escribe el modelo (1–1000 caracteres, no vacía): es texto no confiable
    /// que hay que sanear antes de publicarlo.
    /// </summary>
    public sealed record NotAutomatable(NotAutomatableCategory Category, string Explanation, int Attempts, string Model) : AnalysisResult;

    /// <summary>Ningún intento produjo una Spec válida; <paramref name="LastError"/> es el error del último.</summary>
    public sealed record SchemaInvalid(string LastError, int Attempts) : AnalysisResult;

    /// <summary>Error HTTP, de red o timeout del Proveedor (no cuenta como reintento de esquema).</summary>
    public sealed record ProviderError(string Detail) : AnalysisResult;

    /// <summary>La Ejecución agotó su presupuesto antes o durante el análisis (FR-13, AD-15).</summary>
    public sealed record BudgetExhausted(string Detail) : AnalysisResult;
}

/// <summary>
/// Analista (FR-9, FR-10, AD-5): convierte la issue en una <see cref="Spec"/> o la declara no automatizable.
/// Nunca ejecuta código ni herramientas.
/// Cada salida rechazada se publica como <see cref="AnalystOutputRejected"/> en <see cref="IRunEventSink"/>.
/// </summary>
public interface IAnalyst
{
    Task<AnalysisResult> AnalyzeAsync(AnalysisInput input, CancellationToken cancellationToken);
}
