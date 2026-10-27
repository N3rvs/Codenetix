using System.Collections;
using System.Net.Http.Json;
using CodeNetix.Host.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CodeNetix.Host.Tests;

/// <summary>
/// Filas de la matriz que necesitan variables de entorno reales del proceso o capturar stderr:
/// colección no paralela.
/// </summary>
[Collection(nameof(ConsoleCollection))]
public sealed class EnvironmentConfigurationTests : IDisposable
{
    private const string TokenVariable = "CODENETIX__GitHub__Token";
    private const string ProviderVariable = "CODENETIX__Providers__Anthropic__ApiKey";
    private const string EnvToken = "ghp_fromenvironment0123456789abcdefENV1";
    private const string EnvKey = "sk-ant-fromenvironment0123456789abcdefENV2";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "codenetix-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _dataDirectory;
    private readonly string _webRoot;
    private readonly Dictionary<string, string?> _originalCodeNetixVariables = new(StringComparer.Ordinal);

    public EnvironmentConfigurationTests()
    {
        // Hermético: se retiran las CODENETIX__* del desarrollador y se restauran en Dispose.
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var name = (string)entry.Key;
            if (name.StartsWith("CODENETIX__", StringComparison.OrdinalIgnoreCase))
            {
                _originalCodeNetixVariables[name] = (string?)entry.Value;
                Environment.SetEnvironmentVariable(name, null);
            }
        }

        _dataDirectory = Path.Combine(_root, "data");
        _webRoot = Path.Combine(_root, "wwwroot");
        Directory.CreateDirectory(_webRoot);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Complete_configuration_from_environment_is_Ready()
    {
        Environment.SetEnvironmentVariable(TokenVariable, EnvToken);
        Environment.SetEnvironmentVariable(ProviderVariable, EnvKey);

        await using var factory = new CodeNetixFactory(_dataDirectory, _webRoot, inheritCodeNetixEnvironment: true);
        using var client = factory.CreateClient();

        var body = await client.GetFromJsonAsync<HealthBody>("/api/health", Ct);

        Assert.Equal("Ready", body?.Status);
        Assert.Empty(body!.Reasons);
        await factory.Services.GetRequiredService<CodeNetix.Contracts.IInstanceHealth>()
            .WhenReadyAsync(Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct);
    }

    [Fact]
    public async Task Environment_wins_over_yaml()
    {
        Environment.SetEnvironmentVariable(TokenVariable, EnvToken);
        var yamlPath = WriteYaml("""
            GitHub:
              Token: ghp_fromyamlfile0123456789abcdefYAML
            Providers:
              OpenAI:
                ApiKey: sk-fromyamlfile0123456789abcdefYAML
            """);

        await using var factory = new CodeNetixFactory(
            _dataDirectory,
            _webRoot,
            new Dictionary<string, string?> { ["CODENETIX:InstanceConfigPath"] = yamlPath },
            inheritCodeNetixEnvironment: true);
        using var client = factory.CreateClient();

        var body = await client.GetFromJsonAsync<HealthBody>("/api/health", Ct);
        var options = factory.Services.GetRequiredService<IOptions<InstanceOptions>>().Value;

        Assert.Equal("Ready", body?.Status);
        Assert.Equal(EnvToken, options.GitHub.Token);
        Assert.Equal("sk-fromyamlfile0123456789abcdefYAML", options.Providers["OpenAI"].ApiKey);
    }

    [Fact]
    public async Task Invalid_yaml_exits_non_zero_naming_the_path_without_content()
    {
        const string secretInFile = "ghp_secretinbrokenfile0123456789abcdef";
        var yamlPath = WriteYaml($"""
            GitHub:
              Token: [{secretInFile}
            Providers: {"{"} : :
            """);

        var (exitCode, output) = await RunServeAsync(yamlPath);

        Assert.Equal(CodeNetixCommandLine.ExitFailure, exitCode);
        Assert.Contains(yamlPath, output, StringComparison.Ordinal);
        Assert.DoesNotContain(secretInFile, output, StringComparison.Ordinal);
        Assert.DoesNotContain("ghp_", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Explicit_missing_config_path_exits_non_zero_naming_the_path()
    {
        var missingPath = Path.Combine(_root, "config", "no-existe.yml");

        var (exitCode, output) = await RunServeAsync(missingPath);

        Assert.Equal(CodeNetixCommandLine.ExitFailure, exitCode);
        Assert.Contains(missingPath, output, StringComparison.Ordinal);
    }

    private async Task<(int ExitCode, string Output)> RunServeAsync(string instanceConfigPath)
    {
        var stderr = new StringWriter();
        var originalError = Console.Error;
        Console.SetError(stderr);
        try
        {
            var exitCode = await CodeNetixCommandLine.RunAsync(
                [
                    "serve",
                    $"--CODENETIX:Persistence:DataDirectory={_dataDirectory}",
                    $"--CODENETIX:InstanceConfigPath={instanceConfigPath}",
                ])
                .WaitAsync(TimeSpan.FromSeconds(30), Ct);
            return (exitCode, stderr.ToString());
        }
        finally
        {
            Console.SetError(originalError);
        }
    }

    private string WriteYaml(string content)
    {
        var path = Path.Combine(_root, "config", "codenetix.instance.yml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(TokenVariable, null);
        Environment.SetEnvironmentVariable(ProviderVariable, null);
        foreach (var (name, value) in _originalCodeNetixVariables)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Limpieza de mejor esfuerzo.
        }
    }

    private sealed record HealthBody(string Status, IReadOnlyList<string> Reasons);
}
