using CodeNetix.Agents;
using CodeNetix.Contracts;
using CodeNetix.GitHub;
using CodeNetix.Host.Access;
using CodeNetix.Host.Configuration;
using CodeNetix.Host.Secrets;
using CodeNetix.Ingestion;
using CodeNetix.Ingestion.Features.PollGitHub;
using CodeNetix.Ingestion.Features.ReceiveGitHubWebhook;
using CodeNetix.Persistence;
using CodeNetix.Pipeline;
using CodeNetix.Pipeline.Features.Budget;
using CodeNetix.Sandbox;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace CodeNetix.Host;

/// <summary>
/// Composición común de <c>serve</c> y <c>action</c> (AD-12: un binario, la misma seguridad): módulos, opciones de la
/// Configuración de instancia, redacción de secretos (AD-13), logs, salud y <see cref="StartupReconciler"/> (AD-18).
/// Cada modo añade después sus puertos, endpoints y publicadores.
/// </summary>
internal static class CodeNetixComposition
{
    public const string WorkspacesDirectoryName = "workspaces";

    public const string SandboxSectionName = InstanceOptions.SectionName + ":Sandbox";

    /// <summary>Variable estándar del cliente de Docker (se lee de la configuración, que incluye el entorno).</summary>
    public const string DockerHostKey = "DOCKER_HOST";

    /// <summary>Puerto del Egress Gateway LLM: <c>CODENETIX__Sandbox__LlmGatewayPort</c> o 8080.</summary>
    public static int LlmGatewayPort(IConfiguration configuration)
    {
        var configured = configuration.GetValue<int?>(SandboxSectionName + ":LlmGatewayPort");
        return configured is > 0 ? configured.Value : Features.ReceiveWebhook.ListenPorts.LlmGateway;
    }

    /// <summary>
    /// Elimina las fuentes <c>appsettings*.json</c> (relativas al directorio de trabajo): en <c>action</c> el
    /// directorio de trabajo es el repositorio objetivo (no confiable) y en <c>serve</c> la configuración solo viene
    /// del archivo YAML de instancia y de las variables de entorno.
    /// </summary>
    public static void RemoveAppSettingsSources(this IConfigurationBuilder configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var sources = configuration.Sources;
        for (var i = sources.Count - 1; i >= 0; i--)
        {
            if (sources[i] is JsonConfigurationSource json
                && json.Path is { } path
                && Path.GetFileName(path).StartsWith("appsettings", StringComparison.OrdinalIgnoreCase))
            {
                sources.RemoveAt(i);
            }
        }
    }

    public static void AddCodeNetixServices(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

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

        // CODENETIX__Polling__Repositories admite una cadena con comas, que el binder de colecciones no separa.
        builder.Services.AddOptions<InstanceOptions>()
            .PostConfigure<IConfiguration>((instance, configuration) =>
            {
                instance.Polling ??= new PollingInstanceOptions();
                var repositories = configuration.GetSection(PollingInstanceOptions.RepositoriesKey);
                instance.Polling.Repositories = repositories.Exists() ? PollingInstanceOptions.ReadRepositories(repositories) : [];

                instance.GitHub ??= new GitHubInstanceOptions();
                var webhookRepositories = configuration.GetSection(GitHubInstanceOptions.WebhookRepositoriesKey);
                instance.GitHub.WebhookRepositories = webhookRepositories.Exists() ? PollingInstanceOptions.ReadRepositories(webhookRepositories) : [];
            });
        builder.Services.AddOptions<PricingOptions>()
            .Configure<IOptions<InstanceOptions>>((pricing, instance) =>
            {
                pricing.Models.Clear();
                if (instance.Value.Pricing is not { Count: > 0 })
                {
                    return;
                }

                foreach (var (modelName, entry) in instance.Value.Pricing)
                {
                    if (!string.IsNullOrWhiteSpace(modelName)
                        && entry is { InputPerMillionUsd: >= 0 and var inputUsd, OutputPerMillionUsd: >= 0 and var outputUsd })
                    {
                        pricing.Models[modelName.Trim()] = new ModelPricing(inputUsd, outputUsd);
                    }
                }
            });

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
        builder.Services.AddHostedService<StartupReconciler>();
    }

    /// <summary>
    /// Sondeo de issues etiquetadas (FR-6), solo en <c>serve</c>: <see cref="GitHubPollingOptions"/> desde
    /// <c>CODENETIX__Polling__*</c> (owner/repo en minúsculas y sin duplicados; los inválidos ya dejan la instancia en
    /// <c>Diagnostic</c>, donde no se sondea) y el servicio de Ingestion.
    /// </summary>
    public static void AddGitHubPolling(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddCodeNetixGitHubPolling();
        builder.Services.AddOptions<GitHubPollingOptions>()
            .Configure<IOptions<InstanceOptions>>((polling, instance) =>
            {
                var source = instance.Value.Polling ?? new PollingInstanceOptions();
                polling.Repositories.Clear();
                foreach (var value in source.Repositories ?? [])
                {
                    if (PollingInstanceOptions.TryParseRepository(value, out var owner, out var repo)
                        && !polling.Repositories.Any(r => r.Owner == owner && r.Repo == repo))
                    {
                        polling.Repositories.Add(new PolledRepository(owner, repo));
                    }
                }

                polling.Label = source.EffectiveLabel;
                polling.Interval = TimeSpan.FromSeconds(source.IntervalSeconds);
            });
    }

    /// <summary>
    /// Webhook de GitHub (FR-7), solo en <c>serve</c>: <see cref="GitHubWebhookOptions"/> desde
    /// <c>CODENETIX__GitHub__WebhookSecret</c> (recortado; sin valor, desactivado), <c>CODENETIX__GitHub__WebhookRepositories</c>
    /// (en minúsculas) y la etiqueta de disparo compartida con el sondeo (<c>CODENETIX__Polling__Label</c>).
    /// </summary>
    public static void AddGitHubWebhooks(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddCodeNetixGitHubWebhooks();
        builder.Services.AddOptions<GitHubWebhookOptions>()
            .Configure<IOptions<InstanceOptions>>((webhook, instance) =>
            {
                var gitHub = instance.Value.GitHub ?? new GitHubInstanceOptions();
                webhook.Secret = gitHub.EffectiveWebhookSecret;
                webhook.Label = (instance.Value.Polling ?? new PollingInstanceOptions()).EffectiveLabel;
                webhook.Repositories.Clear();
                foreach (var value in gitHub.WebhookRepositories ?? [])
                {
                    if (PollingInstanceOptions.TryParseRepository(value, out var owner, out var repo))
                    {
                        webhook.Repositories.Add($"{owner}/{repo}");
                    }
                }
            });
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
