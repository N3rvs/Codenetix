using System.Collections.Frozen;
using System.Text.RegularExpressions;
using CodeNetix.Contracts;

namespace CodeNetix.Pipeline.Features.Budget;

/// <summary>
/// Catálogo por defecto de precios por modelo (USD por cada millón de tokens de entrada y de salida, AD-15).
/// Los overrides configurados en la instancia tienen prioridad sobre el catálogo por defecto.
/// Si un modelo no figura ni en los overrides ni en el catálogo (p. ej. modelos locales), no se inventa coste.
/// </summary>
internal static partial class ModelPricingCatalog
{
    private const decimal TokensPerMillion = 1_000_000m;

    /// <summary>Precios por defecto documentados (USD / 1M tokens entrada y salida).</summary>
    public static FrozenDictionary<string, ModelPricing> Defaults { get; } = new Dictionary<string, ModelPricing>(StringComparer.OrdinalIgnoreCase)
    {
        // Anthropic Claude
        ["claude-sonnet-5-5"] = new(3.00m, 15.00m),
        ["claude-sonnet-4-5"] = new(3.00m, 15.00m),
        ["claude-sonnet-4"] = new(3.00m, 15.00m),
        ["claude-opus-4"] = new(15.00m, 75.00m),
        ["claude-haiku-4-5"] = new(0.80m, 4.00m),
        ["claude-3-7-sonnet"] = new(3.00m, 15.00m),
        ["claude-3-5-sonnet"] = new(3.00m, 15.00m),
        ["claude-3-5-haiku"] = new(0.80m, 4.00m),

        // OpenAI
        ["gpt-5"] = new(2.50m, 10.00m),
        ["gpt-5-mini"] = new(0.25m, 2.00m),
        ["gpt-4.1"] = new(2.00m, 8.00m),
        ["gpt-4.1-mini"] = new(0.40m, 1.60m),
        ["gpt-4o"] = new(2.50m, 10.00m),
        ["gpt-4o-mini"] = new(0.15m, 0.60m),
        ["o3"] = new(2.00m, 8.00m),
        ["o4-mini"] = new(1.10m, 4.40m),
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Resuelve el precio de <paramref name="model"/>: primero en <paramref name="overrides"/> (insensible a
    /// mayúsculas), después en <see cref="Defaults"/> (coincidencia exacta o eliminando el sufijo de fecha
    /// <c>-YYYYMMDD</c>, <c>@YYYYMMDD</c> o <c>-YYYY-MM-DD</c>). Devuelve <c>false</c> si no tiene precio conocido.
    /// </summary>
    public static bool TryResolve(
        string? model,
        IReadOnlyDictionary<string, ModelPricing>? overrides,
        out ModelPricing pricing)
    {
        pricing = default!;
        if (string.IsNullOrWhiteSpace(model))
        {
            return false;
        }

        var normalized = model.Trim();
        var withoutDate = StripDateSuffix(normalized);

        if (overrides is { Count: > 0 })
        {
            if (TryLookupCaseInsensitive(overrides, normalized, out pricing))
            {
                return true;
            }

            if (withoutDate is not null && TryLookupCaseInsensitive(overrides, withoutDate, out pricing))
            {
                return true;
            }
        }

        if (Defaults.TryGetValue(normalized, out pricing!))
        {
            return true;
        }

        if (withoutDate is not null && Defaults.TryGetValue(withoutDate, out pricing!))
        {
            return true;
        }

        // Fallback acotado en Defaults: retira únicamente prefijos oficiales de nube (anthropic/, openai/)
        // —nunca ollama/ ni local/— y/o el sufijo -latest junto con posibles sufijos de fecha.
        var canonical = StripOfficialCloudPrefixAndLatest(normalized);
        if (!string.Equals(canonical, normalized, StringComparison.OrdinalIgnoreCase))
        {
            if (Defaults.TryGetValue(canonical, out pricing!))
            {
                return true;
            }

            var canonicalWithoutDate = StripDateSuffix(canonical);
            if (canonicalWithoutDate is not null && Defaults.TryGetValue(canonicalWithoutDate, out pricing!))
            {
                return true;
            }
        }

        return false;
    }

    private static string StripOfficialCloudPrefixAndLatest(string model)
    {
        var result = model;
        if (result.StartsWith("anthropic/", StringComparison.OrdinalIgnoreCase))
        {
            result = result["anthropic/".Length..];
        }
        else if (result.StartsWith("openai/", StringComparison.OrdinalIgnoreCase))
        {
            result = result["openai/".Length..];
        }

        if (result.EndsWith("-latest", StringComparison.OrdinalIgnoreCase))
        {
            result = result[..^"-latest".Length];
        }

        return result;
    }

    /// <summary>Calcula el coste en USD de una llamada, redondeado a 8 decimales para preservar llamadas pequeñas.</summary>
    public static decimal CalculateChargeUsd(int inputTokens, int outputTokens, ModelPricing pricing)
    {
        ArgumentNullException.ThrowIfNull(pricing);
        var safeInput = Math.Max(0, inputTokens);
        var safeOutput = Math.Max(0, outputTokens);
        var raw = (safeInput * pricing.InputPerMillionUsd + safeOutput * pricing.OutputPerMillionUsd) / TokensPerMillion;
        return decimal.Round(raw, 8, MidpointRounding.AwayFromZero);
    }

    private static bool TryLookupCaseInsensitive(
        IReadOnlyDictionary<string, ModelPricing> source,
        string key,
        out ModelPricing pricing)
    {
        if (source.TryGetValue(key, out pricing!))
        {
            return true;
        }

        foreach (var entry in source)
        {
            if (string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                pricing = entry.Value;
                return true;
            }
        }

        pricing = default!;
        return false;
    }

    private static string? StripDateSuffix(string model)
    {
        var match = DateSuffixRegex().Match(model);
        return match.Success ? model[..match.Index] : null;
    }

    [GeneratedRegex(@"(?:[-@]\d{8}|-\d{4}-\d{2}-\d{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex DateSuffixRegex();
}
