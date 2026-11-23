using CodeNetix.Contracts;

namespace CodeNetix.Pipeline.Features.Budget;

/// <summary>
/// Overrides de precios por modelo enlazados desde la Configuración de instancia
/// (<c>CODENETIX__Pricing__&lt;Modelo&gt;__InputPerMillionUsd</c> / <c>OutputPerMillionUsd</c>, AD-15).
/// </summary>
public sealed class PricingOptions
{
    public Dictionary<string, ModelPricing> Models { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
