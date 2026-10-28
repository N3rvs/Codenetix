namespace CodeNetix.Contracts;

/// <summary>Recorte de textos para mostrar o persistir.</summary>
public static class TextLimits
{
    /// <summary>
    /// <paramref name="value"/> si cabe en <paramref name="maxLength"/>; si no, su principio más <c>…</c>, con
    /// <paramref name="maxLength"/> caracteres como mucho y sin partir un par sustituto (UTF-16).
    /// </summary>
    public static string Truncate(string value, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 2);
        if (value.Length <= maxLength)
        {
            return value;
        }

        var cut = maxLength - 1;
        if (char.IsHighSurrogate(value[cut - 1]))
        {
            cut--;
        }

        return string.Concat(value.AsSpan(0, cut), "…");
    }
}
