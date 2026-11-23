using CodeNetix.Agents.StructuredOutput;
using CodeNetix.Contracts;

namespace CodeNetix.Agents.Features.AnalyzeIssue;

/// <summary>
/// Respuesta del Analista según <c>schemas/analysis.v1.json</c> (camelCase): <see cref="Spec"/> con
/// <c>outcome = "spec"</c>, o <see cref="Category"/> y <see cref="Explanation"/> con <c>outcome = "not_automatable"</c>.
/// </summary>
internal sealed record AnalystOutput(string? Outcome, Spec? Spec, string? Category, string? Explanation)
{
    /// <summary>Esquema de la petición y de la validación (con la Spec referenciada) más <see cref="Rules"/>.</summary>
    public static readonly StructuredOutputValidator<AnalystOutput> Validator =
        new(AnalysisSchema.Json, AnalysisSchema.Name, Rules, SpecSchema.Json);

    /// <summary>
    /// Reglas que el esquema no expresa: las de <see cref="SpecRules"/> (con el prefijo <c>spec.</c>) y una
    /// <c>explanation</c> que no sea solo espacios. Como en <see cref="SpecRules"/>, los errores nunca citan valores.
    /// </summary>
    public static string? Rules(AnalystOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);

        switch (output.Outcome)
        {
            case AnalysisSchema.OutcomeSpec:
                if (output.Spec is null)
                {
                    return "falta la propiedad obligatoria 'spec'";
                }

                return SpecRules.Validate(output.Spec) is { } specError ? "spec." + specError : null;

            case AnalysisSchema.OutcomeNotAutomatable:
                if (!NotAutomatableCategories.TryParse(output.Category, out _))
                {
                    return "category: valor no admitido";
                }

                return string.IsNullOrWhiteSpace(output.Explanation) ? "explanation está vacío" : null;

            default:
                return "outcome: valor no admitido";
        }
    }
}
