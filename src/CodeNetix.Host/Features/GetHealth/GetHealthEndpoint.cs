namespace CodeNetix.Host.Features.GetHealth;

/// <summary>
/// <c>GET /api/health</c>. Anónimo por decisión: lo usa el <c>HEALTHCHECK</c> de la imagen
/// y debe seguir exento del <c>admin-token</c> (historia 1.3).
/// </summary>
internal static class GetHealthEndpoint
{
    public const string Route = "/api/health";

    public static IEndpointRouteBuilder MapGetHealth(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(Route, (StartupState state) => state.IsCompleted
                ? Results.Ok(new HealthResponse("Ready"))
                : Results.Json(new HealthResponse("Starting"), statusCode: StatusCodes.Status503ServiceUnavailable))
            .WithName("GetHealth");

        return endpoints;
    }
}

internal sealed record HealthResponse(string Status);
