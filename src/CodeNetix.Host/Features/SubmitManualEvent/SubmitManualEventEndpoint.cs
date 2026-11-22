using System.Text.Json;
using CodeNetix.Contracts;
using CodeNetix.Host.Configuration;

namespace CodeNetix.Host.Features.SubmitManualEvent;

/// <summary>
/// <c>POST /api/events/manual</c> (FR-5, AD-4): cuerpo <c>{ "url": "...", "requestId": "&lt;guid&gt;" }</c>.
/// 201 si el Evento es nuevo, 200 si ya existía (mismo <c>requestId</c>), ambos con <c>status</c>
/// (<c>Accepted</c> o <c>Ignored</c>) y <c>reason</c>; 400 si el cuerpo o la URL no son válidos, 501 para
/// Sentry, 502 si no se puede autorizar en GitHub y 503 si la instancia no está <c>Ready</c>. En ningún
/// error se persiste nada. Protegido por el <c>admin-token</c> como todo <c>/api</c>. No crea Ejecuciones.
/// </summary>
internal static class SubmitManualEventEndpoint
{
    public const string Route = "/api/events/manual";

    public const string InvalidRequestIdMessage =
        "Falta 'requestId' o no es un GUID. Cada petición debe llevar un GUID propio; repetirlo reintenta la misma petición sin duplicar el Evento.";

    public const string StartingMessage = "CodeNetix está arrancando; vuelve a intentarlo en unos segundos.";

    public const string DiagnosticMessage = "CodeNetix está en modo diagnóstico y no acepta trabajo hasta completar la configuración";

    public static IEndpointRouteBuilder MapSubmitManualEvent(this IEndpointRouteBuilder endpoints)
    {
        // No usar .Accepts<SubmitManualEventRequest>("application/json") porque ConsumesMatcherPolicy
        // rechazaría Content-Type != application/json antes de HandleAsync (el contrato exige 400 ProblemDetails).
        // El requestBody OpenAPI se adjunta mediante un document transformer en ServeMode.
        endpoints.MapPost(Route, HandleAsync)
            .WithName("SubmitManualEvent")
            .Produces<SubmitManualEventResponse>(StatusCodes.Status201Created)
            .Produces<SubmitManualEventResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status501NotImplemented)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        return endpoints;
    }

    private static async Task<IResult> HandleAsync(
        HttpContext context,
        InstanceHealth health,
        IManualEventIngestion ingestion,
        CancellationToken cancellationToken)
    {
        var (status, reasons) = health.Read();
        if (status != InstanceHealthStatus.Ready)
        {
            return Unavailable(context, status, reasons);
        }

        SubmitManualEventRequest? request;
        try
        {
            request = await context.Request.ReadFromJsonAsync<SubmitManualEventRequest>(cancellationToken);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or BadHttpRequestException)
        {
            // Cuerpo que no es JSON o sin Content-Type JSON: nunca se cita el contenido.
            return BadRequest(context, "El cuerpo debe ser JSON: { \"url\": \"https://github.com/<owner>/<repo>/issues/<número>\", \"requestId\": \"<guid>\" }.");
        }

        if (request is null
            || !Guid.TryParse(request.RequestId, out var requestId)
            || requestId == Guid.Empty)
        {
            return BadRequest(context, InvalidRequestIdMessage);
        }

        var result = await ingestion.SubmitAsync(request.Url ?? string.Empty, requestId, cancellationToken);
        return result.Outcome switch
        {
            ManualEventOutcome.Created => Results.Json(SubmitManualEventResponse.From(result.Event!, created: true), statusCode: StatusCodes.Status201Created),
            ManualEventOutcome.Existing => Results.Ok(SubmitManualEventResponse.From(result.Event!, created: false)),
            ManualEventOutcome.SentryNotSupported => Results.Problem(
                statusCode: StatusCodes.Status501NotImplemented,
                title: "Fuente no disponible",
                detail: result.Reason,
                instance: context.Request.Path),
            ManualEventOutcome.Unavailable => Results.Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: "No se puede autorizar el Evento",
                detail: $"No se puede comprobar el permiso en GitHub: {result.Reason}.",
                instance: context.Request.Path),
            _ => BadRequest(context, result.Reason ?? "URL no admitida."),
        };
    }

    private static IResult BadRequest(HttpContext context, string detail) => Results.Problem(
        statusCode: StatusCodes.Status400BadRequest,
        title: "Petición no válida",
        detail: detail,
        instance: context.Request.Path);

    private static IResult Unavailable(HttpContext context, InstanceHealthStatus status, IReadOnlyList<string> reasons)
    {
        var detail = status == InstanceHealthStatus.Diagnostic
            ? $"{DiagnosticMessage}:\n{string.Join('\n', reasons.Select(r => "- " + r))}"
            : StartingMessage;

        return Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "CodeNetix no está listo",
            detail: detail,
            instance: context.Request.Path,
            extensions: new Dictionary<string, object?>
            {
                ["instanceStatus"] = status.ToString(),
                ["reasons"] = reasons,
            });
    }
}

internal sealed record SubmitManualEventRequest(string? Url, string? RequestId);

/// <param name="Status"><c>Accepted</c> o <c>Ignored</c>.</param>
/// <param name="Reason">Motivo si <c>Ignored</c> (p. ej. <c>"no autorizado"</c>); si no, <c>null</c>.</param>
/// <param name="Actor">Login de GitHub que disparó el Evento.</param>
/// <param name="Repository"><c>owner/repo</c> del Repositorio objetivo.</param>
internal sealed record SubmitManualEventResponse(Guid EventId, bool Created, string Status, string? Reason, string? Actor, string Repository)
{
    public static SubmitManualEventResponse From(ManualEventInfo info, bool created) =>
        new(info.Id, created, info.Status.ToString(), info.IgnoredReason, info.Actor, info.Repository);
}
