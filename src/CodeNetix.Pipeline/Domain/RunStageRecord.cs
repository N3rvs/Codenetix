using CodeNetix.Contracts;

namespace CodeNetix.Pipeline.Domain;

/// <summary>Fila de <c>run_stages</c>: Etapa individual de una Ejecución (AD-11).</summary>
internal sealed class RunStageRecord
{
    public Guid Id { get; set; }

    public Guid RunId { get; set; }

    public StageKind Kind { get; set; }

    public int Round { get; set; }

    public ReviewerKind? Reviewer { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }
}
