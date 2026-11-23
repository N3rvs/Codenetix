using CodeNetix.Contracts;

namespace CodeNetix.Host.Features.GetEvent;

/// <summary>
/// <c>GET /api/events/{eventId}</c>: <c>{ eventId, status, reason, runId }</c>. <c>runId</c> aparece cuando el
/// Dispatcher ha creado la Ejecución; un Evento <c>Ignored</c> lleva su motivo. 404 si no existe.
/// </summary>
internal static class GetEventEndpoint
{
    public const string Route = "/api/events/{eventId:guid}";

    public static IEndpointRouteBuilder MapGetEvent(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(Route, HandleAsync)
            .WithName("GetEvent")
            .Produces<EventResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);
        return endpoints;
    }

    private static async Task<IResult> HandleAsync(HttpContext context, Guid eventId, IEventReader events, CancellationToken cancellationToken)
    {
        var info = await events.FindAsync(eventId, cancellationToken);
        if (info is null)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Evento no encontrado",
                detail: $"No existe el Evento {eventId}.",
                instance: context.Request.Path);
        }

        return Results.Ok(new EventResponse(info.EventId, info.Status.ToString(), info.Reason, info.RunId));
    }
}

/// <param name="Status"><c>Accepted</c> o <c>Ignored</c>.</param>
internal sealed record EventResponse(Guid EventId, string Status, string? Reason, Guid? RunId);
