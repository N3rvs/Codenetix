using System.Globalization;
using System.Text;
using CodeNetix.Contracts;

namespace CodeNetix.Pipeline.Features.RunPipeline;

/// <summary>
/// Construye título, mensaje de commit y cuerpo Markdown trazable del Pull Request (FR-17, AD-8),
/// aplicando <see cref="ISecretRedactor"/> a todo el contenido generado.
/// </summary>
internal static class PullRequestBodyBuilder
{
    private const int MaxTitleLength = 200;
    private const int MaxNewTestsListed = 50;
    private const int MaxProtectedPathsListed = 50;

    public const string NeedsAttentionTitlePrefix = "[Requiere atención] ";

    public static string BuildTitle(long issueNumber, string issueTitle, ISecretRedactor redactor, PolicyResult? policy = null)
    {
        ArgumentNullException.ThrowIfNull(redactor);
        var cleanIssueTitle = CollapseSingleLine(issueTitle);
        var raw = string.IsNullOrEmpty(cleanIssueTitle)
            ? string.Create(CultureInfo.InvariantCulture, $"fix: resolver #{issueNumber}")
            : string.Create(CultureInfo.InvariantCulture, $"fix(#{issueNumber}): {cleanIssueTitle}");
        if (policy?.RiskClass == RiskClass.NeedsAttention)
        {
            raw = NeedsAttentionTitlePrefix + raw;
        }

        return TextLimits.Truncate(redactor.Redact(raw), MaxTitleLength);
    }

    public static string BuildCommitMessage(Guid runId, long issueNumber, string issueTitle, ISecretRedactor redactor)
    {
        ArgumentNullException.ThrowIfNull(redactor);
        var subject = BuildTitle(issueNumber, issueTitle, redactor);
        var raw = string.Create(
            CultureInfo.InvariantCulture,
            $"{subject}\n\nRefs #{issueNumber}\nCodeNetix-Run: {runId:D}");
        return redactor.Redact(raw);
    }

    public static string BuildBody(
        Guid runId,
        Guid eventId,
        string owner,
        string repo,
        long issueNumber,
        Spec spec,
        IReadOnlyList<RunVerdict> verdicts,
        TestReport testReport,
        ISecretRedactor redactor,
        decimal? costUsd = null,
        PolicyResult? policy = null,
        RunCostSummary? cost = null)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(verdicts);
        ArgumentNullException.ThrowIfNull(testReport);
        ArgumentNullException.ThrowIfNull(redactor);

        var sb = new StringBuilder(2048);

        // 1. Origen / Evento
        sb.AppendLine("## Origen");
        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"- Issue: https://github.com/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/issues/{issueNumber} (Closes #{issueNumber})"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"- Evento: {InlineCode(eventId.ToString("D"))}"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"- Ejecución: {InlineCode(runId.ToString("D"))} ({InlineCode(RunShortId.Of(runId))})"));
        sb.AppendLine();

        // 2. Spec
        sb.AppendLine("## Spec");
        sb.AppendLine($"**Problema:** {spec.Problem.Trim()}");
        sb.AppendLine();
        sb.AppendLine($"**Causa probable:** {spec.ProbableCause.Trim()}");
        sb.AppendLine();
        sb.AppendLine($"**Cambio propuesto:** {spec.ProposedChange.Trim()}");
        sb.AppendLine();
        sb.AppendLine("### Criterios de aceptación");
        foreach (var criterion in spec.AcceptanceCriteria)
        {
            sb.AppendLine($"- {criterion.Trim()}");
        }

        sb.AppendLine();

        // 3. Veredictos de revisión
        sb.AppendLine("## Veredictos de revisión");
        if (verdicts.Count == 0)
        {
            sb.AppendLine("_Sin veredictos registrados._");
        }
        else
        {
            foreach (var verdict in verdicts)
            {
                var reviewerName = verdict.Reviewer.ToString().ToLowerInvariant();
                var decisionName = verdict.Decision switch
                {
                    VerdictDecision.Approved => "approved",
                    VerdictDecision.ChangesRequested => "changes_requested",
                    VerdictDecision.Rejected => "rejected",
                    _ => verdict.Decision.ToString(),
                };
                sb.AppendLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"- **Ronda {verdict.Round} — {reviewerName}** ({InlineCode(decisionName)} · {InlineCode(CollapseSingleLine(verdict.Model))}): {CollapseSingleLine(verdict.Summary)}"));

                foreach (var finding in verdict.Findings)
                {
                    var location = finding.Line is > 0
                        ? string.Create(CultureInfo.InvariantCulture, $"{finding.File}:{finding.Line.Value}")
                        : finding.File;
                    sb.AppendLine($"  - {InlineCode($"[{CollapseSingleLine(finding.Severity)}]")} {InlineCode(CollapseSingleLine(location))}: {CollapseSingleLine(finding.Message)}");
                }
            }
        }

        sb.AppendLine();

        // 4. TestReport
        sb.AppendLine("## Informe de tests");
        sb.AppendLine($"- Compilación: {(testReport.BuildSucceeded ? "OK" : "Fallida")}");
        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"- Tests: {testReport.TotalTests} totales, {testReport.PassedTests} pasados, {testReport.FailedTests} fallidos, {testReport.SkippedTests} omitidos"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"- Tests nuevos: {testReport.NewTests.Count}"));
        foreach (var testName in testReport.NewTests.Take(MaxNewTestsListed))
        {
            sb.AppendLine($"  - {InlineCode(CollapseSingleLine(testName))}");
        }

        if (testReport.NewTests.Count > MaxNewTestsListed)
        {
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  - … y {testReport.NewTests.Count - MaxNewTestsListed} más"));
        }

        sb.AppendLine();

        // 5. Clasificación de riesgo (FR-18)
        if (policy is not null)
        {
            sb.AppendLine("## Clasificación de riesgo");
            sb.AppendLine($"- Clasificación: {InlineCode(policy.RiskClass.ToString())}");
            if (policy.RiskClass == RiskClass.NeedsAttention)
            {
                if (policy.Reasons is { Count: > 0 })
                {
                    sb.AppendLine("- Motivos:");
                    foreach (var reason in policy.Reasons)
                    {
                        sb.AppendLine($"  - {CollapseSingleLine(reason)}");
                    }
                }

                if (policy.MatchedProtectedPaths is { Count: > 0 })
                {
                    sb.AppendLine("- Rutas protegidas implicadas:");
                    foreach (var path in policy.MatchedProtectedPaths.Take(MaxProtectedPathsListed))
                    {
                        sb.AppendLine($"  - {InlineCode(CollapseSingleLine(path))}");
                    }

                    if (policy.MatchedProtectedPaths.Count > MaxProtectedPathsListed)
                    {
                        sb.AppendLine(string.Create(
                            CultureInfo.InvariantCulture,
                            $"  - … y {policy.MatchedProtectedPaths.Count - MaxProtectedPathsListed} ruta(s) más"));
                    }
                }
            }
            else if (policy.RiskClass == RiskClass.Minor)
            {
                sb.AppendLine("- Cambio menor: sin rutas protegidas, dentro del umbral de diff y aprobado por unanimidad en la ronda 1.");
            }

            sb.AppendLine();
        }

        // 6. Coste
        sb.AppendLine("## Coste");
        if (cost is { IsEstimable: true, EstimatedCostUsd: { } usd })
        {
            sb.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"- Coste estimado: ${usd:F4} USD ({cost.TotalInputTokens} tokens entrada / {cost.TotalOutputTokens} tokens salida)"));
        }
        else if (cost is { HasUnpricedModels: true })
        {
            sb.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"- Coste: no estimable ({cost.TotalInputTokens} tokens entrada / {cost.TotalOutputTokens} tokens salida; modelo sin precio conocido)"));
        }
        else if (cost is null && costUsd.HasValue)
        {
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"- Coste estimado: ${costUsd.Value:F4} USD"));
        }
        else
        {
            sb.AppendLine("- Coste: no disponible");
        }

        return redactor.Redact(sb.ToString().TrimEnd());
    }

    private static string InlineCode(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "``";
        }

        var maxRun = 0;
        var current = 0;
        foreach (var c in value)
        {
            if (c == '`')
            {
                current++;
                if (current > maxRun)
                {
                    maxRun = current;
                }
            }
            else
            {
                current = 0;
            }
        }

        var fence = new string('`', maxRun + 1);
        var pad = value[0] == '`' || value[^1] == '`' || value[0] == ' ' || value[^1] == ' ';
        return pad ? $"{fence} {value} {fence}" : $"{fence}{value}{fence}";
    }

    private static string CollapseSingleLine(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : string.Join(' ', value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
