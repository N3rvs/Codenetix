using CodeNetix.Contracts;

namespace CodeNetix.Host.Features.ListRuns;

/// <summary>
/// <c>GET /api/runs?limit=N</c>: devuelve las Ejecuciones recientes ordenadas por <c>CreatedAt</c> descendente.
/// <c>limit</c> es opcional (por defecto 50, acotado a un máximo de 200; valores &lt;= 0 devuelven 400).
/// </summary>
internal static class ListRunsEndpoint
{
    public const string Route = "/api/runs";
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    public static IEndpointRouteBuilder MapListRuns(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(Route, HandleAsync)
            .WithName("ListRuns")
            .Produces<IReadOnlyList<RunSummary>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);
        return endpoints;
    }

    private static async Task<IResult> HandleAsync(
        HttpContext context,
        int? limit,
        IRunReader runs,
        CancellationToken cancellationToken)
    {
        if (limit is <= 0)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Petición no válida",
                detail: "'limit' debe ser un entero mayor que 0.",
                instance: context.Request.Path);
        }

        var effectiveLimit = Math.Min(limit ?? DefaultLimit, MaxLimit);
        var items = await runs.ListRecentAsync(effectiveLimit, cancellationToken);
        return Results.Ok(items);
    }
}
