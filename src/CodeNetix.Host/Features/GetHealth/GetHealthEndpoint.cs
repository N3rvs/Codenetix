using CodeNetix.Contracts;
using CodeNetix.Host.Configuration;

namespace CodeNetix.Host.Features.GetHealth;

/// <summary>
/// <c>GET /api/health</c>. Anónimo por decisión: lo usa el <c>HEALTHCHECK</c> de la imagen
/// y debe seguir exento del <c>admin-token</c> (historia 1.3).
/// <c>Starting</c> → 503; <c>Ready</c> y <c>Diagnostic</c> → 200 (en diagnóstico la instancia está viva).
/// </summary>
internal static class GetHealthEndpoint
{
    public const string Route = "/api/health";

    public static IEndpointRouteBuilder MapGetHealth(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(Route, (InstanceHealth health) =>
            {
                var (status, reasons) = health.Read();
                var body = new HealthResponse(status.ToString(), reasons);
                return status == InstanceHealthStatus.Starting
                    ? Results.Json(body, statusCode: StatusCodes.Status503ServiceUnavailable)
                    : Results.Ok(body);
            })
            .WithName("GetHealth");

        return endpoints;
    }
}

internal sealed record HealthResponse(string Status, IReadOnlyList<string> Reasons);
