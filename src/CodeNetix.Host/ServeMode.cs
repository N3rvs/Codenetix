using CodeNetix.Agents;
using CodeNetix.Contracts;
using CodeNetix.Host.Access;
using CodeNetix.Host.Configuration;
using CodeNetix.Host.Features.GetEvent;
using CodeNetix.Host.Features.GetHealth;
using CodeNetix.Host.Features.GetRun;
using CodeNetix.Host.Features.GetRunEvents;
using CodeNetix.Host.Features.ReceiveWebhook;
using CodeNetix.Host.Features.SubmitManualEvent;
using CodeNetix.GitHub;
using CodeNetix.Host.Secrets;
using CodeNetix.Ingestion;
using CodeNetix.Persistence;
using CodeNetix.Pipeline;
using CodeNetix.Sandbox;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace CodeNetix.Host;

/// <summary>
/// <c>codenetix serve</c>: host completo. <c>:5000</c> sirve el Panel, <c>/api</c> y <c>/hubs</c>;
/// <c>:5001</c>, solo <c>/webhooks/*</c> (AD-17).
/// </summary>
internal static class ServeMode
{
    public const string WorkspacesDirectoryName = "workspaces";

    public const string SandboxSectionName = InstanceOptions.SectionName + ":Sandbox";

    /// <summary>Variable estándar del cliente de Docker (se lee de la configuración, que incluye el entorno).</summary>
    public const string DockerHostKey = "DOCKER_HOST";

    public static async Task<int> RunAsync(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.UseUrls(ListenPorts.Urls);

        // Solo Host lee el entorno. Configuración de instancia = archivo YAML opcional + CODENETIX__*
        // (el entorno tiene prioridad: el archivo se inserta antes de las variables de entorno).
        // Un archivo inválido lanza InstanceConfigurationException aquí, antes de construir el host.
        builder.Configuration.AddInstanceConfigFile();

        // Exponer la instancia sin AccessToken aborta aquí, antes de construir el host (Kestrel no arranca).
        AccessOptionsValidator.EnsureValid(builder.Configuration);

        builder.Services.AddCodeNetixPersistence();
        builder.Services.AddCodeNetixIngestion();
        builder.Services.AddCodeNetixPipeline();
        builder.Services.AddCodeNetixGitHub();
        builder.Services.AddCodeNetixAgents();
        builder.Services.AddCodeNetixSandbox();
        builder.Services.Configure<PersistenceOptions>(builder.Configuration.GetSection("CODENETIX:Persistence"));
        builder.Services.Configure<GitHubOptions>(builder.Configuration.GetSection(InstanceOptions.SectionName + ":GitHub"));

        // Árboles de trabajo de las Ejecuciones en <DataDirectory>/workspaces (GitHub no referencia Persistence).
        builder.Services.AddOptions<GitHubOptions>()
            .PostConfigure<IOptions<PersistenceOptions>>((gitHub, persistence) =>
                gitHub.WorkspacesDirectory ??= Path.Combine(persistence.Value.DataDirectory, WorkspacesDirectoryName));
        // Sandbox: CODENETIX__Sandbox__*; sin DockerEndpoint, DOCKER_HOST y, si tampoco, el socket por defecto del sistema.
        builder.Services.Configure<SandboxOptions>(builder.Configuration.GetSection(SandboxSectionName));
        builder.Services.AddOptions<SandboxOptions>()
            .PostConfigure<IConfiguration>((sandbox, configuration) =>
            {
                if (string.IsNullOrWhiteSpace(sandbox.DockerEndpoint) && !string.IsNullOrWhiteSpace(configuration[DockerHostKey]))
                {
                    sandbox.DockerEndpoint = configuration[DockerHostKey];
                }
            });
        builder.Services.Configure<InstanceLimits>(builder.Configuration.GetSection(LimitsInstanceOptions.SectionName));
        builder.Services.Configure<InstanceOptions>(builder.Configuration.GetSection(InstanceOptions.SectionName));

        // Agents no lee el entorno: Host resuelve Proveedor, URL, key y modelo de cada rol. Si la configuración no es
        // coherente, Analyst queda nulo y StartupReconciler publica Diagnostic con el motivo (no se procesa nada).
        builder.Services.AddOptions<AgentsOptions>()
            .Configure<IOptions<InstanceOptions>>((agents, instance) =>
            {
                agents.SchemaRetries = instance.Value.Agents?.SchemaRetries ?? AgentsOptions.DefaultSchemaRetries;
                agents.Analyst = AgentEndpointResolver.ResolveAnalyst(instance.Value).Endpoint;
            });

        // Redacción (AD-13): un único ISecretRedactor, aplicado en el sumidero de logs y en /api.
        builder.Services.AddSingleton<ISecretRedactor>(sp => new SecretRedactor(sp.GetRequiredService<IConfiguration>()));
        builder.Logging.ClearProviders();
        builder.Logging
            .AddConsole(options => options.FormatterName = RedactingConsoleFormatter.FormatterName)
            .AddConsoleFormatter<RedactingConsoleFormatter, ConsoleFormatterOptions>();

        // EF Core registra cada comando SQL en Information: solo avisos y errores.
        builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);

        builder.Services.AddSingleton<InstanceHealth>();
        builder.Services.AddSingleton<IInstanceHealth>(sp => sp.GetRequiredService<InstanceHealth>());
        builder.Services.AddSingleton<AdminTokenStore>();
        builder.Services.AddHostedService<StartupReconciler>();
        builder.Services.AddProblemDetails();

        var app = builder.Build();

        // Resuelto antes de arrancar: si el arranque falla, el contenedor ya estará liberado.
        var redactor = app.Services.GetRequiredService<ISecretRedactor>();

        // :5001 solo /webhooks/*; :5000 nunca /webhooks/* (por LocalPort, no por la cabecera Host).
        app.UseMiddleware<WebhookPortGuardMiddleware>();

        app.UseMiddleware<ApiRedactionMiddleware>();

        // Antes de los archivos estáticos y del enrutado: /api desconocido sin token -> 401, no 404.
        app.UseMiddleware<AdminTokenMiddleware>();

        app.UseDefaultFiles();
        app.UseStaticFiles();

        app.MapGetHealth();
        app.MapReceiveWebhook();
        app.MapSubmitManualEvent();
        app.MapGetEvent();
        app.MapGetRun();
        app.MapGetRunEvents();

        // /api desconocido -> 404 (ProblemDetails), nunca index.html.
        app.Map("/api/{**rest}", (HttpContext context) => Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Recurso no encontrado",
            instance: context.Request.Path));

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
