using CodeNetix.Contracts;

namespace CodeNetix.Host.Features.ListIgnoredEvents;

/// <summary>
/// <c>GET /api/events/ignored?limit=N</c> (FR-8: motivo visible en el Panel): los Eventos <c>Ignored</c> más recientes, con su
/// Fuente, objeto, motivo y fecha. <c>limit</c> es opcional (por defecto 20, máximo 100; valores &lt;= 0 → 400).
/// </summary>
internal static class ListIgnoredEventsEndpoint
{
    public const string Route = "/api/events/ignored";
    public const int DefaultLimit = 20;
    public const int MaxLimit = 100;

    public static IEndpointRouteBuilder MapListIgnoredEvents(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(Route, HandleAsync)
            .WithName("ListIgnoredEvents")
            .Produces<IReadOnlyList<IgnoredEventSummary>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);
        return endpoints;
    }

    private static async Task<IResult> HandleAsync(HttpContext context, int? limit, IIgnoredEventReader events, CancellationToken cancellationToken)
    {
        if (limit is <= 0)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Petición no válida",
                detail: "'limit' debe ser un entero mayor que 0.",
                instance: context.Request.Path);
        }

        var items = await events.ListIgnoredAsync(Math.Min(limit ?? DefaultLimit, MaxLimit), cancellationToken);
        return Results.Ok(items.Select(e => new IgnoredEventSummary(e.EventId, e.Source.ToString(), e.ObjectKey, e.Reason, e.ReceivedAt)).ToList());
    }
}

/// <param name="Source"><c>Manual</c>, <c>GitHub</c>, <c>Sentry</c> o <c>GitHubAction</c>.</param>
internal sealed record IgnoredEventSummary(Guid EventId, string Source, string ObjectKey, string? Reason, DateTimeOffset ReceivedAt);
