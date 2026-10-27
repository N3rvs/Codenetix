using System.Text.Json;
using System.Text.RegularExpressions;
using CodeNetix.Contracts;

namespace CodeNetix.Host.Secrets;

/// <summary>
/// Enmascara los secretos de la instancia (todo valor de configuración cuya última clave sea
/// <c>ApiKey</c>, <c>Token</c>, <c>AuthToken</c>, <c>AccessToken</c>, <c>Secret</c>,
/// <c>WebhookSecret</c> o <c>Password</c>) y los patrones conocidos de keys y tokens.
/// </summary>
internal sealed partial class SecretRedactor : ISecretRedactor
{
    public const string Ellipsis = "…";

    /// <summary>Los valores más cortos no se buscan como texto literal: enmascararlos destrozaría cualquier salida.</summary>
    public const int MinimumKnownSecretLength = 4;

    private static readonly HashSet<string> SecretKeyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "ApiKey", "Token", "AuthToken", "AccessToken", "Secret", "WebhookSecret", "Password",
    };

    /// <summary>Pares (texto a buscar, máscara), del más largo al más corto.</summary>
    private readonly (string Value, string Mask)[] _knownSecrets;

    public SecretRedactor(IConfiguration configuration)
        : this(CollectSecrets(configuration))
    {
    }

    public SecretRedactor(IEnumerable<string> secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);

        var pairs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in secrets)
        {
            var secret = raw?.Trim();
            if (string.IsNullOrEmpty(secret) || secret.Length < MinimumKnownSecretLength)
            {
                continue;
            }

            var mask = Mask(secret);
            pairs.TryAdd(secret, mask);

            // En un cuerpo JSON el secreto puede aparecer escapado (p. ej. '+' como +).
            var jsonEscaped = JsonEncodedText.Encode(secret).ToString();
            pairs.TryAdd(jsonEscaped, mask);

            // En rutas y consultas registradas el secreto puede aparecer codificado para URL.
            var urlEscaped = Uri.EscapeDataString(secret);
            pairs.TryAdd(urlEscaped, mask);
        }

        _knownSecrets = [.. pairs.Select(p => (p.Key, p.Value)).OrderByDescending(p => p.Key.Length)];
    }

    public string Redact(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var result = value;
        foreach (var (secret, mask) in _knownSecrets)
        {
            if (result.Contains(secret, StringComparison.Ordinal))
            {
                result = result.Replace(secret, mask, StringComparison.Ordinal);
            }
        }

        return KnownKeyPattern().Replace(result, m => Mask(m.Value));
    }

    /// <summary>Con 12 caracteres o más: 3 primeros + "…" + 4 últimos; con menos, solo "…".</summary>
    public static string Mask(string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        return secret.Length >= 12
            ? string.Concat(secret.AsSpan(0, 3), Ellipsis, secret.AsSpan(secret.Length - 4))
            : Ellipsis;
    }

    private static IEnumerable<string> CollectSecrets(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        foreach (var (key, value) in configuration.AsEnumerable())
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var lastSeparator = key.LastIndexOf(ConfigurationPath.KeyDelimiter, StringComparison.Ordinal);
            var lastKey = lastSeparator < 0 ? key : key[(lastSeparator + 1)..];
            if (SecretKeyNames.Contains(lastKey))
            {
                yield return value;
            }
        }
    }

    // sk-ant-…, sk-… (Anthropic, OpenAI y compatibles), ghp_/gho_/ghu_/ghs_/ghr_… y github_pat_… (GitHub).
    // Longitud mínima para no tocar palabras corrientes como "sk-learn".
    [GeneratedRegex(@"(?<![A-Za-z0-9_\-])(?:sk-[A-Za-z0-9_\-]{16,}|gh[pousr]_[A-Za-z0-9]{16,}|github_pat_[A-Za-z0-9_]{16,})", RegexOptions.CultureInvariant)]
    private static partial Regex KnownKeyPattern();
}
