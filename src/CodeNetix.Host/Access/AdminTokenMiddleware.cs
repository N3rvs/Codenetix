using System.Security.Cryptography;
using System.Text;
using CodeNetix.Host.Features.GetHealth;
using Microsoft.Net.Http.Headers;

namespace CodeNetix.Host.Access;

/// <summary>
/// Único middleware de autenticación (AD-17): <c>/api/**</c> y <c>/hubs/**</c> exigen
/// <c>Authorization: Bearer &lt;admin-token&gt;</c>. Va antes del enrutado, así que un <c>/api</c>
/// desconocido sin token da 401 y no 404. Única excepción: <c>GET /api/health</c>.
/// </summary>
internal sealed class AdminTokenMiddleware(RequestDelegate next, AdminTokenStore tokenStore)
{
    public const string ApiPrefix = "/api";
    public const string HubsPrefix = "/hubs";
    public const string BearerScheme = "Bearer";

    public Task InvokeAsync(HttpContext context)
    {
        if (!RequiresToken(context.Request) || IsAuthorized(context.Request, tokenStore.EffectiveToken))
        {
            return next(context);
        }

        return ChallengeAsync(context);
    }

    internal static bool RequiresToken(HttpRequest request)
    {
        var path = request.Path;
        if (!path.StartsWithSegments(ApiPrefix, StringComparison.OrdinalIgnoreCase)
            && !path.StartsWithSegments(HubsPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var isAnonymousHealth = (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
            && IsHealthPath(path.Value);
        return !isAnonymousHealth;
    }

    /// <summary><c>/api/health</c> con, como mucho, una barra final (sin distinguir mayúsculas).</summary>
    private static bool IsHealthPath(string? path)
    {
        if (path is null)
        {
            return false;
        }

        var trimmed = path.EndsWith('/') ? path[..^1] : path;
        return string.Equals(trimmed, GetHealthEndpoint.Route, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsAuthorized(HttpRequest request, string? expectedToken)
    {
        if (string.IsNullOrEmpty(expectedToken))
        {
            return false; // Sin token cargado se deniega todo.
        }

        var headers = request.Headers.Authorization;
        if (headers.Count != 1 || headers[0] is not { } header)
        {
            return false;
        }

        var separator = header.IndexOf(' ', StringComparison.Ordinal);
        if (separator <= 0
            || !header.AsSpan(0, separator).Equals(BearerScheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var presented = header.AsSpan(separator + 1).Trim();
        if (presented.IsEmpty)
        {
            return false;
        }

        // Comparación en tiempo constante sobre resúmenes de igual longitud: tampoco se filtra la longitud.
        Span<byte> presentedHash = stackalloc byte[SHA256.HashSizeInBytes];
        Span<byte> expectedHash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(presented.ToString()), presentedHash);
        SHA256.HashData(Encoding.UTF8.GetBytes(expectedToken), expectedHash);
        return CryptographicOperations.FixedTimeEquals(presentedHash, expectedHash);
    }

    private static Task ChallengeAsync(HttpContext context)
    {
        context.Response.Headers[HeaderNames.WWWAuthenticate] = BearerScheme;
        return Results.Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Se requiere el admin-token",
                detail: "Envía la cabecera 'Authorization: Bearer <admin-token>'. El token está en /data/admin.token o en CODENETIX__Host__AccessToken.")
            .ExecuteAsync(context);
    }
}
