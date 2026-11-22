using CodeNetix.Contracts;
using CodeNetix.Host.Features.GetRun;

namespace CodeNetix.Host.Features.CancelRun;

/// <summary>
/// <c>POST /api/runs/{id}/cancel</c> (FR-23, AD-2): solicita la cancelación segura de una Ejecución
/// identificada por GUID o <c>shortId</c> de 8 caracteres. Devuelve <c>202 Accepted</c> si está en
/// <c>Received</c>, <c>Analyzing</c>, <c>Coding</c> o <c>Reviewing</c>; <c>409 Conflict</c> en <c>Deciding</c>
/// o estados terminales; y <c>404 Not Found</c> si no existe.
/// </summary>
internal static class CancelRunEndpoint
{
    public const string Route = "/api/runs/{id}/cancel";

    public static IEndpointRouteBuilder MapCancelRun(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(Route, HandleAsync)
            .WithName("CancelRun")
            .Produces<CancelRunResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        return endpoints;
    }

    private static async Task<IResult> HandleAsync(
        HttpContext context,
        string id,
        IRunControl control,
        CancellationToken cancellationToken)
    {
        var result = await control.RequestCancelAsync(id, cancellationToken);
        return result.Outcome switch
        {
            CancelRunOutcome.Accepted => Results.Accepted(
                uri: $"/api/runs/{result.RunId!.Value}",
                value: new CancelRunResponse(
                    result.RunId.Value,
                    result.ShortId!,
                    result.State!.Value,
                    CancellationRequested: true)),
            CancelRunOutcome.NotFound => GetRunEndpoint.RunNotFound(context),
            CancelRunOutcome.Conflict => Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "No se puede cancelar la Ejecución",
                detail: result.Detail ?? "La Ejecución no se encuentra en un estado cancelable.",
                instance: context.Request.Path),
            _ => throw new InvalidOperationException($"Resultado de cancelación desconocido: {result.Outcome}."),
        };
    }
}

internal sealed record CancelRunResponse(
    Guid RunId,
    string ShortId,
    RunState State,
    bool CancellationRequested);
