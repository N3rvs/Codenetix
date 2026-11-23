using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using CodeNetix.Contracts;

namespace CodeNetix.Sandbox.Gateway;

/// <summary>
/// Registro en memoria de las sesiones activas del Sandbox en el Egress Gateway (AD-7). Mantiene por <c>runId</c> su
/// credencial efímera <c>cnx-run-&lt;runId&gt;-&lt;nonce&gt;</c> y la IP del contenedor en <c>codenetix-sandbox</c>, y las
/// revoca al detener o liberar la sesión.
/// </summary>
public sealed class SandboxGatewayRegistry : ISandboxGateway
{
    public const string TokenPrefix = "cnx-run-";

    private static readonly byte[] DummyHash = new byte[SHA256.HashSizeInBytes];

    private readonly ConcurrentDictionary<Guid, Entry> _sessions = new();

    /// <summary>Genera una credencial efímera <c>cnx-run-&lt;runId&gt;-&lt;nonce&gt;</c> (nonce de 32 hex).</summary>
    public static string GenerateToken(Guid runId)
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return $"{TokenPrefix}{runId:D}-{Convert.ToHexStringLower(bytes)}";
    }

    public SandboxGatewaySession Register(Guid runId) =>
        Register(runId, GenerateToken(runId), containerIp: null);

    public SandboxGatewaySession Register(Guid runId, string token, string? containerIp = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        var trimmed = token.Trim();
        var session = new SandboxGatewaySession(runId, trimmed, NormalizeIp(containerIp));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(trimmed));
        _sessions[runId] = new Entry(session, hash);
        return session;
    }

    public void UpdateContainerIp(Guid runId, string? containerIp)
    {
        var normalized = NormalizeIp(containerIp);
        while (_sessions.TryGetValue(runId, out var existing))
        {
            var updated = existing with { Session = existing.Session with { ContainerIp = normalized } };
            if (_sessions.TryUpdate(runId, updated, existing))
            {
                break;
            }
        }
    }

    public bool Revoke(Guid runId) => _sessions.TryRemove(runId, out _);

    public bool TryGet(Guid runId, out SandboxGatewaySession? session)
    {
        if (_sessions.TryGetValue(runId, out var entry))
        {
            session = entry.Session;
            return true;
        }

        session = null;
        return false;
    }

    public SandboxGatewaySession? TryAuthenticate(string? token) =>
        TryAuthenticate(token, out var session) ? session : null;

    public bool TryAuthenticate(string? token, out SandboxGatewaySession? session)
    {
        session = null;
        var candidate = token?.Trim() ?? string.Empty;
        Span<byte> presentedHash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(candidate), presentedHash);

        if (candidate.Length == 0)
        {
            _ = CryptographicOperations.FixedTimeEquals(presentedHash, DummyHash);
            return false;
        }

        var snapshot = _sessions.Values;
        if (snapshot.Count == 0)
        {
            _ = CryptographicOperations.FixedTimeEquals(presentedHash, DummyHash);
            return false;
        }

        SandboxGatewaySession? matched = null;
        foreach (var entry in snapshot)
        {
            if (CryptographicOperations.FixedTimeEquals(presentedHash, entry.TokenHash))
            {
                matched = entry.Session;
            }
        }

        session = matched;
        return matched is not null;
    }

    public Guid? ResolveRunId(string? proxyAuthorization, string? remoteIp)
    {
        if (!string.IsNullOrWhiteSpace(proxyAuthorization))
        {
            var resolved = ResolveFromProxyAuthorization(proxyAuthorization.Trim());
            if (resolved is not null)
            {
                return resolved;
            }
        }

        var normalizedIp = NormalizeIp(remoteIp);
        if (!string.IsNullOrEmpty(normalizedIp))
        {
            foreach (var entry in _sessions.Values)
            {
                if (string.Equals(entry.Session.ContainerIp, normalizedIp, StringComparison.OrdinalIgnoreCase))
                {
                    return entry.Session.RunId;
                }
            }
        }

        var values = _sessions.Values;
        if (values.Count == 1)
        {
            foreach (var entry in values)
            {
                if (string.IsNullOrEmpty(entry.Session.ContainerIp) || IsNullOrLoopback(normalizedIp))
                {
                    return entry.Session.RunId;
                }
            }
        }

        return null;
    }

    private Guid? ResolveFromProxyAuthorization(string header)
    {
        var value = header;
        var space = header.IndexOf(' ', StringComparison.Ordinal);
        if (space > 0)
        {
            var scheme = header.AsSpan(0, space);
            var parameter = header[(space + 1)..].Trim();
            if (scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase))
            {
                value = parameter;
            }
            else if (scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(parameter));
                    var colon = decoded.IndexOf(':', StringComparison.Ordinal);
                    if (colon >= 0)
                    {
                        var user = decoded[..colon];
                        var pass = decoded[(colon + 1)..];
                        if (TryAuthenticate(user, out var byUser))
                        {
                            return byUser!.RunId;
                        }

                        if (TryAuthenticate(pass, out var byPass))
                        {
                            return byPass!.RunId;
                        }

                        return null;
                    }

                    value = decoded;
                }
                catch (FormatException)
                {
                    return null;
                }
            }
        }

        return TryAuthenticate(value, out var session) ? session!.RunId : null;
    }

    private static bool IsNullOrLoopback(string? ip) =>
        string.IsNullOrEmpty(ip)
        || (IPAddress.TryParse(ip, out var parsed) && IPAddress.IsLoopback(parsed));

    internal static bool TryExtractRunIdFromTokenFormat(string? token, out Guid runId)
    {
        runId = default;
        if (string.IsNullOrWhiteSpace(token) || !token.StartsWith(TokenPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        // cnx-run-<36 chars GUID>-<nonce>
        var rest = token.AsSpan(TokenPrefix.Length);
        return rest.Length > 37 && rest[36] == '-' && Guid.TryParse(rest[..36], out runId);
    }

    private static string? NormalizeIp(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip))
        {
            return null;
        }

        var trimmed = ip.Trim();
        if (IPAddress.TryParse(trimmed, out var parsed))
        {
            return (parsed.IsIPv4MappedToIPv6 ? parsed.MapToIPv4() : parsed).ToString();
        }

        return trimmed;
    }

    private sealed record Entry(SandboxGatewaySession Session, byte[] TokenHash);
}
