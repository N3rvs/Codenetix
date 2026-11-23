using System.Globalization;
using System.Text;
using CodeNetix.Contracts;

namespace CodeNetix.Pipeline.Features.RunPipeline;

/// <summary>
/// Construye el cuerpo Markdown (en español, redactado con <see cref="ISecretRedactor"/>) de los avisos terminales
/// en la issue de GitHub (FR-19, AD-8, AD-16 paso 7):
/// <list type="bullet">
/// <item><see cref="NotificationKind.PullRequestCreated"/>: enlace al PR (<c>#number</c> y <c>url</c>), <c>shortId</c> y clasificación de riesgo.</item>
/// <item><see cref="NotificationKind.RunFailed"/>: <see cref="FailureReason"/>, detalle redactado (si existe) y <c>shortId</c>.</item>
/// <item><see cref="NotificationKind.RunCancelled"/>: confirmación de cancelación y <c>shortId</c>.</item>
/// </list>
/// El módulo GitHub sanea el cuerpo antes de publicarlo y añade el marcador oculto de idempotencia.
/// </summary>
internal static class TerminalNotificationComment
{
    public const string PullRequestCreatedTitle = "CodeNetix ha abierto un pull request";

    public const string RunFailedTitle = "CodeNetix no ha podido completar esta issue";

    public const string RunCancelledTitle = "Ejecución de CodeNetix cancelada";

    public const int MaxDetailLength = RunOrchestrator.MaxDetailLength;

    public static string FailureReasonLabel(FailureReason reason) => reason switch
    {
        FailureReason.InvalidRepoConfig => "configuración del repositorio (`codenetix.yml`) no válida",
        FailureReason.CloneFailed => "no se pudo clonar el repositorio",
        FailureReason.NotAutomatable => "issue no automatizable",
        FailureReason.SchemaInvalid => "la salida del agente no cumplió el esquema esperado",
        FailureReason.TestsFailed => "la compilación o los tests no superaron la verificación",
        FailureReason.ReviewRejected => "la revisión independiente ha rechazado los cambios",
        FailureReason.ReviewRoundsExhausted => "se agotaron las rondas de revisión sin aprobación unánime",
        FailureReason.BudgetExceeded => "se superó el presupuesto configurado",
        FailureReason.Timeout => "se agotó el tiempo máximo de ejecución",
        FailureReason.HostRestarted => "el host se reinició durante la ejecución",
        FailureReason.StageError => "error interno durante una etapa de la ejecución",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "FailureReason desconocido."),
    };

    public static string BuildPullRequestCreated(
        string shortId,
        PullRequestInfo pullRequest,
        PolicyResult? policy,
        ISecretRedactor redactor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shortId);
        ArgumentNullException.ThrowIfNull(pullRequest);
        ArgumentNullException.ThrowIfNull(redactor);

        var sb = new StringBuilder();
        sb.Append("### ").AppendLine(PullRequestCreatedTitle);
        sb.AppendLine();
        sb.Append("- **Pull Request:** ");
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"[#{pullRequest.Number}]({pullRequest.Url})"));
        sb.Append("- **Ejecución:** `").Append(shortId).AppendLine("`");

        if (policy is not null)
        {
            var riskNote = policy.RiskClass == RiskClass.NeedsAttention
                ? " (requiere revisión detallada)"
                : string.Empty;
            sb.Append("- **Clasificación de riesgo:** `").Append(policy.RiskClass).Append('`').AppendLine(riskNote);
            if (policy.RiskClass == RiskClass.NeedsAttention && policy.MatchedProtectedPaths is { Count: > 0 })
            {
                const int MaxListed = 10;
                sb.Append("- **Rutas protegidas implicadas:** ");
                sb.Append(string.Join(", ", policy.MatchedProtectedPaths.Take(MaxListed).Select(p => $"`{p}`")));
                var remaining = policy.MatchedProtectedPaths.Count - MaxListed;
                if (remaining > 0)
                {
                    sb.Append(string.Create(CultureInfo.InvariantCulture, $" (+{remaining} más)"));
                }

                sb.AppendLine();
            }
        }

        sb.AppendLine();
        sb.Append("Revisa los cambios y los resultados de verificación en el pull request antes de hacer merge.");
        return NormalizeAndRedact(sb.ToString(), redactor);
    }

    public static string BuildRunFailed(
        string shortId,
        FailureReason reason,
        string? detail,
        ISecretRedactor redactor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shortId);
        ArgumentNullException.ThrowIfNull(redactor);
        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(reason), reason, "FailureReason desconocido.");
        }

        var sb = new StringBuilder();
        sb.Append("### ").AppendLine(RunFailedTitle);
        sb.AppendLine();
        sb.Append("- **Motivo:** `").Append(reason).Append("` — ").AppendLine(FailureReasonLabel(reason));
        sb.Append("- **Ejecución:** `").Append(shortId).AppendLine("`");

        if (!string.IsNullOrWhiteSpace(detail))
        {
            var redactedDetail = redactor.Redact(detail)
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Trim();
            redactedDetail = TextLimits.Truncate(redactedDetail, MaxDetailLength);
            if (!string.IsNullOrEmpty(redactedDetail))
            {
                sb.AppendLine();
                foreach (var line in redactedDetail.Split('\n'))
                {
                    sb.Append("> ").AppendLine(line.TrimEnd());
                }
            }
        }

        sb.AppendLine();
        sb.Append("No se ha abierto ningún pull request.");
        return NormalizeAndRedact(sb.ToString(), redactor);
    }

    public static string BuildRunCancelled(string shortId, ISecretRedactor redactor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shortId);
        ArgumentNullException.ThrowIfNull(redactor);

        var sb = new StringBuilder();
        sb.Append("### ").AppendLine(RunCancelledTitle);
        sb.AppendLine();
        sb.Append("La Ejecución `").Append(shortId).Append("` ha sido cancelada y no se abrirá ningún pull request.");
        return NormalizeAndRedact(sb.ToString(), redactor);
    }

    private static string NormalizeAndRedact(string text, ISecretRedactor redactor) =>
        redactor.Redact(text).Replace("\r\n", "\n", StringComparison.Ordinal);
}
