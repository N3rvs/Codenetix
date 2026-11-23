using CodeNetix.Contracts;
using CodeNetix.Host.Configuration;
using CodeNetix.Sandbox;
using CodeNetix.Sandbox.Gateway;
using Microsoft.Extensions.Options;

namespace CodeNetix.Host.Features.ReceiveWebhook;

/// <summary>
/// Puertos (AD-7, AD-17): <c>:5000</c> sirve el Panel, <c>/api</c> y <c>/hubs</c>; <c>:5001</c> solo
/// <c>/webhooks/*</c>, sin <c>admin-token</c>; <c>:8080</c> solo <c>/llm/*</c> (Egress Gateway para el Sandbox).
/// La decisión se toma por <see cref="ConnectionInfo.LocalPort"/>, nunca por la cabecera <c>Host</c>.
/// </summary>
internal static class ListenPorts
{
    public const int Main = 5000;
    public const int Webhooks = 5001;
    public const int LlmGateway = SandboxOptions.DefaultLlmGatewayPort;

    public static readonly string[] Urls = [$"http://+:{Main}", $"http://+:{Webhooks}", $"http://+:{LlmGateway}"];

    /// <summary><c>LocalPort</c> = 0 (TestServer) o cualquier otro puerto distinto de 5001 y 8080 cuenta como <c>:5000</c>.</summary>
    public static bool IsWebhookPort(HttpContext context) => context.Connection.LocalPort == Webhooks;

    public static bool IsLlmGatewayPort(HttpContext context, int configuredPort = LlmGateway) =>
        context.Connection.LocalPort == (configuredPort > 0 ? configuredPort : LlmGateway);
}

/// <summary>
/// Guarda de puerto: en <c>:5001</c> todo lo que no sea <c>/webhooks/*</c> es 404; en <c>:8080</c> todo lo que no sea
/// <c>/llm/{**rest}</c> es 404; en <c>:5000</c>, <c>/webhooks/*</c> y <c>/llm/*</c> son 404. Va antes de la
/// autenticación: en <c>:5001</c> nunca hay 401 y en <c>:8080</c> solo responde el Gateway LLM.
/// </summary>
internal sealed class WebhookPortGuardMiddleware(RequestDelegate next, IOptionsMonitor<SandboxOptions> sandboxOptions)
{
    public Task InvokeAsync(HttpContext context)
    {
        var isWebhookPath = context.Request.Path.StartsWithSegments(ReceiveWebhookEndpoint.Prefix, StringComparison.OrdinalIgnoreCase);
        var hasLlmPrefix = context.Request.Path.StartsWithSegments(LlmReverseProxy.Prefix, StringComparison.OrdinalIgnoreCase, out var llmRest);
        var isLlmSubPath = hasLlmPrefix && llmRest.HasValue && LlmReverseProxy.IsValidSubPath(llmRest.Value);

        var isWebhookPort = ListenPorts.IsWebhookPort(context);
        var isLlmPort = ListenPorts.IsLlmGatewayPort(context, sandboxOptions.CurrentValue.LlmGatewayPort);

        var allowed = isWebhookPort ? isWebhookPath
            : isLlmPort ? isLlmSubPath
            : !isWebhookPath && !hasLlmPrefix;

        return allowed
            ? next(context)
            // Sin "instance": va antes de ApiRedactionMiddleware y no debe devolver la ruta (podría ser un secreto).
            : Results.Problem(
                    statusCode: StatusCodes.Status404NotFound,
                    title: "Recurso no encontrado")
                .ExecuteAsync(context);
    }
}

/// <summary>
/// Webhooks en <c>:5001</c> (AD-17, FR-7), sin <c>admin-token</c>: solo la firma autentica.
/// <list type="bullet">
/// <item><c>POST /webhooks/github</c>: <c>Starting</c>/<c>Diagnostic</c> → 503 (AD-18); sin
/// <c>CODENETIX__GitHub__WebhookSecret</c> → 404 "no configurado"; cuerpo de más de 1 MiB → 413; firma no válida → 401 sin
/// detalle; si no, lo que decida <see cref="IGitHubWebhookIngestion"/> (200 <c>pong</c>/<c>ignored</c>, 202
/// <c>queued</c>/<c>duplicate</c>, 400, 415, 503 con la cola llena). Nunca llama a GitHub antes de responder.</item>
/// <item><c>POST /webhooks/sentry</c>: mismas comprobaciones previas con <c>CODENETIX__Sentry__WebhookSecret</c>; después lo
/// que decida <see cref="ISentryWebhookIngestion"/> (200 <c>ignored</c>, también para un proyecto sin correspondencia, que
/// queda registrado; 202 <c>queued</c>/<c>duplicate</c>; 400, 401, 415, 503 con la cola llena).</item>
/// <item>El resto de <c>/webhooks/{**rest}</c>: 503 si no está <c>Ready</c>; si no, 404 "Fuente de webhook desconocida".</item>
/// </list>
/// </summary>
internal static class ReceiveWebhookEndpoint
{
    public const string Prefix = "/webhooks";
    public const string Route = Prefix + "/{**rest}";
    public const string GitHubRoute = Prefix + "/github";
    public const string SentryRoute = Prefix + "/sentry";
    public const string UnknownSourceTitle = "Fuente de webhook desconocida";
    public const string GitHubNotConfiguredTitle = "Webhook de GitHub no configurado";
    public const string SentryNotConfiguredTitle = "Webhook de Sentry no configurado";
    public const string InvalidSignatureTitle = "Firma no válida";

    public static IEndpointRouteBuilder MapReceiveWebhook(this IEndpointRouteBuilder endpoints)
    {
        endpoints.Map(GitHubRoute, HandleGitHubAsync)
            .WithName("ReceiveGitHubWebhook")
            .ExcludeFromDescription();

        endpoints.Map(SentryRoute, HandleSentryAsync)
            .WithName("ReceiveSentryWebhook")
            .ExcludeFromDescription();

        endpoints.Map(Route, (HttpContext context, InstanceHealth health) =>
            {
                // Defensa en profundidad: la guarda de puerto ya filtra :5000.
                if (!ListenPorts.IsWebhookPort(context))
                {
                    return NotFound();
                }

                return health.Status == InstanceHealthStatus.Ready
                    ? Results.Problem(
                        statusCode: StatusCodes.Status404NotFound,
                        title: UnknownSourceTitle,
                        detail: "Los webhooks admitidos son /webhooks/github y /webhooks/sentry.")
                    : NotReady();
            })
            .WithName("ReceiveWebhook");

        return endpoints;
    }

    /// <summary>
    /// Comprobaciones comunes (orden de la spec): puerto, método, salud, fuente activada y tamaño del cuerpo. Devuelve la
    /// respuesta de error o, si todo va bien, <c>null</c> y el cuerpo crudo.
    /// </summary>
    private static async Task<(IResult? Error, ReadOnlyMemory<byte> Body)> PrepareAsync(
        HttpContext context,
        InstanceHealth health,
        bool enabled,
        int maxBodyBytes,
        string notConfiguredTitle,
        string secretVariable,
        CancellationToken cancellationToken)
    {
        if (!ListenPorts.IsWebhookPort(context))
        {
            return (NotFound(), default);
        }

        if (!HttpMethods.IsPost(context.Request.Method))
        {
            context.Response.Headers.Allow = HttpMethods.Post;
            return (Results.Problem(statusCode: StatusCodes.Status405MethodNotAllowed, title: "Método no permitido", detail: "Usa POST."), default);
        }

        if (health.Status != InstanceHealthStatus.Ready)
        {
            return (NotReady(), default);
        }

        if (!enabled)
        {
            return (Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: notConfiguredTitle,
                detail: $"Define {secretVariable} para activar este webhook."), default);
        }

        var body = await ReadBodyAsync(context.Request, maxBodyBytes, cancellationToken);
        if (body is null)
        {
            return (Results.Problem(
                statusCode: StatusCodes.Status413PayloadTooLarge,
                title: "Cuerpo demasiado grande",
                detail: $"El webhook admite como mucho {maxBodyBytes} bytes."), default);
        }

        return (null, body.Value);
    }

    private static async Task<IResult> HandleSentryAsync(HttpContext context, InstanceHealth health, CancellationToken cancellationToken)
    {
        // Se resuelve aquí (no como parámetro): fuera de serve no está registrado y equivale a desactivado.
        var ingestion = health.Status == InstanceHealthStatus.Ready ? context.RequestServices.GetService<ISentryWebhookIngestion>() : null;
        var (error, body) = await PrepareAsync(
            context,
            health,
            ingestion is { Enabled: true },
            ingestion?.MaxBodyBytes ?? 0,
            SentryNotConfiguredTitle,
            "CODENETIX__Sentry__WebhookSecret",
            cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var headers = context.Request.Headers;

        // Una sola cabecera de firma: repetida (o vacía) es una firma no válida.
        var signatures = headers["Sentry-Hook-Signature"];
        var receipt = await ingestion!.ReceiveAsync(
            new SentryWebhookDelivery(
                headers["Sentry-Hook-Resource"].ToString(),
                headers["Request-ID"].ToString(),
                signatures.Count == 1 ? signatures[0] : null,
                context.Request.ContentType,
                body),
            cancellationToken);

        return receipt.Outcome switch
        {
            SentryWebhookOutcome.InvalidSignature => Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: InvalidSignatureTitle),
            SentryWebhookOutcome.UnsupportedContentType => Results.Problem(
                statusCode: StatusCodes.Status415UnsupportedMediaType,
                title: "Tipo de contenido no admitido",
                detail: receipt.Reason),
            SentryWebhookOutcome.InvalidPayload => Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Payload no válido",
                detail: receipt.Reason),
            SentryWebhookOutcome.Ignored or SentryWebhookOutcome.Unmapped => Results.Ok(new WebhookResponse("ignored", receipt.Reason)),
            SentryWebhookOutcome.Queued => Results.Json(new WebhookResponse("queued", null), statusCode: StatusCodes.Status202Accepted),
            SentryWebhookOutcome.Duplicate => Results.Json(new WebhookResponse("duplicate", null), statusCode: StatusCodes.Status202Accepted),
            SentryWebhookOutcome.QueueFull => Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Cola de webhooks llena",
                detail: receipt.Reason),
            _ => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: SentryNotConfiguredTitle),
        };
    }

    private static async Task<IResult> HandleGitHubAsync(HttpContext context, InstanceHealth health, CancellationToken cancellationToken)
    {
        // Se resuelve aquí (no como parámetro): fuera de serve no está registrado y equivale a desactivado.
        var ingestion = health.Status == InstanceHealthStatus.Ready ? context.RequestServices.GetService<IGitHubWebhookIngestion>() : null;
        var (error, body) = await PrepareAsync(
            context,
            health,
            ingestion is { Enabled: true },
            ingestion?.MaxBodyBytes ?? 0,
            GitHubNotConfiguredTitle,
            "CODENETIX__GitHub__WebhookSecret",
            cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var headers = context.Request.Headers;

        // Una sola cabecera de firma: repetida (o vacía) es una firma no válida, nunca "alguna de ellas".
        var signatures = headers["X-Hub-Signature-256"];
        var receipt = ingestion!.Receive(new GitHubWebhookDelivery(
            headers["X-GitHub-Event"].ToString(),
            headers["X-GitHub-Delivery"].ToString(),
            signatures.Count == 1 ? signatures[0] : null,
            context.Request.ContentType,
            body));

        return receipt.Outcome switch
        {
            GitHubWebhookOutcome.InvalidSignature => Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: InvalidSignatureTitle),
            GitHubWebhookOutcome.UnsupportedContentType => Results.Problem(
                statusCode: StatusCodes.Status415UnsupportedMediaType,
                title: "Tipo de contenido no admitido",
                detail: receipt.Reason),
            GitHubWebhookOutcome.InvalidPayload => Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Payload no válido",
                detail: receipt.Reason),
            GitHubWebhookOutcome.Pong => Results.Ok(new WebhookResponse("pong", null)),
            GitHubWebhookOutcome.Ignored => Results.Ok(new WebhookResponse("ignored", receipt.Reason)),
            GitHubWebhookOutcome.Queued => Results.Json(new WebhookResponse("queued", null), statusCode: StatusCodes.Status202Accepted),
            GitHubWebhookOutcome.Duplicate => Results.Json(new WebhookResponse("duplicate", null), statusCode: StatusCodes.Status202Accepted),
            GitHubWebhookOutcome.QueueFull => Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Cola de webhooks llena",
                detail: receipt.Reason),
            _ => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: GitHubNotConfiguredTitle),
        };
    }

    /// <summary>Cuerpo crudo (la firma va sobre estos bytes); <c>null</c> si supera <paramref name="maxBytes"/>.</summary>
    private static async Task<ReadOnlyMemory<byte>?> ReadBodyAsync(HttpRequest request, int maxBytes, CancellationToken cancellationToken)
    {
        if (request.ContentLength > maxBytes)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static IResult NotFound() => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Recurso no encontrado");

    private static IResult NotReady() => Results.Problem(
        statusCode: StatusCodes.Status503ServiceUnavailable,
        title: "CodeNetix no está listo para recibir webhooks",
        detail: "La instancia está arrancando o en modo diagnóstico; consulta /api/health en :5000.");
}

/// <param name="Result"><c>pong</c>, <c>ignored</c>, <c>queued</c> o <c>duplicate</c>.</param>
/// <param name="Reason">Por qué se ignora (visible en el registro de entregas de GitHub o Sentry).</param>
internal sealed record WebhookResponse(string Result, string? Reason);
