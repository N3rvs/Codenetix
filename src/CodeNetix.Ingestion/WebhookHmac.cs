using System.Security.Cryptography;
using System.Text;

namespace CodeNetix.Ingestion;

/// <summary>
/// HMAC SHA-256 de un webhook (GitHub y Sentry): el cuerpo crudo con el secreto, comparado en tiempo constante con la firma
/// recibida en hexadecimal (64 caracteres, mayúsculas o minúsculas). Nunca revela por qué falla.
/// </summary>
internal static class WebhookHmac
{
    public const int HexLength = HMACSHA256.HashSizeInBytes * 2;

    public static bool MatchesHex(string secret, ReadOnlySpan<byte> body, ReadOnlySpan<char> hex)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        if (hex.Length != HexLength)
        {
            return false;
        }

        Span<byte> received = stackalloc byte[HMACSHA256.HashSizeInBytes];
        if (Convert.FromHexString(hex, received, out _, out var written) != System.Buffers.OperationStatus.Done
            || written != received.Length)
        {
            return false;
        }

        Span<byte> expected = stackalloc byte[HMACSHA256.HashSizeInBytes];
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body, expected);
        return CryptographicOperations.FixedTimeEquals(expected, received);
    }

    /// <summary>Hex en minúsculas del HMAC (para pruebas y documentación).</summary>
    public static string ComputeHex(string secret, ReadOnlySpan<byte> body) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body));
}
