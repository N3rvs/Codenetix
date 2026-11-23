using System.Security.Cryptography;
using System.Text;

namespace CodeNetix.Ingestion.Features.ReceiveGitHubWebhook;

/// <summary>
/// Firma de GitHub (<c>X-Hub-Signature-256: sha256=&lt;hex&gt;</c>): HMAC SHA-256 del cuerpo crudo con el secreto del
/// webhook, comparado en tiempo constante. Nunca revela por qué falla.
/// </summary>
internal static class GitHubWebhookSignature
{
    public const string Prefix = "sha256=";

    private const int HexLength = 64;

    public static bool IsValid(string secret, ReadOnlySpan<byte> body, string? header)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);

        var value = header?.Trim();
        if (value is null
            || value.Length != Prefix.Length + HexLength
            // Exactamente como lo envía GitHub: prefijo en minúsculas (el hex sí admite mayúsculas).
            || !value.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        Span<byte> received = stackalloc byte[HMACSHA256.HashSizeInBytes];
        if (Convert.FromHexString(value.AsSpan(Prefix.Length), received, out _, out var written) != System.Buffers.OperationStatus.Done
            || written != received.Length)
        {
            return false;
        }

        Span<byte> expected = stackalloc byte[HMACSHA256.HashSizeInBytes];
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body, expected);
        return CryptographicOperations.FixedTimeEquals(expected, received);
    }

    /// <summary>Firma que enviaría GitHub (para pruebas y documentación).</summary>
    public static string Compute(string secret, ReadOnlySpan<byte> body) =>
        Prefix + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body));
}
