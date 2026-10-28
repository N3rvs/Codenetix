using System.Collections.Immutable;
using CodeNetix.Contracts;

namespace CodeNetix.Pipeline.Features.ResolveConfig;

/// <summary>
/// Calcula la <see cref="EffectiveConfig"/> (AD-10). El repositorio solo puede restringir:
/// <list type="bullet">
/// <item>Límites: el mínimo entre la instancia y el repositorio (un presupuesto del repositorio limita una instancia sin límite).</item>
/// <item>Rutas protegidas: las de por defecto más las del repositorio; las de por defecto nunca se quitan.</item>
/// <item>Revisores: los del repositorio o, si no los define, los de por defecto. Modelos: los del repositorio o nulos.</item>
/// </list>
/// </summary>
internal static class EffectiveConfigCalculator
{
    public static EffectiveConfig Calculate(InstanceLimits instance, RepoConfigFile? repo)
    {
        ArgumentNullException.ThrowIfNull(instance);

        var protectedPaths = EffectiveConfig.DefaultProtectedPaths
            .Concat(repo?.ProtectedPaths ?? [])
            .Distinct(StringComparer.Ordinal)
            .ToImmutableArray();

        var reviewers = repo?.Reviewers is { Count: > 0 } names
            ? names.Select(ParseReviewer).Distinct().ToImmutableArray()
            : EffectiveConfig.DefaultReviewers;

        var models = repo?.Models is { } m && (m.Analyst ?? m.Coder ?? m.Reviewers) is not null
            ? new AgentModels(m.Analyst, m.Coder, m.Reviewers)
            : null;

        return new EffectiveConfig(
            protectedPaths,
            reviewers,
            Min(instance.MaxReviewRounds, repo?.MaxReviewRounds),
            Min(instance.MaxCodingAttempts, repo?.MaxCodingAttempts),
            MinBudget(instance.BudgetUsd, repo?.BudgetUsd),
            models,
            repo is null ? ConfigSource.Defaults : ConfigSource.File);
    }

    private static int Min(int instance, int? repo) => repo is { } value ? Math.Min(instance, value) : instance;

    private static decimal? MinBudget(decimal? instance, decimal? repo) => (instance, repo) switch
    {
        ({ } a, { } b) => Math.Min(a, b),
        (null, { } b) => b,
        _ => instance,
    };

    private static ReviewerKind ParseReviewer(string name) => name switch
    {
        "security" => ReviewerKind.Security,
        "quality" => ReviewerKind.Quality,
        "tests" => ReviewerKind.Tests,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Revisor no admitido (el esquema debería haberlo rechazado)."),
    };
}
