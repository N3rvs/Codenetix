namespace CodeNetix.Pipeline;

/// <summary>
/// Límites de la instancia (<c>CODENETIX__Limits__*</c>, FR-3). Host enlaza la sección <c>CODENETIX:Limits</c>.
/// <c>codenetix.yml</c> solo puede restringirlos.
/// </summary>
public sealed class InstanceLimits
{
    public const int DefaultMaxReviewRounds = 2;
    public const int DefaultMaxCodingAttempts = 3;

    /// <summary><c>CODENETIX__Limits__MaxReviewRounds</c> (por defecto 2).</summary>
    public int MaxReviewRounds { get; set; } = DefaultMaxReviewRounds;

    /// <summary><c>CODENETIX__Limits__MaxCodingAttempts</c> (por defecto 3).</summary>
    public int MaxCodingAttempts { get; set; } = DefaultMaxCodingAttempts;

    /// <summary><c>CODENETIX__Limits__BudgetUsd</c>: presupuesto por Ejecución; <c>null</c> = sin límite.</summary>
    public decimal? BudgetUsd { get; set; }
}
