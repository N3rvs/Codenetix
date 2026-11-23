using System.Collections.Immutable;
using System.Text.Json;
using CodeNetix.Contracts;

namespace CodeNetix.Pipeline.Domain;

/// <summary>Fila de <c>run_verdicts</c>: Veredicto persistido de un Revisor en una Ronda (AD-11, FR-15).</summary>
internal sealed class RunVerdictRecord
{
    public Guid Id { get; set; }

    public Guid RunId { get; set; }

    public Guid StageId { get; set; }

    public int Round { get; set; }

    public ReviewerKind Reviewer { get; set; }

    public VerdictDecision Decision { get; set; }

    public string Summary { get; set; } = string.Empty;

    /// <summary>JSON camelCase de <see cref="ImmutableArray{ReviewFinding}"/>.</summary>
    public string FindingsJson { get; set; } = "[]";

    public string Model { get; set; } = string.Empty;

    public int Attempts { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Serialización JSON de hallazgos de un Veredicto en <c>run_verdicts.findings_json</c> (camelCase).</summary>
internal static class VerdictFindingsSerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize(ImmutableArray<ReviewFinding> findings) =>
        JsonSerializer.Serialize(findings.IsDefault ? [] : findings, Options);

    public static ImmutableArray<ReviewFinding> Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        var parsed = JsonSerializer.Deserialize<ImmutableArray<ReviewFinding>>(json, Options);
        return parsed.IsDefault ? [] : parsed;
    }
}
