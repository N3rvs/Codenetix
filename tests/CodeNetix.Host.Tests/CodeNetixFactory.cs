using System.Collections;
using CodeNetix.Agents.ChatClients;
using CodeNetix.Contracts;
using CodeNetix.Sandbox;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CodeNetix.Host.Tests;

/// <summary>
/// Host de pruebas con un <c>DataDirectory</c> y un <c>WebRootPath</c> temporales. La ruta del YAML de
/// instancia apunta por defecto a un archivo vacío propio; <paramref name="settings"/> añade valores extra.
/// Hermético: las variables <c>CODENETIX__*</c> del proceso se ignoran salvo con
/// <paramref name="inheritCodeNetixEnvironment"/> (tests de entorno, colección no paralela).
/// La API de GitHub es <paramref name="gitHub"/> o, si no se pasa (ni <see cref="GitHubApiUrlKey"/> en
/// <paramref name="settings"/>), un <see cref="FakeGitHub"/> propio con permiso <c>write</c> por defecto. Los clones
/// van a <see cref="LocalGitRepos.Default"/> salvo que <see cref="CloneBaseUrlKey"/> indique otra base.
/// Los agentes usan <see cref="ChatClient"/> (un <see cref="FakeChatClient"/> que responde una Spec válida) salvo
/// con <see cref="UseRealChatClient"/>: ningún test llama a un Proveedor real.
/// El Sandbox es <see cref="Sandbox"/> (un <see cref="FakeSandboxManager"/>) y la comprobación de Docker,
/// <see cref="SandboxHealth"/> (sin motivos): ningún test de Host necesita Docker ni entra en <c>Diagnostic</c> por él.
/// </summary>
internal sealed class CodeNetixFactory(
    string dataDirectory,
    string webRoot,
    IReadOnlyDictionary<string, string?>? settings = null,
    bool inheritCodeNetixEnvironment = false,
    FakeGitHub? gitHub = null) : WebApplicationFactory<Program>
{
    public const string GitHubApiUrlKey = "CODENETIX:GitHub:ApiUrl";

    /// <summary>Base de clonado; por defecto <see cref="LocalGitRepos.Default"/> (nunca la red).</summary>
    public const string CloneBaseUrlKey = "CODENETIX:GitHub:CloneBaseUrl";

    private FakeGitHub? _ownedGitHub;

    /// <summary>Cliente de chat falso que reciben todos los agentes (salvo con <see cref="UseRealChatClient"/>).</summary>
    public FakeChatClient ChatClient { get; } = new();

    /// <summary>Fábrica falsa registrada (para saber con qué Proveedor y modelo se creó el cliente).</summary>
    public FakeChatClientFactory ChatClientFactory => _chatClientFactory ??= new FakeChatClientFactory(ChatClient);

    /// <summary>Usa la fábrica real compatible con OpenAI (solo contra un servidor falso local).</summary>
    public bool UseRealChatClient { get; init; }

    private FakeChatClientFactory? _chatClientFactory;

    /// <summary>Gestor de Sandbox falso que recibe el orquestador.</summary>
    public FakeSandboxManager Sandbox { get; } = new();

    /// <summary>Comprobación de Docker falsa del arranque (sin motivos por defecto).</summary>
    public FakeSandboxHealthCheck SandboxHealth { get; } = new();

    /// <summary>Provisionador de herramientas falso que no toca npm ni Docker.</summary>
    public FakeSandboxToolsProvisioner ToolsProvisioner { get; } = new();

    /// <summary>Ejecutor de tests falso (por defecto aprueba con 1 test nuevo).</summary>
    public FakeTestRunner TestRunner { get; } = new();

    /// <summary>La API de GitHub falsa en uso (la pasada o la propia); <c>null</c> si los ajustes fijan otra URL.</summary>
    public FakeGitHub? GitHub => gitHub ?? _ownedGitHub;

    /// <summary>Configuración completa mínima (token de GitHub y una key de Proveedor).</summary>
    public static readonly IReadOnlyDictionary<string, string?> CompleteSettings = new Dictionary<string, string?>
    {
        ["CODENETIX:GitHub:Token"] = "ghp_testtoken0123456789abcdefghijklmn",
        ["CODENETIX:Providers:Anthropic:ApiKey"] = "sk-ant-testkey0123456789abcdefghijklmn",
    };

    /// <summary>
    /// <c>AccessToken</c> que la fábrica fija por defecto (sobrescribible con
    /// <see cref="AccessTokenKey"/> en <paramref name="settings"/>; con <c>null</c> se usa el archivo).
    /// </summary>
    public const string TestAccessToken = "test-access-token-0123456789abcdef";

    public const string AccessTokenKey = "CODENETIX:Host:AccessToken";

    private readonly string _emptyInstanceConfigPath = CreateEmptyInstanceConfig();

    /// <summary>Cliente con <c>Authorization: Bearer <see cref="TestAccessToken"/></c>.</summary>
    public HttpClient CreateAuthorizedClient() => CreateAuthorizedClient(TestAccessToken);

    public HttpClient CreateAuthorizedClient(string token)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static string CreateEmptyInstanceConfig()
    {
        var path = Path.Combine(Path.GetTempPath(), "codenetix-tests-" + Guid.NewGuid().ToString("N") + ".instance.yml");
        File.WriteAllText(path, string.Empty);
        return path;
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // La configuración de host se pasa al punto de entrada como "--clave=valor": así es visible
        // en WebApplicationBuilder.Configuration antes de Build() (ServeMode resuelve ahí la ruta del YAML).
        var values = new Dictionary<string, string?>
        {
            ["CODENETIX:Persistence:DataDirectory"] = dataDirectory,
            ["CODENETIX:InstanceConfigPath"] = _emptyInstanceConfigPath,
            [AccessTokenKey] = TestAccessToken,
        };

        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            values[key] = value;
        }

        if (!values.ContainsKey(CloneBaseUrlKey))
        {
            // Nunca se clona de la red: por defecto, los repositorios locales compartidos (o/r).
            values[CloneBaseUrlKey] = LocalGitRepos.Default.BaseUrl;
        }

        if (!values.ContainsKey(GitHubApiUrlKey))
        {
            // CreateHost es síncrono: la API falsa se arranca aquí, una sola vez por fábrica.
            var fake = gitHub ?? (_ownedGitHub ??= FakeGitHub.StartAsync().GetAwaiter().GetResult());
            values[GitHubApiUrlKey] = fake.BaseUrl;
        }

        builder.ConfigureHostConfiguration(config => config.AddInMemoryCollection(values));
        return base.CreateHost(builder);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Fuera de Development: así no se superponen los static web assets del wwwroot real
        // del proyecto (el Panel compilado) sobre el WebRootPath temporal.
        builder.UseEnvironment("Testing");
        builder.UseWebRoot(webRoot);

        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<ISandboxManager>(Sandbox);
            services.AddSingleton<ISandboxHealthCheck>(SandboxHealth);
            services.AddSingleton<ISandboxToolsProvisioner>(ToolsProvisioner);
            services.AddSingleton<ITestRunner>(TestRunner);
        });

        if (!UseRealChatClient)
        {
            builder.ConfigureTestServices(services => services.AddSingleton<IAgentChatClientFactory>(ChatClientFactory));
        }

        if (!inheritCodeNetixEnvironment)
        {
            builder.ConfigureAppConfiguration(config =>
            {
                for (var i = 0; i < config.Sources.Count; i++)
                {
                    if (config.Sources[i] is EnvironmentVariablesConfigurationSource env)
                    {
                        config.Sources[i] = new MemoryConfigurationSource { InitialData = EnvironmentWithoutCodeNetix(env.Prefix) };
                    }
                }
            });
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (_ownedGitHub is not null)
        {
            await _ownedGitHub.DisposeAsync();
        }

        try
        {
            File.Delete(_emptyInstanceConfigPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Limpieza de mejor esfuerzo.
        }
    }

    /// <summary>Instantánea del entorno (con el mismo prefijo) sin ninguna clave <c>CODENETIX</c>.</summary>
    private static Dictionary<string, string?> EnvironmentWithoutCodeNetix(string? prefix)
    {
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var key = (string)entry.Key;
            if (!string.IsNullOrEmpty(prefix))
            {
                if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                key = key[prefix.Length..];
            }

            key = key.Replace("__", ConfigurationPath.KeyDelimiter, StringComparison.Ordinal);
            if (key.StartsWith("CODENETIX", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            data[key] = (string?)entry.Value;
        }

        return data;
    }
}
