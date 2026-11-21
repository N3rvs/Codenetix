using CodeNetix.Agents;
using CodeNetix.Contracts;
using CodeNetix.GitHub;
using CodeNetix.Host.Access;
using CodeNetix.Host.Configuration;
using CodeNetix.Host.Features.GetEvent;
using CodeNetix.Host.Features.GetHealth;
using CodeNetix.Host.Features.GetRun;
using CodeNetix.Host.Features.GetRunEvents;
using CodeNetix.Host.Features.ListRuns;
using CodeNetix.Host.Features.ReceiveWebhook;
using CodeNetix.Host.Features.SubmitManualEvent;
using CodeNetix.Host.Hubs;
using CodeNetix.Host.Secrets;
using CodeNetix.Ingestion;
using CodeNetix.Persistence;
using CodeNetix.Pipeline;
using CodeNetix.Sandbox;
using CodeNetix.Sandbox.Gateway;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace CodeNetix.Host;

/// <summary>
/// <c>codenetix serve</c>: host completo. <c>:5000</c> sirve el Panel, <c>/api</c> y <c>/hubs</c>;
/// <c>:5001</c>, solo <c>/webhooks/*</c> (AD-17); <c>:8080</c>, solo <c>/llm/*</c> y <c>:8081</c>, proxy <c>CONNECT</c>
/// de NuGet para el Sandbox (AD-7).
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

        // Solo Host lee el entorno. Configuración de instancia = archivo YAML opcional + CODENETIX__*
        // (el entorno tiene prioridad: el archivo se inserta antes de las variables de entorno).
        // Un archivo inválido lanza InstanceConfigurationException aquí, antes de construir el host.
        builder.Configuration.AddInstanceConfigFile();

        var configuredLlmPort = builder.Configuration.GetValue<int?>(SandboxSectionName + ":LlmGatewayPort");
        var llmPort = configuredLlmPort is > 0 ? configuredLlmPort.Value : ListenPorts.LlmGateway;
        builder.WebHost.UseUrls($"http://+:{ListenPorts.Main}", $"http://+:{ListenPorts.Webhooks}", $"http://+:{llmPort}");

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

                var allowedHostsSection = configuration.GetSection(SandboxSectionName + ":AllowedNuGetHosts");
                if (allowedHostsSection.Exists())
                {
                    sandbox.AllowedNuGetHosts = ReadConfiguredHosts(allowedHostsSection);
                }
            });
        builder.Services.Configure<InstanceLimits>(builder.Configuration.GetSection(LimitsInstanceOptions.SectionName));
        builder.Services.Configure<InstanceOptions>(builder.Configuration.GetSection(InstanceOptions.SectionName));

        // Agents y el Egress Gateway no leen el entorno: Host resuelve Proveedor, URL, key y modelo de cada rol. Si la
        // configuración no es coherente, quedan nulos y StartupReconciler publica Diagnostic con el motivo.
        builder.Services.AddOptions<AgentsOptions>()
            .Configure<IOptions<InstanceOptions>>((agents, instance) =>
            {
                agents.SchemaRetries = instance.Value.Agents?.SchemaRetries ?? AgentsOptions.DefaultSchemaRetries;
                agents.Analyst = AgentEndpointResolver.ResolveAnalyst(instance.Value).Endpoint;
                agents.Coder = AgentEndpointResolver.ResolveCoder(instance.Value).Endpoint;
                agents.Reviewers = AgentEndpointResolver.ResolveReviewers(instance.Value).Endpoint;
                agents.ReviewerOverrides.Clear();
                foreach (var kind in Enum.GetValues<ReviewerKind>())
                {
                    if (AgentEndpointResolver.ResolveReviewer(instance.Value, kind).Endpoint is { } specific)
                    {
                        agents.ReviewerOverrides[kind] = specific;
                    }
                }
            });
        builder.Services.AddOptions<SandboxOptions>()
            .PostConfigure<IOptions<InstanceOptions>>((sandbox, instance) =>
            {
                if (AgentEndpointResolver.ResolveCoder(instance.Value).Endpoint is { } coder)
                {
                    sandbox.Coder = new SandboxCoderEndpoint(coder.Provider, coder.BaseUrl, coder.ApiKey, coder.Model);
                }
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
        builder.Services.AddSingleton<IRunEventPublisher, RunsHubEventPublisher>();
        builder.Services.AddSignalR();
        builder.Services.AddOpenApi();
        builder.Services.AddHostedService<StartupReconciler>();
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

    private static List<string> ReadConfiguredHosts(IConfigurationSection section)
    {
        var result = new List<string>();
        if (!string.IsNullOrWhiteSpace(section.Value))
        {
            foreach (var part in section.Value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!result.Contains(part, StringComparer.OrdinalIgnoreCase))
                {
                    result.Add(part);
                }
            }

            return result;
        }

        foreach (var child in section.GetChildren())
        {
            if (!string.IsNullOrWhiteSpace(child.Value))
            {
                var trimmed = child.Value.Trim();
                if (!result.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                {
                    result.Add(trimmed);
                }
            }
        }

        return result;
    }
}
