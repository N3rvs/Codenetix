namespace CodeNetix.Ingestion.Features.ReceiveGitHubWebhook;

/// <summary>
/// Firma de GitHub (<c>X-Hub-Signature-256: sha256=&lt;hex&gt;</c>): HMAC SHA-256 del cuerpo crudo con el secreto del
/// webhook, comparado en tiempo constante (<see cref="WebhookHmac"/>). Nunca revela por qué falla.
/// </summary>
internal static class GitHubWebhookSignature
{
    public const string Prefix = "sha256=";

    public static bool IsValid(string secret, ReadOnlySpan<byte> body, string? header)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);

        var value = header?.Trim();
        return value is not null
            && value.Length == Prefix.Length + WebhookHmac.HexLength
            // Exactamente como lo envía GitHub: prefijo en minúsculas (el hex sí admite mayúsculas).
            && value.StartsWith(Prefix, StringComparison.Ordinal)
            && WebhookHmac.MatchesHex(secret, body, value.AsSpan(Prefix.Length));
    }

    /// <summary>Firma que enviaría GitHub (para pruebas y documentación).</summary>
    public static string Compute(string secret, ReadOnlySpan<byte> body) => Prefix + WebhookHmac.ComputeHex(secret, body);
}
