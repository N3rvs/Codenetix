namespace CodeNetix.Pipeline.Features.DecideMerge;

/// <summary>
/// Matcher glob puro y determinista sin E/S ni expresiones regulares (AD-9, NFR-6).
/// Soporta <c>**</c> (cero o más segmentos de ruta), <c>*</c> (cero o más caracteres dentro de un segmento)
/// y <c>?</c> (un carácter dentro de un segmento), normalizando separadores <c>\</c> y <c>/</c>.
/// </summary>
public static class GlobMatcher
{
    /// <summary>Normaliza una ruta relativa a separadores <c>/</c>, resolviendo segmentos <c>.</c> y <c>..</c>.</summary>
    public static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var rawSegments = path.Trim().Replace('\\', '/').Split('/');
        var resolved = new List<string>(rawSegments.Length);
        foreach (var segment in rawSegments)
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (resolved.Count > 0)
                {
                    resolved.RemoveAt(resolved.Count - 1);
                }

                continue;
            }

            resolved.Add(segment);
        }

        return resolved.Count == 0 ? string.Empty : string.Join('/', resolved);
    }

    /// <summary>Comprueba si <paramref name="path"/> coincide con el patrón glob <paramref name="pattern"/>.</summary>
    public static bool IsMatch(string path, string pattern)
    {
        var normalizedPath = NormalizePath(path);
        var normalizedPattern = NormalizePath(pattern);
        if (normalizedPath.Length == 0 || normalizedPattern.Length == 0)
        {
            return false;
        }

        var pathSegments = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var patternSegments = normalizedPattern.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (pathSegments.Length == 0 || patternSegments.Length == 0)
        {
            return false;
        }

        var pLen = patternSegments.Length;
        var sLen = pathSegments.Length;

        // Programación dinámica sobre segmentos: dp[p, s] = patternSegments[p..] coincide con pathSegments[s..].
        var dp = new bool[pLen + 1, sLen + 1];
        dp[pLen, sLen] = true;

        for (var p = pLen - 1; p >= 0; p--)
        {
            if (patternSegments[p] == "**")
            {
                dp[p, sLen] = dp[p + 1, sLen];
            }
        }

        for (var p = pLen - 1; p >= 0; p--)
        {
            var patSeg = patternSegments[p];
            if (patSeg == "**")
            {
                for (var s = sLen - 1; s >= 0; s--)
                {
                    // "**" coincide con 0 segmentos (dp[p + 1, s]) o consume 1 segmento y sigue (dp[p, s + 1]).
                    dp[p, s] = dp[p + 1, s] || dp[p, s + 1];
                }
            }
            else
            {
                for (var s = sLen - 1; s >= 0; s--)
                {
                    dp[p, s] = dp[p + 1, s + 1] && MatchSegment(pathSegments[s], patSeg);
                }
            }
        }

        return dp[0, 0];
    }

    private static bool MatchSegment(ReadOnlySpan<char> text, ReadOnlySpan<char> pattern)
    {
        var t = 0;
        var p = 0;
        var starIdx = -1;
        var matchIdx = 0;

        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || pattern[p] == text[t]))
            {
                t++;
                p++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                starIdx = p;
                matchIdx = t;
                p++;
            }
            else if (starIdx != -1)
            {
                p = starIdx + 1;
                matchIdx++;
                t = matchIdx;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }
}
