using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace CodeNetix.Contracts;

/// <summary>Decisión de un Revisor independiente (FR-15). Se serializa en snake_case, igual que en el esquema.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<VerdictDecision>))]
public enum VerdictDecision
{
    [JsonStringEnumMemberName("approved")]
    Approved,

    [JsonStringEnumMemberName("changes_requested")]
    ChangesRequested,

    [JsonStringEnumMemberName("rejected")]
    Rejected,
}

/// <summary>Hallazgo estructurado dentro de un <see cref="Verdict"/> (FR-15).</summary>
public sealed record ReviewFinding(
    string Severity,
    string File,
    int? Line,
    string Message);

/// <summary>
/// Veredicto estructurado emitido por un Revisor (FR-15, AD-5). Su forma la fija <see cref="VerdictSchema"/>
/// (<c>schemas/verdict.v1.json</c>).
/// </summary>
public sealed record Verdict(
    VerdictDecision Decision,
    string Summary,
    ImmutableArray<ReviewFinding> Findings)
{
    public ImmutableArray<ReviewFinding> Findings { get; init; } = Findings.IsDefault ? [] : Findings;
}

/// <summary>Veredicto persistido en <c>run_verdicts</c> y expuesto en <c>GET /api/runs/{id}</c> (AD-11).</summary>
public sealed record RunVerdict(
    Guid StageId,
    int Round,
    ReviewerKind Reviewer,
    VerdictDecision Decision,
    string Summary,
    ImmutableArray<ReviewFinding> Findings,
    string Model,
    int Attempts,
    DateTimeOffset CreatedAt)
{
    public ImmutableArray<ReviewFinding> Findings { get; init; } = Findings.IsDefault ? [] : Findings;
}

/// <summary>Diff calculado en el host contra <c>BaseSha</c> (AD-8).</summary>
public sealed record DiffResult(
    string UnifiedDiff,
    ImmutableArray<string> ChangedFiles,
    bool Truncated = false)
{
    public ImmutableArray<string> ChangedFiles { get; init; } = ChangedFiles.IsDefault ? [] : ChangedFiles;

    /// <summary>Devuelve una copia con <see cref="UnifiedDiff"/> recortado de forma segura si supera <paramref name="maxLength"/>.</summary>
    public DiffResult Truncate(int maxLength)
    {
        if (maxLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLength));
        }

        if (UnifiedDiff.Length <= maxLength)
        {
            return this;
        }

        return this with
        {
            UnifiedDiff = TextLimits.Truncate(UnifiedDiff, maxLength),
            Truncated = true,
        };
    }
}

/// <summary>
/// Entrada aislada de un Revisor independiente (FR-14, AD-5): contiene exclusivamente <c>Spec</c>, <c>Diff</c> y
/// <c>TestReport</c> junto a los metadatos de la Ronda/Etapa y la configuración efectiva. Nunca incluye historial,
/// prompts ni salida del Programador.
/// </summary>
public sealed record ReviewInput(
    Guid RunId,
    Guid StageId,
    int Round,
    ReviewerKind ReviewerKind,
    Spec Spec,
    DiffResult Diff,
    TestReport TestReport,
    EffectiveConfig Config)
{
    /// <summary>Límite seguro del diff enviado al Revisor.</summary>
    public const int MaxDiffLength = 100_000;
}

/// <summary>Resultado de <see cref="IReviewerAgent.ReviewAsync"/>.</summary>
public sealed record ReviewResult(
    Verdict Verdict,
    int Attempts,
    string Model,
    bool FallbackDueToError = false);

/// <summary>
/// Revisor independiente (FR-14, FR-15, AD-5): audita el diff frente a la Spec y el TestReport sin ejecutar código
/// ni ver la salida del Programador. Ante esquema inválido tras agotar reintentos o error de proveedor, devuelve
/// <see cref="VerdictDecision.ChangesRequested"/> con el detalle en <see cref="Verdict.Summary"/>.
/// </summary>
public interface IReviewerAgent
{
    Task<ReviewResult> ReviewAsync(ReviewInput input, CancellationToken cancellationToken);
}

/// <summary>Esquema JSON (2020-12) de <see cref="Verdict"/>: <c>schemas/verdict.v1.json</c>, incrustado en Contracts.</summary>
public static class VerdictSchema
{
    public const string Name = "verdict";

    public const string Id = "https://codenetix.dev/schemas/verdict.v1.json";

    public const int MaxFindings = 50;

    private static readonly Lazy<string> Text = new(() => EmbeddedSchemas.Load("verdict.v1.json"), LazyThreadSafetyMode.ExecutionAndPublication);

    public static string Json => Text.Value;
}

/// <summary>Reglas semánticas de <see cref="Verdict"/> que complementan <see cref="VerdictSchema"/>.</summary>
public static class VerdictRules
{
    private static readonly HashSet<string> AllowedSeverities = new(StringComparer.Ordinal)
    {
        "critical",
        "high",
        "medium",
        "low",
        "info",
    };

    public static string? Validate(Verdict verdict)
    {
        ArgumentNullException.ThrowIfNull(verdict);

        if (!Enum.IsDefined(verdict.Decision))
        {
            return "decision: valor no admitido";
        }

        if (string.IsNullOrWhiteSpace(verdict.Summary))
        {
            return "summary está vacío";
        }

        if (verdict.Findings.Length > VerdictSchema.MaxFindings)
        {
            return $"findings debe tener como mucho {VerdictSchema.MaxFindings} elementos";
        }

        for (var i = 0; i < verdict.Findings.Length; i++)
        {
            var finding = verdict.Findings[i];
            if (finding is null)
            {
                return $"findings[{i}] es nulo";
            }

            if (string.IsNullOrWhiteSpace(finding.Severity) || !AllowedSeverities.Contains(finding.Severity))
            {
                return $"findings[{i}].severity: valor no admitido";
            }

            if (!SpecRules.IsSafeRelativePath(finding.File))
            {
                return $"findings[{i}].file no es una ruta relativa válida";
            }

            if (finding.Line is <= 0)
            {
                return $"findings[{i}].line debe ser mayor o igual que 1";
            }

            if (string.IsNullOrWhiteSpace(finding.Message))
            {
                return $"findings[{i}].message está vacío";
            }
        }

        return null;
    }
}
