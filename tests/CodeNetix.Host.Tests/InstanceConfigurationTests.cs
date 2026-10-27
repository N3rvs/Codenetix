using System.Net;
using System.Net.Http.Json;
using System.Text;
using CodeNetix.Contracts;
using CodeNetix.Host.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace CodeNetix.Host.Tests;

/// <summary>Filas de la matriz de la historia 1.2 que no tocan variables de entorno del proceso.</summary>
public sealed class InstanceConfigurationTests : IDisposable
{
    private const string GitHubToken = "ghp_abcdefghijklmnopqrstuvwxyz012345wxyz";
    private const string AnthropicKey = "sk-ant-api03-ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789abcd";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "codenetix-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _dataDirectory;
    private readonly string _webRoot;

    public InstanceConfigurationTests()
    {
        _dataDirectory = Path.Combine(_root, "data");
        _webRoot = Path.Combine(_root, "wwwroot");
        Directory.CreateDirectory(_webRoot);
        File.WriteAllText(Path.Combine(_webRoot, "index.html"), "<!doctype html><html><body>panel</body></html>");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private CodeNetixFactory CreateFactory(Dictionary<string, string?>? settings = null) =>
        new(_dataDirectory, _webRoot, settings);

    [Fact]
    public async Task Missing_GitHub_token_is_Diagnostic_naming_the_variable()
    {
        await using var factory = CreateFactory(new() { ["CODENETIX:Providers:Anthropic:ApiKey"] = AnthropicKey });
        using var client = factory.CreateClient();

        var body = await GetHealthAsync(client, HttpStatusCode.OK);

        Assert.Equal("Diagnostic", body.Status);
        var reason = Assert.Single(body.Reasons);
        Assert.Contains("CODENETIX__GitHub__Token", reason, StringComparison.Ordinal);
        Assert.DoesNotContain(AnthropicKey, reason, StringComparison.Ordinal);

        var health = factory.Services.GetRequiredService<IInstanceHealth>();
        Assert.Equal(InstanceHealthStatus.Diagnostic, health.Status);
        Assert.False(health.WhenReadyAsync(Ct).IsCompleted);
    }

    [Fact]
    public async Task Missing_both_is_Diagnostic_with_two_reasons_and_never_becomes_ready()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var body = await GetHealthAsync(client, HttpStatusCode.OK);

        Assert.Equal("Diagnostic", body.Status);
        Assert.Equal(2, body.Reasons.Count);
        Assert.Contains(body.Reasons, r => r.Contains("CODENETIX__GitHub__Token", StringComparison.Ordinal));
        Assert.Contains(body.Reasons, r => r.Contains("CODENETIX__Providers__", StringComparison.Ordinal));

        // AC: tras StartupReconciler, Diagnostic y WhenReadyAsync pendiente pasados 500 ms.
        var health = factory.Services.GetRequiredService<IInstanceHealth>();
        Assert.Equal(InstanceHealthStatus.Diagnostic, health.Status);
        var ready = health.WhenReadyAsync(Ct);
        var winner = await Task.WhenAny(ready, Task.Delay(500, Ct));
        Assert.NotSame(ready, winner);
    }

    [Fact]
    public async Task Blank_values_count_as_missing()
    {
        await using var factory = CreateFactory(new()
        {
            ["CODENETIX:GitHub:Token"] = " ",
            ["CODENETIX:Providers:Anthropic:ApiKey"] = "   ",
        });
        using var client = factory.CreateClient();

        var body = await GetHealthAsync(client, HttpStatusCode.OK);

        Assert.Equal("Diagnostic", body.Status);
        Assert.Equal(2, body.Reasons.Count);
    }

    [Fact]
    public async Task Complete_configuration_is_Ready_with_empty_reasons()
    {
        await using var factory = CreateFactory(new()
        {
            ["CODENETIX:GitHub:Token"] = GitHubToken,
            ["CODENETIX:Providers:Anthropic:ApiKey"] = AnthropicKey,
        });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/health", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"status":"Ready","reasons":[]}""", await response.Content.ReadAsStringAsync(Ct));
        await factory.Services.GetRequiredService<IInstanceHealth>().WhenReadyAsync(Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct);
    }

    [Fact]
    public async Task Yaml_only_configuration_is_Ready()
    {
        var yamlPath = WriteYaml($"""
            GitHub:
              Token: {GitHubToken}
            Providers:
              Anthropic:
                ApiKey: {AnthropicKey}
                BaseUrl: https://api.anthropic.com
            """);

        await using var factory = CreateFactory(new() { ["CODENETIX:InstanceConfigPath"] = yamlPath });
        using var client = factory.CreateClient();

        var body = await GetHealthAsync(client, HttpStatusCode.OK);

        Assert.Equal("Ready", body.Status);
        var options = factory.Services.GetRequiredService<IOptions<InstanceOptions>>().Value;
        Assert.Equal("https://api.anthropic.com", options.Providers["Anthropic"].BaseUrl);
    }

    [Fact]
    public async Task Yaml_plain_nulls_count_as_missing()
    {
        var yamlPath = WriteYaml("""
            GitHub:
              Token: ~
            Providers:
              Anthropic:
                ApiKey: null
              OpenAI:
                ApiKey:
            """);

        await using var factory = CreateFactory(new() { ["CODENETIX:InstanceConfigPath"] = yamlPath });
        using var client = factory.CreateClient();

        var body = await GetHealthAsync(client, HttpStatusCode.OK);

        Assert.Equal("Diagnostic", body.Status);
        Assert.Equal(2, body.Reasons.Count);
    }

    private const string NonPatternKey = "local-key-0123456789abcd";

    [Fact]
    public async Task Configured_non_pattern_key_from_settings_is_masked_in_api()
    {
        await using var factory = CreateFactory(new()
        {
            ["CODENETIX:GitHub:Token"] = GitHubToken,
            ["CODENETIX:Providers:Local:ApiKey"] = NonPatternKey,
        });
        using var client = factory.CreateClient();

        await AssertNonPatternKeyMaskedAsync(client);
    }

    [Fact]
    public async Task Configured_non_pattern_key_from_yaml_is_masked_in_api()
    {
        var yamlPath = WriteYaml($"""
            GitHub:
              Token: {GitHubToken}
            Providers:
              Local:
                ApiKey: {NonPatternKey}
            """);
        await using var factory = CreateFactory(new() { ["CODENETIX:InstanceConfigPath"] = yamlPath });
        using var client = factory.CreateClient();

        await AssertNonPatternKeyMaskedAsync(client);
    }

    private static async Task AssertNonPatternKeyMaskedAsync(HttpClient client)
    {
        using var response = await client.GetAsync($"/api/{NonPatternKey}", Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain(NonPatternKey, body, StringComparison.Ordinal);
        Assert.Contains("\"instance\":\"/api/loc…abcd\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Secret_in_api_response_is_masked()
    {
        await using var factory = CreateFactory(new()
        {
            ["CODENETIX:GitHub:Token"] = GitHubToken,
            ["CODENETIX:Providers:Anthropic:ApiKey"] = AnthropicKey,
        });
        using var client = factory.CreateClient();

        // El 404 de /api devuelve la ruta en "instance" (RFC 9457): la key acaba en el cuerpo.
        using var response = await client.GetAsync($"/api/{AnthropicKey}", Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain(AnthropicKey, body, StringComparison.Ordinal);
        Assert.Contains("sk-…abcd", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unconfigured_key_pattern_in_api_response_is_masked()
    {
        var unknownKey = "sk-ant-" + new string('Q', 36) + "zz99";
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/api/{unknownKey}", Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.DoesNotContain(unknownKey, body, StringComparison.Ordinal);
        Assert.Contains("sk-…zz99", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Console_sink_uses_the_redacting_formatter()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var options = factory.Services.GetRequiredService<IOptionsMonitor<ConsoleLoggerOptions>>().CurrentValue;

        Assert.Equal(Secrets.RedactingConsoleFormatter.FormatterName, options.FormatterName);
        Assert.Contains(
            factory.Services.GetServices<ConsoleFormatter>(),
            f => f is Secrets.RedactingConsoleFormatter);
    }

    [Fact]
    public async Task Secrets_are_never_written_to_the_database_files()
    {
        await using (var factory = CreateFactory(new()
        {
            ["CODENETIX:GitHub:Token"] = GitHubToken,
            ["CODENETIX:Providers:Anthropic:ApiKey"] = AnthropicKey,
        }))
        {
            using var client = factory.CreateClient();
            (await client.GetAsync("/api/health", Ct)).EnsureSuccessStatusCode();

            // Con el host aún vivo, -wal y -shm existen.
            AssertFilesDoNotContainSecrets();
        }

        SqliteConnection.ClearAllPools();
        AssertFilesDoNotContainSecrets();
    }

    private void AssertFilesDoNotContainSecrets()
    {
        var files = new[] { "codenetix.db", "codenetix.db-wal", "codenetix.db-shm" }
            .Select(name => Path.Combine(_dataDirectory, name))
            .Where(File.Exists)
            .ToList();
        Assert.Contains(files, f => f.EndsWith("codenetix.db", StringComparison.Ordinal));

        foreach (var file in files)
        {
            byte[] bytes;
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var memory = new MemoryStream())
            {
                stream.CopyTo(memory);
                bytes = memory.ToArray();
            }

            foreach (var secret in new[] { GitHubToken, AnthropicKey })
            {
                Assert.True(IndexOf(bytes, Encoding.UTF8.GetBytes(secret)) < 0, $"{Path.GetFileName(file)} contiene un secreto (UTF-8).");
                Assert.True(IndexOf(bytes, Encoding.Unicode.GetBytes(secret)) < 0, $"{Path.GetFileName(file)} contiene un secreto (UTF-16).");
            }
        }
    }

    private static int IndexOf(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle);

    private string WriteYaml(string content)
    {
        var path = Path.Combine(_root, "config", "codenetix.instance.yml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static async Task<HealthBody> GetHealthAsync(HttpClient client, HttpStatusCode expected)
    {
        using var response = await client.GetAsync("/api/health", Ct);
        Assert.Equal(expected, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<HealthBody>(Ct))!;
    }

    public void Dispose()
    {
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
