using System.Globalization;
using CodeNetix.Contracts;

namespace CodeNetix.Pipeline.Features.DecideMerge;

/// <summary>
/// Política de clasificación de riesgo determinista (AD-9, FR-18, NFR-6): función pura sin E/S, reloj,
/// aleatoriedad ni LLM. En la v1 el destino es siempre <see cref="PolicyAction.CreatePullRequest"/>.
/// </summary>
public static class MergePolicy
{
    /// <summary>
    /// Clasifica un cambio como <see cref="RiskClass.Minor"/> o <see cref="RiskClass.NeedsAttention"/>
    /// de forma puramente determinista.
    /// </summary>
    public static PolicyResult Classify(PolicyInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentOutOfRangeException.ThrowIfNegative(input.DiffLines);
        ArgumentOutOfRangeException.ThrowIfNegative(input.MaxMinorDiffLines);

        var matchedSet = new HashSet<string>(StringComparer.Ordinal);
        if (input.ChangedFiles is { Count: > 0 } && input.ProtectedPaths is { Count: > 0 })
        {
            foreach (var file in input.ChangedFiles)
            {
                var normalized = GlobMatcher.NormalizePath(file);
                if (normalized.Length == 0)
                {
                    continue;
                }

                foreach (var pattern in input.ProtectedPaths)
                {
                    if (GlobMatcher.IsMatch(normalized, pattern))
                    {
                        matchedSet.Add(normalized);
                        break;
                    }
                }
            }
        }

        var matchedProtectedPaths = matchedSet.Count == 0
            ? Array.Empty<string>()
            : matchedSet.OrderBy(static p => p, StringComparer.Ordinal).ToArray();

        var reasons = new List<string>(3);

        // 1. Rutas protegidas
        if (matchedProtectedPaths.Length > 0)
        {
            reasons.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"Toca rutas protegidas ({matchedProtectedPaths.Length}): {string.Join(", ", matchedProtectedPaths)}"));
        }

        // 2. Umbral de líneas del diff
        if (input.DiffLines > input.MaxMinorDiffLines)
        {
            reasons.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"El diff supera el umbral de cambios menores ({input.DiffLines} > {input.MaxMinorDiffLines} líneas)"));
        }

        // 3. Unanimidad en la primera ronda
        var hasVerdicts = input.Verdicts is { Count: > 0 };
        var allApproved = hasVerdicts && input.Verdicts.All(static v => v == VerdictDecision.Approved);
        if (input.Round != 1)
        {
            reasons.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"No hubo unanimidad en la primera ronda (ronda {input.Round})"));
        }

        if (!hasVerdicts)
        {
            reasons.Add("No hay veredictos de revisión aprobados");
        }
        else if (!allApproved)
        {
            reasons.Add("No todos los veredictos de revisión son Approved");
        }

        var riskClass = reasons.Count == 0 ? RiskClass.Minor : RiskClass.NeedsAttention;
        return new PolicyResult(
            riskClass,
            PolicyAction.CreatePullRequest,
            matchedProtectedPaths,
            reasons);
    }

    /// <summary>
    /// Cuenta las líneas cambiadas (<c>+</c> y <c>-</c>) de un diff unificado, excluyendo las cabeceras
    /// <c>+++</c> y <c>---</c>.
    /// </summary>
    public static int CountDiffLines(string? unifiedDiff)
    {
        if (string.IsNullOrEmpty(unifiedDiff))
        {
            return 0;
        }

        var count = 0;
        var inHunk = false;
        var span = unifiedDiff.AsSpan();
        while (!span.IsEmpty)
        {
            var newlineIndex = span.IndexOfAny('\r', '\n');
            ReadOnlySpan<char> line;
            if (newlineIndex < 0)
            {
                line = span;
                span = [];
            }
            else
            {
                line = span[..newlineIndex];
                var advance = newlineIndex + 1;
                if (span[newlineIndex] == '\r' && advance < span.Length && span[advance] == '\n')
                {
                    advance++;
                }

                span = span[advance..];
            }

            if (line.IsEmpty)
            {
                continue;
            }

            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                inHunk = false;
                continue;
            }

            if (line.StartsWith("@@ ", StringComparison.Ordinal))
            {
                inHunk = true;
                continue;
            }

            if (inHunk)
            {
                if (line[0] is '+' or '-')
                {
                    count++;
                }
            }
            else
            {
                if (line[0] == '+'
                    && !line.StartsWith("+++ ", StringComparison.Ordinal)
                    && !line.Equals("+++", StringComparison.Ordinal))
                {
                    count++;
                }
                else if (line[0] == '-'
                    && !line.StartsWith("--- ", StringComparison.Ordinal)
                    && !line.Equals("---", StringComparison.Ordinal))
                {
                    count++;
                }
            }
        }

        return count;
    }
}
