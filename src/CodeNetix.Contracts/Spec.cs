using System.Collections.Immutable;
using System.Reflection;

namespace CodeNetix.Contracts;

/// <summary>
/// Spec técnica de una issue (FR-9, AD-5): la produce el Analista y la consume el Programador. Su forma la fija
/// <see cref="SpecSchema"/> (<c>schemas/spec.v1.json</c>); <see cref="SpecRules"/> añade las reglas semánticas.
/// Se serializa en camelCase (<c>problem</c>, <c>probableCause</c>, …).
/// </summary>
/// <param name="AcceptanceCriteria">De 1 a 20 criterios verificables, no vacíos.</param>
/// <param name="AffectedFiles">De 0 a 50 rutas relativas a la raíz del repositorio, sin <c>..</c> ni rutas absolutas.</param>
public sealed record Spec(
    string Problem,
    string ProbableCause,
    string ProposedChange,
    ImmutableArray<string> AcceptanceCriteria,
    ImmutableArray<string> AffectedFiles);

/// <summary>Esquema JSON (2020-12) de <see cref="Spec"/>: <c>schemas/spec.v1.json</c>, incrustado en Contracts.</summary>
public static class SpecSchema
{
    /// <summary>Nombre del esquema en una petición de salida estructurada.</summary>
    public const string Name = "spec";

    /// <summary><c>$id</c> del esquema (lo referencia <c>schemas/analysis.v1.json</c>).</summary>
    public const string Id = "https://codenetix.dev/schemas/spec.v1.json";

    private static readonly Lazy<string> Text = new(() => EmbeddedSchemas.Load("spec.v1.json"), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>El texto del esquema tal cual está en <c>schemas/spec.v1.json</c>.</summary>
    public static string Json => Text.Value;
}

/// <summary>Esquemas de <c>schemas/</c> incrustados en Contracts.</summary>
internal static class EmbeddedSchemas
{
    public static string Load(string fileName)
    {
        var resourceName = "CodeNetix.Contracts." + fileName;
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Falta el recurso incrustado {resourceName}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

/// <summary>Reglas de <see cref="Spec"/> que el esquema no expresa (o que conviene comprobar además del esquema).</summary>
public static class SpecRules
{
    public const int MaxAcceptanceCriteria = 20;
    public const int MaxAffectedFiles = 50;

    /// <summary>
    /// <c>null</c> si <paramref name="spec"/> es válida; si no, el primer error. El mensaje nunca cita los valores
    /// (podrían repetir texto de la issue): solo el campo y la posición.
    /// </summary>
    public static string? Validate(Spec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        if (string.IsNullOrWhiteSpace(spec.Problem))
        {
            return "problem está vacío";
        }

        if (string.IsNullOrWhiteSpace(spec.ProbableCause))
        {
            return "probableCause está vacío";
        }

        if (string.IsNullOrWhiteSpace(spec.ProposedChange))
        {
            return "proposedChange está vacío";
        }

        if (spec.AcceptanceCriteria.IsDefault || spec.AcceptanceCriteria.Length is 0 or > MaxAcceptanceCriteria)
        {
            return $"acceptanceCriteria debe tener entre 1 y {MaxAcceptanceCriteria} elementos";
        }

        for (var i = 0; i < spec.AcceptanceCriteria.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(spec.AcceptanceCriteria[i]))
            {
                return $"acceptanceCriteria[{i}] está vacío";
            }
        }

        if (spec.AffectedFiles.IsDefault || spec.AffectedFiles.Length > MaxAffectedFiles)
        {
            return $"affectedFiles debe tener entre 0 y {MaxAffectedFiles} elementos";
        }

        for (var i = 0; i < spec.AffectedFiles.Length; i++)
        {
            if (!IsSafeRelativePath(spec.AffectedFiles[i]))
            {
                return $"affectedFiles[{i}] no es una ruta relativa al repositorio (sin '..' ni rutas absolutas)";
            }
        }

        return null;
    }

    /// <summary>
    /// Ruta relativa a la raíz del repositorio: no vacía, sin espacios al principio o al final, sin caracteres de
    /// control, sin empezar por <c>/</c>, <c>\</c> o <c>~</c>, sin unidad (<c>C:</c>) ni esquema. Ningún segmento puede
    /// quedar vacío tras quitarle puntos y espacios finales (Windows los ignora: <c>.</c>, <c>..</c>, <c>.. </c>,
    /// <c>...</c>), ni ser <c>.git</c>.
    /// </summary>
    public static bool IsSafeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path != path.Trim() || path.Any(char.IsControl))
        {
            return false;
        }

        if (path[0] is '/' or '\\' or '~' || path.Contains(':', StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var segment in path.Split('/', '\\'))
        {
            var normalized = segment.TrimEnd('.', ' ');
            // Vacío incluye ".", "..", "...", ".. " y "a//b".
            if (normalized.Length == 0 || string.Equals(normalized, ".git", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}
