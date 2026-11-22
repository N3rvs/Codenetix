using System.Text.Json.Serialization;

namespace CodeNetix.Contracts;

/// <summary>
/// Tarifa de un modelo en dólares estadounidenses (USD) por cada millón de tokens de entrada y de salida (AD-15).
/// </summary>
public sealed record ModelPricing(decimal InputPerMillionUsd, decimal OutputPerMillionUsd);

/// <summary>
/// Petición para anotar un consumo de tokens en el libro de presupuesto (<c>run_budget</c>) y emitir
/// <see cref="TokensConsumed"/> de forma atómica (AD-15).
/// </summary>
public sealed record BudgetChargeRequest(
    Guid RunId,
    Guid? StageId,
    string Role,
    string Provider,
    string Model,
    int InputTokens,
    int OutputTokens);

/// <summary>Resultado de comprobar el presupuesto al registrar un cargo (AD-15).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<BudgetChargeOutcome>))]
public enum BudgetChargeOutcome
{
    Recorded,
    Exhausted,
}

/// <summary>Resultado detallado de <see cref="IBudgetLedger.ChargeAsync"/>.</summary>
public sealed record BudgetChargeResult(
    BudgetChargeOutcome Outcome,
    decimal? ChargeUsd,
    decimal? TotalEstimatedCostUsd,
    bool HasUnpricedModels,
    decimal? BudgetLimitUsd,
    decimal? RemainingUsd);

/// <summary>
/// Resumen agregado de coste y consumo de tokens de una Ejecución (AD-15).
/// Si intervino cualquier modelo sin precio conocido (incluidos modelos locales),
/// <see cref="IsEstimable"/> es <c>false</c>, <see cref="HasUnpricedModels"/> es <c>true</c>
/// y <see cref="EstimatedCostUsd"/> es <c>null</c>.
/// </summary>
public sealed record RunCostSummary(
    decimal? EstimatedCostUsd,
    bool IsEstimable,
    bool HasUnpricedModels,
    int TotalInputTokens,
    int TotalOutputTokens,
    decimal? BudgetLimitUsd)
{
    /// <summary>Resumen vacío cuando la Ejecución todavía no tiene cargos registrados.</summary>
    public static RunCostSummary Empty { get; } = new(
        EstimatedCostUsd: null,
        IsEstimable: false,
        HasUnpricedModels: false,
        TotalInputTokens: 0,
        TotalOutputTokens: 0,
        BudgetLimitUsd: null);
}

/// <summary>
/// Libro de presupuesto de Ejecuciones (AD-15), implementado por Pipeline.
/// Es el único punto de registro de consumo: persiste cada cargo en <c>run_budget</c> y emite
/// <see cref="TokensConsumed"/> en la misma transacción.
/// </summary>
public interface IBudgetLedger
{
    /// <summary>Registra un consumo de tokens y devuelve el estado actualizado del presupuesto.</summary>
    Task<BudgetChargeResult> ChargeAsync(BudgetChargeRequest request, CancellationToken cancellationToken);

    /// <summary>Devuelve el resumen agregado de coste y tokens de <paramref name="runId"/>.</summary>
    Task<RunCostSummary> GetRunCostAsync(Guid runId, CancellationToken cancellationToken);
}
