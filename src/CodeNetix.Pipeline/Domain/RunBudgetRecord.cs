namespace CodeNetix.Pipeline.Domain;

/// <summary>
/// Fila de <c>run_budget</c> (AD-15): un cargo individual de tokens y su coste calculado (o <c>null</c> si el modelo
/// no tiene precio conocido en el catálogo ni en la Configuración de instancia).
/// </summary>
internal sealed class RunBudgetRecord
{
    public Guid Id { get; set; }

    public Guid RunId { get; set; }

    public Guid? StageId { get; set; }

    public string Role { get; set; } = string.Empty;

    public string Provider { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    public int InputTokens { get; set; }

    public int OutputTokens { get; set; }

    public decimal? CostUsd { get; set; }

    public bool IsPriced { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
