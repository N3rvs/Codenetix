using CodeNetix.Contracts;
using CodeNetix.Host.Features.GetRun;

namespace CodeNetix.Host.Features.GetRunEvents;

/// <summary>
/// <c>GET /api/runs/{id}/events?afterSeq=N</c> (AD-3): solo los <see cref="RunEvent"/> con <c>seq</c> &gt; N
/// (por defecto 0), en orden, como array JSON. 404 si la Ejecución no existe; 400 si <c>afterSeq</c> es negativo.
/// </summary>
internal static class GetRunEventsEndpoint
{
    public const string Route = "/api/runs/{id}/events";

    public static IEndpointRouteBuilder MapGetRunEvents(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(Route, HandleAsync)
            .WithName("GetRunEvents")
            .Produces<IReadOnlyList<RunEvent>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);
        return endpoints;
    }

    private static async Task<IResult> HandleAsync(
        HttpContext context,
        string id,
        long? afterSeq,
        IRunReader runs,
        CancellationToken cancellationToken)
    {
        if (afterSeq is < 0)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Petición no válida",
                detail: "'afterSeq' debe ser un entero mayor o igual que 0.",
                instance: context.Request.Path);
        }

        var run = await runs.FindAsync(id, cancellationToken);
        if (run is null)
        {
            return GetRunEndpoint.RunNotFound(context);
        }

        var events = await runs.GetEventsAsync(run.RunId, afterSeq ?? 0, cancellationToken);
        return Results.Ok(events);
    }
}
