using CodeNetix.Contracts;
using CodeNetix.Host.Access;
using CodeNetix.Host.Configuration;
using CodeNetix.Host.Features.CancelRun;
using CodeNetix.Host.Features.GetEvent;
using CodeNetix.Host.Features.GetHealth;
using CodeNetix.Host.Features.GetRun;
using CodeNetix.Host.Features.GetRunEvents;
using CodeNetix.Host.Features.ListRuns;
using CodeNetix.Host.Features.ReceiveWebhook;
using CodeNetix.Host.Features.SubmitManualEvent;
using CodeNetix.Host.Hubs;
using CodeNetix.Host.Secrets;
using CodeNetix.Sandbox.Gateway;

namespace CodeNetix.Host;

/// <summary>
/// <c>codenetix serve</c>: host completo. <c>:5000</c> sirve el Panel, <c>/api</c> y <c>/hubs</c>;
/// <c>:5001</c>, solo <c>/webhooks/*</c> (AD-17); <c>:8080</c>, solo <c>/llm/*</c> y <c>:8081</c>, proxy <c>CONNECT</c>
/// de NuGet para el Sandbox (AD-7). La composición de módulos es la común (<see cref="CodeNetixComposition"/>).
/// </summary>
internal static class ServeMode
{
    public static async Task<int> RunAsync(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Solo Host lee el entorno. Nunca se cargan appsettings*.json del directorio de trabajo.
        // Configuración de instancia = archivo YAML opcional + CODENETIX__*
        // (el entorno tiene prioridad: el archivo se inserta antes de las variables de entorno).
        // Un archivo inválido lanza InstanceConfigurationException aquí, antes de construir el host.
        builder.Configuration.RemoveAppSettingsSources();
        builder.Configuration.AddInstanceConfigFile();

        var llmPort = CodeNetixComposition.LlmGatewayPort(builder.Configuration);
        builder.WebHost.UseUrls($"http://+:{ListenPorts.Main}", $"http://+:{ListenPorts.Webhooks}", $"http://+:{llmPort}");

        // Exponer la instancia sin AccessToken aborta aquí, antes de construir el host (Kestrel no arranca).
        AccessOptionsValidator.EnsureValid(builder.Configuration);

        builder.AddCodeNetixServices();

        builder.Services.AddSingleton<IRunEventPublisher, RunsHubEventPublisher>();
        builder.Services.AddSignalR();
        builder.Services.AddOpenApi(options =>
        {
            options.AddDocumentTransformer(async (document, context, cancellationToken) =>
            {
                if (document.Paths is not null
                    && document.Paths.TryGetValue(SubmitManualEventEndpoint.Route, out var pathItem)
                    && pathItem?.Operations is not null
                    && pathItem.Operations.TryGetValue(HttpMethod.Post, out var postOperation))
                {
                    var schema = await context.GetOrCreateSchemaAsync(typeof(SubmitManualEventRequest), cancellationToken: cancellationToken);
                    document.Components ??= new Microsoft.OpenApi.OpenApiComponents();
                    document.Components.Schemas ??= new Dictionary<string, Microsoft.OpenApi.IOpenApiSchema>();
                    document.Components.Schemas[nameof(SubmitManualEventRequest)] = schema;

                    postOperation.RequestBody = new Microsoft.OpenApi.OpenApiRequestBody
                    {
                        Required = true,
                        Content = new Dictionary<string, Microsoft.OpenApi.OpenApiMediaType>
                        {
                            ["application/json"] = new Microsoft.OpenApi.OpenApiMediaType
                            {
                                Schema = new Microsoft.OpenApi.OpenApiSchemaReference(nameof(SubmitManualEventRequest), document),
                            },
                        },
                    };
                }
            });
        });
        builder.Services.AddProblemDetails();

        var app = builder.Build();

        // Resuelto antes de arrancar: si el arranque falla, el contenedor ya estará liberado.
        var redactor = app.Services.GetRequiredService<ISecretRedactor>();

        // :5001 solo /webhooks/*; :8080 solo /llm/*; :5000 ni /webhooks/* ni /llm/* (por LocalPort, no por Host).
        app.UseMiddleware<WebhookPortGuardMiddleware>();

        app.UseMiddleware<ApiRedactionMiddleware>();

        // Antes de los archivos estáticos y del enrutado: /api desconocido sin token -> 401, no 404.
        app.UseMiddleware<AdminTokenMiddleware>();

        app.UseDefaultFiles();
        app.UseStaticFiles();

        app.MapOpenApi();
        app.MapGetHealth();
        app.MapReceiveWebhook();
        app.MapLlmGateway();
        app.MapSubmitManualEvent();
        app.MapGetEvent();
        app.MapListRuns();
        app.MapGetRun();
        app.MapGetRunEvents();
        app.MapCancelRun();
        app.MapHub<RunsHub>(RunsHub.Route);

        // /api, /hubs y /openapi desconocidos -> 404 (ProblemDetails), nunca index.html.
        static IResult NotFoundProblem(HttpContext context) => Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Recurso no encontrado",
            instance: context.Request.Path);

        app.Map("/api/{**rest}", NotFoundProblem).ExcludeFromDescription();
        app.Map("/hubs/{**rest}", NotFoundProblem).ExcludeFromDescription();
        app.Map("/openapi/{**rest}", NotFoundProblem).ExcludeFromDescription();

        // Rutas del Panel (SPA) -> index.html.
        app.MapFallbackToFile("index.html");

        try
        {
            await app.RunAsync();
            return CodeNetixCommandLine.ExitOk;
        }
        catch (Exception ex)
        {
            // StartupReconciler y el host ya registraron el detalle (el logger puede estar liberado aquí);
            // solo se asegura un mensaje final (redactado: no pasa por el sumidero de logs) y un código != 0.
            await Console.Error.WriteLineAsync(redactor.Redact($"CodeNetix no ha podido arrancar: {ex.Message}"));
            return CodeNetixCommandLine.ExitFailure;
        }
    }
}
