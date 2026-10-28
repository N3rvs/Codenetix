using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace CodeNetix.Contracts;

/// <summary>
/// Texto no confiable (AD-5): el cuerpo de una issue, un comentario… Es siempre dato, nunca instrucción.
/// No hay conversión a <see cref="string"/> y <see cref="ToString"/> no devuelve el contenido: la única vía
/// para obtenerlo es <see cref="RenderAsDataBlock"/>, que lo envuelve en un bloque delimitado con un nonce
/// aleatorio y neutraliza cualquier cierre de bloque incrustado.
/// </summary>
[DebuggerDisplay("[UntrustedText]")]
public sealed partial class UntrustedText
{
    public const string Placeholder = "[UntrustedText]";

    private readonly string _text;

    private UntrustedText(string text) => _text = text;

    public static UntrustedText From(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new UntrustedText(text);
    }

    /// <summary>Siempre <c>"[UntrustedText]"</c>: el contenido nunca se filtra por interpolación ni logs.</summary>
    public override string ToString() => Placeholder;

    /// <summary>
    /// <code>
    /// &lt;untrusted-data label="{label}" id="{nonce}"&gt;
    /// {texto}
    /// &lt;/untrusted-data id="{nonce}"&gt;
    /// </code>
    /// <paramref name="label"/> solo admite <c>[a-z0-9-]</c>; el nonce son 16 bytes aleatorios en hexadecimal,
    /// distintos en cada llamada; cada <c>&lt;/untrusted-data</c> del texto se convierte en <c>&lt;\/untrusted-data</c>.
    /// </summary>
    public string RenderAsDataBlock(string label)
    {
        if (string.IsNullOrEmpty(label) || !LabelPattern().IsMatch(label))
        {
            throw new ArgumentException("La etiqueta solo admite letras minúsculas, dígitos y '-'.", nameof(label));
        }

        var nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var neutralized = ClosingTagPattern().Replace(_text, m => "<\\/" + m.Value[2..]);
        return $"<untrusted-data label=\"{label}\" id=\"{nonce}\">\n{neutralized}\n</untrusted-data id=\"{nonce}\">";
    }

    [GeneratedRegex(@"\A[a-z0-9-]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex LabelPattern();

    [GeneratedRegex("</untrusted-data", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ClosingTagPattern();
}
