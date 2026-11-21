using CodeNetix.Contracts;

namespace CodeNetix.Host.Features.GetRun;

/// <summary>
/// <c>GET /api/runs/{id}</c> (AD-3): <c>id</c> es el GUID o el <c>shortId</c> de 8 caracteres. Devuelve el
/// Estado, la <see cref="Spec"/> (<c>spec</c>, o <c>null</c> si todavía no hay) y todos los <see cref="RunEvent"/>
/// en orden de <c>seq</c>; 404 si no existe. Protegido por el
/// <c>admin-token</c> como todo <c>/api</c>.
/// </summary>
internal static class GetRunEndpoint
{
    public const string Route = "/api/runs/{id}";

    public static IEndpointRouteBuilder MapGetRun(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(Route, HandleAsync).WithName("GetRun");
        return endpoints;
    }

    private static async Task<IResult> HandleAsync(HttpContext context, string id, IRunReader runs, CancellationToken cancellationToken)
    {
        var run = await runs.FindAsync(id, cancellationToken);
        if (run is null)
        {
            return RunNotFound(context);
        }

        var events = await runs.GetEventsAsync(run.RunId, 0, cancellationToken);
        var spec = await runs.GetSpecAsync(run.RunId, cancellationToken);
        var testReport = await runs.GetTestReportAsync(run.RunId, cancellationToken);
        var verdicts = await runs.GetVerdictsAsync(run.RunId, cancellationToken);
        return Results.Ok(new RunResponse(
            run.RunId,
            run.ShortId,
            run.State,
            run.FailureReason,
            run.FailureDetail,
            run.EventId,
            run.ObjectKey,
            run.CreatedAt,
            run.UpdatedAt,
            spec,
            testReport,
            verdicts,
            events));
    }

    internal static IResult RunNotFound(HttpContext context) => Results.Problem(
        statusCode: StatusCodes.Status404NotFound,
        title: "Ejecución no encontrada",
        detail: "No existe ninguna Ejecución con ese id (GUID o shortId de 8 caracteres).",
        instance: context.Request.Path);
}

internal sealed record RunResponse(
    Guid RunId,
    string ShortId,
    RunState State,
    FailureReason? FailureReason,
    string? FailureDetail,
    Guid EventId,
    string ObjectKey,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    Spec? Spec,
    TestReport? TestReport,
    IReadOnlyList<RunVerdict> Verdicts,
    IReadOnlyList<RunEvent> Events);
