using System.Security.Cryptography;
using System.Text;
using CodeNetix.Host.Features.GetHealth;
using Microsoft.Net.Http.Headers;

namespace CodeNetix.Host.Access;

/// <summary>
/// Único middleware de autenticación (AD-17): <c>/api/**</c>, <c>/hubs/**</c> y <c>/openapi/**</c> exigen
/// <c>Authorization: Bearer &lt;admin-token&gt;</c>. Solo en <c>/hubs/**</c> se acepta además el parámetro de
/// consulta <c>access_token</c> (requerido por WebSocket/SSE de SignalR en navegador). Va antes del enrutado,
/// así que un <c>/api</c> desconocido sin token da 401 y no 404. Única excepción: <c>GET /api/health</c>.
/// </summary>
internal sealed class AdminTokenMiddleware(RequestDelegate next, AdminTokenStore tokenStore)
{
    public const string ApiPrefix = "/api";
    public const string HubsPrefix = "/hubs";
    public const string OpenApiPrefix = "/openapi";
    public const string BearerScheme = "Bearer";
    public const string AccessTokenQueryKey = "access_token";

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
            && !path.StartsWithSegments(HubsPrefix, StringComparison.OrdinalIgnoreCase)
            && !path.StartsWithSegments(OpenApiPrefix, StringComparison.OrdinalIgnoreCase))
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

        if (TryExtractBearerToken(request, out var bearerToken))
        {
            return TokenEquals(bearerToken, expectedToken);
        }

        // AD-17: solo /hubs/** admite ?access_token=<token> (nunca /api/** ni /openapi/**).
        if (request.Path.StartsWithSegments(HubsPrefix, StringComparison.OrdinalIgnoreCase)
            && TryExtractQueryAccessToken(request, out var queryToken))
        {
            return TokenEquals(queryToken, expectedToken);
        }

        return false;
    }

    private static bool TryExtractBearerToken(HttpRequest request, out string token)
    {
        token = string.Empty;
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

        token = presented.ToString();
        return true;
    }

    private static bool TryExtractQueryAccessToken(HttpRequest request, out string token)
    {
        token = string.Empty;
        if (!request.Query.TryGetValue(AccessTokenQueryKey, out var values)
            || values.Count != 1
            || values[0] is not { } raw)
        {
            return false;
        }

        var presented = raw.AsSpan().Trim();
        if (presented.IsEmpty)
        {
            return false;
        }

        token = presented.ToString();
        return true;
    }

    private static bool TokenEquals(string presented, string expectedToken)
    {
        // Comparación en tiempo constante sobre resúmenes de igual longitud: tampoco se filtra la longitud.
        Span<byte> presentedHash = stackalloc byte[SHA256.HashSizeInBytes];
        Span<byte> expectedHash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(presented), presentedHash);
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
