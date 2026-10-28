using CodeNetix.Contracts;
using CodeNetix.Host.Configuration;

namespace CodeNetix.Host.Features.ReceiveWebhook;

/// <summary>
/// Puertos (AD-17): <c>:5000</c> sirve el Panel, <c>/api</c> y <c>/hubs</c>; <c>:5001</c> solo
/// <c>/webhooks/*</c>, sin <c>admin-token</c>. La decisión se toma por
/// <see cref="ConnectionInfo.LocalPort"/>, nunca por la cabecera <c>Host</c>.
/// </summary>
internal static class ListenPorts
{
    public const int Main = 5000;
    public const int Webhooks = 5001;

    public static readonly string[] Urls = [$"http://+:{Main}", $"http://+:{Webhooks}"];

    /// <summary><c>LocalPort</c> = 0 (TestServer) o cualquier otro puerto cuenta como <c>:5000</c>.</summary>
    public static bool IsWebhookPort(HttpContext context) => context.Connection.LocalPort == Webhooks;
}

/// <summary>
/// Guarda de puerto: en <c>:5001</c> todo lo que no sea <c>/webhooks/*</c> es 404; en <c>:5000</c>,
/// <c>/webhooks/*</c> es 404. Va antes de la autenticación: en <c>:5001</c> nunca hay 401.
/// </summary>
internal sealed class WebhookPortGuardMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        var isWebhookPath = context.Request.Path.StartsWithSegments(ReceiveWebhookEndpoint.Prefix, StringComparison.OrdinalIgnoreCase);
        return isWebhookPath == ListenPorts.IsWebhookPort(context)
            ? next(context)
            // Sin "instance": va antes de ApiRedactionMiddleware y no debe devolver la ruta (podría ser un secreto).
            : Results.Problem(
                    statusCode: StatusCodes.Status404NotFound,
                    title: "Recurso no encontrado")
                .ExecuteAsync(context);
    }
}

/// <summary>
/// <c>/webhooks/{**rest}</c> en <c>:5001</c>, provisional hasta la Épica 7 (firmas y Fuentes reales):
/// <c>Starting</c> o <c>Diagnostic</c> → 503 (AD-18); <c>Ready</c> → 404 "Fuente de webhook no disponible todavía".
/// </summary>
internal static class ReceiveWebhookEndpoint
{
    public const string Prefix = "/webhooks";
    public const string Route = Prefix + "/{**rest}";
    public const string SourceNotAvailableTitle = "Fuente de webhook no disponible todavía";

    public static IEndpointRouteBuilder MapReceiveWebhook(this IEndpointRouteBuilder endpoints)
    {
        endpoints.Map(Route, (HttpContext context, InstanceHealth health) =>
            {
                // Defensa en profundidad: la guarda de puerto ya filtra :5000.
                if (!ListenPorts.IsWebhookPort(context))
                {
                    return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Recurso no encontrado");
                }

                return health.Status == InstanceHealthStatus.Ready
                    ? Results.Problem(
                        statusCode: StatusCodes.Status404NotFound,
                        title: SourceNotAvailableTitle,
                        detail: "La recepción de webhooks llegará en una versión posterior.")
                    : Results.Problem(
                        statusCode: StatusCodes.Status503ServiceUnavailable,
                        title: "CodeNetix no está listo para recibir webhooks",
                        detail: "La instancia está arrancando o en modo diagnóstico; consulta /api/health en :5000.");
            })
            .WithName("ReceiveWebhook");

        return endpoints;
    }
}
