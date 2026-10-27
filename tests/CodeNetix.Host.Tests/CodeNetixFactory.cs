using System.Collections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.Hosting;

namespace CodeNetix.Host.Tests;

/// <summary>
/// Host de pruebas con un <c>DataDirectory</c> y un <c>WebRootPath</c> temporales. La ruta del YAML de
/// instancia apunta por defecto a un archivo vacío propio; <paramref name="settings"/> añade valores extra.
/// Hermético: las variables <c>CODENETIX__*</c> del proceso se ignoran salvo con
/// <paramref name="inheritCodeNetixEnvironment"/> (tests de entorno, colección no paralela).
/// </summary>
internal sealed class CodeNetixFactory(
    string dataDirectory,
    string webRoot,
    IReadOnlyDictionary<string, string?>? settings = null,
    bool inheritCodeNetixEnvironment = false) : WebApplicationFactory<Program>
{
    /// <summary>Configuración completa mínima (token de GitHub y una key de Proveedor).</summary>
    public static readonly IReadOnlyDictionary<string, string?> CompleteSettings = new Dictionary<string, string?>
    {
        ["CODENETIX:GitHub:Token"] = "ghp_testtoken0123456789abcdefghijklmn",
        ["CODENETIX:Providers:Anthropic:ApiKey"] = "sk-ant-testkey0123456789abcdefghijklmn",
    };

    private readonly string _emptyInstanceConfigPath = CreateEmptyInstanceConfig();

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
        };

        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            values[key] = value;
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
