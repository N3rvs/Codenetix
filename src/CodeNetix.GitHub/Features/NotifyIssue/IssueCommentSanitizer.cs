using System.Text;
using System.Text.RegularExpressions;
using CodeNetix.Contracts;

namespace CodeNetix.GitHub.Features.NotifyIssue;

/// <summary>
/// Sanea Markdown no confiable (p. ej. la explicación de un modelo) antes de publicarlo en una issue:
/// <list type="bullet">
/// <item>sin caracteres de control (salvo saltos de línea, normalizados a <c>\n</c>, y tabuladores);</item>
/// <item>sin HTML: se quitan comentarios y etiquetas; después <c>&amp;</c> → <c>&amp;amp;</c> (ninguna entidad
/// sobrevive: ni <c>&amp;#64;</c> ni <c>&amp;commat;</c>) y <c>&lt;</c> → <c>&amp;lt;</c>;</item>
/// <item>sin enlaces ni imágenes: <c>[texto](url)</c>, <c>![alt](url)</c> y <c>[texto][ref]</c> quedan en su texto, las
/// definiciones <c>[ref]: url</c> se quitan y cualquier <c>[</c>, <c>]</c> o <c>!</c> restante se escapa con
/// <c>\</c> (corchetes anidados o multilínea); <c>//</c> y <c>www.</c> se rompen con un espacio de ancho cero para
/// que GitHub no enlace URL sueltas ni relativas al protocolo;</item>
/// <item>sin menciones ni referencias: <c>@</c> → <c>@</c>+U+200B; <c>#</c> seguido de dígito (<c>#123</c>,
/// <c>owner/repo#5</c>) y <c>GH-</c> seguido de dígito llevan U+200B tras <c>#</c> / <c>GH-</c>;</item>
/// <item>como mucho <c>maxLength</c> caracteres (<see cref="TextLimits.Truncate"/>).</item>
/// </list>
/// </summary>
internal static partial class IssueCommentSanitizer
{
    /// <summary>Espacio de ancho cero (U+200B).</summary>
    public const char ZeroWidthSpace = '​';

    private const int RegexTimeoutMs = 1000;

    public static string Sanitize(string? text, int maxLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 2);
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var clean = WithoutControlCharacters(text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'));

        // HTML: primero los comentarios (incluido un marcador codenetix falso) y los autoenlaces <scheme:...>, que pasan
        // a texto; después cualquier etiqueta. Luego se escapa '&' (antes que '<') para que no sobreviva ninguna entidad.
        clean = HtmlComment().Replace(clean, string.Empty);
        clean = AutoLink().Replace(clean, "$1");
        clean = HtmlTag().Replace(clean, string.Empty);
        clean = clean.Replace("&", "&amp;", StringComparison.Ordinal);
        clean = clean.Replace("<", "&lt;", StringComparison.Ordinal);

        // Enlaces e imágenes de Markdown → su texto; lo que quede de su sintaxis se escapa.
        clean = Image().Replace(clean, "$1");
        clean = InlineLink().Replace(clean, "$1");
        clean = ReferenceLink().Replace(clean, "$1");
        clean = ReferenceDefinition().Replace(clean, string.Empty);
        clean = LinkSyntax().Replace(clean, @"\$0");

        return TextLimits.Truncate(BreakReferences(clean).Trim(), maxLength);
    }

    /// <summary>
    /// Rompe con U+200B las URL sueltas o relativas al protocolo (<c>//</c>, <c>www.</c>), las menciones (<c>@</c>) y las
    /// referencias a issues (<c>#123</c>, <c>owner/repo#5</c>, <c>GH-7</c>). También sirve para texto plano, como un título.
    /// </summary>
    public static string BreakReferences(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var clean = text.Replace("//", "/" + ZeroWidthSpace + "/", StringComparison.Ordinal);
        clean = Www().Replace(clean, "$1" + ZeroWidthSpace + ".");
        clean = clean.Replace("@", "@" + ZeroWidthSpace, StringComparison.Ordinal);
        return IssueReference().Replace(clean, "$1" + ZeroWidthSpace);
    }

    /// <summary>
    /// Sanea <paramref name="text"/> con las mismas reglas que <see cref="Sanitize"/> pero preservando enlaces
    /// Markdown seguros al PR del propio repositorio (<c>[#N](https://github.com/{owner}/{repo}/pull/N)</c>).
    /// Cualquier HTML, mención o enlace externo sigue eliminándose o neutralizándose.
    /// </summary>
    public static string SanitizeAllowingPullRequestLinks(string? text, int maxLength, string owner, string repo)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 2);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var escapedOwner = Regex.Escape(Uri.EscapeDataString(owner));
        var escapedRepo = Regex.Escape(Uri.EscapeDataString(repo));
        var pattern = $@"\[#(\d+)\]\((https://github\.com/{escapedOwner}/{escapedRepo}/pull/\1)\)";
        var prLinkRegex = new Regex(pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(RegexTimeoutMs));

        var preserved = new List<(string Placeholder, string Link)>();
        var nonce = Guid.NewGuid().ToString("N");
        var substituted = prLinkRegex.Replace(text, match =>
        {
            var placeholder = $"CODENETIXPRLINK{nonce}{preserved.Count}END";
            preserved.Add((placeholder, match.Value));
            return placeholder;
        });

        var sanitized = Sanitize(substituted, int.MaxValue);
        if (preserved.Count > 0)
        {
            foreach (var (placeholder, link) in preserved)
            {
                sanitized = sanitized.Replace(placeholder, link, StringComparison.Ordinal);
            }
        }

        return TextLimits.Truncate(sanitized, maxLength);
    }

    private static string WithoutControlCharacters(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is '\n' or '\t' || !char.IsControl(c))
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    [GeneratedRegex(@"<!--[\s\S]*?(?:-->|$)", RegexOptions.None, RegexTimeoutMs)]
    private static partial Regex HtmlComment();

    [GeneratedRegex(@"<((?:https?|ftp|mailto|file):[^<>\s]*)>", RegexOptions.IgnoreCase, RegexTimeoutMs)]
    private static partial Regex AutoLink();

    [GeneratedRegex(@"</?[A-Za-z!?][^<>]*>", RegexOptions.None, RegexTimeoutMs)]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"!\[([^\[\]\n]*)\]\([^)\n]*\)", RegexOptions.None, RegexTimeoutMs)]
    private static partial Regex Image();

    [GeneratedRegex(@"\[([^\[\]\n]*)\]\([^)\n]*\)", RegexOptions.None, RegexTimeoutMs)]
    private static partial Regex InlineLink();

    [GeneratedRegex(@"\[([^\[\]\n]*)\]\[[^\[\]\n]*\]", RegexOptions.None, RegexTimeoutMs)]
    private static partial Regex ReferenceLink();

    [GeneratedRegex(@"^[ \t]*\[[^\[\]\n]+\]:[^\n]*$", RegexOptions.Multiline, RegexTimeoutMs)]
    private static partial Regex ReferenceDefinition();

    [GeneratedRegex(@"[\[\]!]", RegexOptions.None, RegexTimeoutMs)]
    private static partial Regex LinkSyntax();

    [GeneratedRegex(@"\b(www)\.", RegexOptions.IgnoreCase, RegexTimeoutMs)]
    private static partial Regex Www();

    [GeneratedRegex(@"(#|\bGH-)(?=\d)", RegexOptions.IgnoreCase, RegexTimeoutMs)]
    private static partial Regex IssueReference();
}
