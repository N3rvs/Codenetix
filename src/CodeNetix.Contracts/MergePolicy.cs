using System.Text.Json.Serialization;

namespace CodeNetix.Contracts;

/// <summary>Clasificación determinista de riesgo de un cambio (AD-9, FR-18).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RiskClass>))]
public enum RiskClass
{
    Minor,
    NeedsAttention,
}

/// <summary>Acción resultante de la política de decisión (AD-9, FR-18). En la v1 es siempre abrir PR.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PolicyAction>))]
public enum PolicyAction
{
    CreatePullRequest,
}

/// <summary>
/// Entrada pura de <c>MergePolicy.Classify</c> (AD-9, FR-18, NFR-6): únicamente datos ya resueltos de la Ejecución
/// (sin E/S, reloj, aleatoriedad ni LLM).
/// </summary>
public sealed record PolicyInput(
    IReadOnlyList<string> ChangedFiles,
    int DiffLines,
    IReadOnlyList<string> ProtectedPaths,
    IReadOnlyList<VerdictDecision> Verdicts,
    int Round,
    int MaxMinorDiffLines = PolicyInput.DefaultMaxMinorDiffLines)
{
    /// <summary>Umbral por defecto de líneas cambiadas para clasificar como <see cref="RiskClass.Minor"/> (FR-18).</summary>
    public const int DefaultMaxMinorDiffLines = 400;
}

/// <summary>Resultado determinista de <c>MergePolicy.Classify</c> (AD-9, FR-18).</summary>
public sealed record PolicyResult(
    RiskClass RiskClass,
    PolicyAction Action,
    IReadOnlyList<string> MatchedProtectedPaths,
    IReadOnlyList<string> Reasons);
