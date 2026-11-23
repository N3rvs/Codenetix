using System.Text;
using CodeNetix.Contracts;

namespace CodeNetix.Pipeline.Features.RunPipeline;

/// <summary>
/// Cuerpo (Markdown, en español) del comentario de rechazo temprano (FR-10): título, categoría traducida, la
/// explicación del modelo (como mucho <see cref="AnalysisSchema.MaxExplanationLength"/> caracteres, en cita) y el
/// <c>shortId</c> de la Ejecución. Nunca cita el texto de la issue. El módulo GitHub sanea el cuerpo entero (HTML,
/// menciones y enlaces) y añade el marcador oculto de idempotencia al publicarlo.
/// </summary>
internal static class NotAutomatableComment
{
    public const string Title = "CodeNetix no puede automatizar esta issue";

    public static string CategoryLabel(NotAutomatableCategory category) => category switch
    {
        NotAutomatableCategory.MissingInformation => "falta información",
        NotAutomatableCategory.TooLarge => "cambio demasiado grande",
        NotAutomatableCategory.OutOfScope => "fuera del alcance (.NET)",
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, "Categoría desconocida."),
    };

    public static string Build(string shortId, NotAutomatableCategory category, string explanation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shortId);
        ArgumentNullException.ThrowIfNull(explanation);

        // \r\n y \r sueltos → \n: así cada línea de la explicación queda dentro de la cita "> ".
        var text = explanation.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();
        text = TextLimits.Truncate(text, AnalysisSchema.MaxExplanationLength);

        var builder = new StringBuilder();
        builder.Append("### ").AppendLine(Title);
        builder.AppendLine();
        builder.Append("**Motivo:** ").AppendLine(CategoryLabel(category));
        builder.AppendLine();
        foreach (var line in text.Split('\n'))
        {
            builder.Append("> ").AppendLine(line.TrimEnd());
        }

        builder.AppendLine();
        builder.Append("Ejecución `").Append(shortId).Append("`: no se abrirá ningún pull request. ");
        builder.Append("Si completas o acotas la issue, puedes volver a lanzar CodeNetix.");
        return builder.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }
}
