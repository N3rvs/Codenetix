using System.Net;
using CodeNetix.Host.Configuration;
using CodeNetix.Host.Features.GetHealth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CodeNetix.Host.Tests;

public sealed class InstanceConfigFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codenetix-tests-" + Guid.NewGuid().ToString("N"));

    public InstanceConfigFileTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData(null, YamlInstanceConfigurationExtensions.DefaultPath)]
    [InlineData("", YamlInstanceConfigurationExtensions.DefaultPath)]
    [InlineData("   ", YamlInstanceConfigurationExtensions.DefaultPath)]
    [InlineData("/etc/otro.yml", "/etc/otro.yml")]
    public void ResolveInstanceConfigPath_defaults_unless_overridden(string? configured, string expected)
    {
        var data = new Dictionary<string, string?>();
        if (configured is not null)
        {
            data[YamlInstanceConfigurationExtensions.PathKey] = configured;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(data).Build();

        Assert.Equal("/config/codenetix.instance.yml", YamlInstanceConfigurationExtensions.DefaultPath);
        Assert.Equal(expected, configuration.ResolveInstanceConfigPath());
    }

    [Fact]
    public void Plain_nulls_are_null_and_quoted_null_is_text()
    {
        var path = Write("""
            A: ~
            B: null
            C: NULL
            D:
            E: "null"
            F: 'NULL'
            G: valor
            """);

        var provider = Load(path, optional: false);

        foreach (var key in new[] { "A", "B", "C", "D" })
        {
            Assert.True(provider.TryGet($"CODENETIX:{key}", out var value));
            Assert.Null(value);
        }

        Assert.True(provider.TryGet("CODENETIX:E", out var e));
        Assert.Equal("null", e);
        Assert.True(provider.TryGet("CODENETIX:F", out var f));
        Assert.Equal("NULL", f);
        Assert.True(provider.TryGet("CODENETIX:G", out var g));
        Assert.Equal("valor", g);
    }

    [Fact]
    public void Optional_missing_file_contributes_nothing()
    {
        var provider = Load(Path.Combine(_root, "no-existe.yml"), optional: true);

        Assert.Empty(provider.GetChildKeys([], null));
    }

    [Fact]
    public void Required_missing_file_throws_naming_the_path()
    {
        var path = Path.Combine(_root, "no-existe.yml");

        var ex = Assert.Throws<InstanceConfigurationException>(() => Load(path, optional: false));

        Assert.Equal(path, ex.ConfigPath);
        Assert.Contains(path, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Required_path_that_is_a_directory_throws()
    {
        var ex = Assert.Throws<InstanceConfigurationException>(() => Load(_root, optional: false));

        Assert.Contains(_root, ex.Message, StringComparison.Ordinal);
        Assert.Contains("directorio", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void File_is_inserted_before_the_unprefixed_environment_source()
    {
        var builder = new ConfigurationBuilder()
            .AddEnvironmentVariables("DOTNET_")
            .AddInMemoryCollection() // equivale a appsettings
            .AddEnvironmentVariables();

        builder.AddInstanceConfigFile(Path.Combine(_root, "x.yml"), optional: true);

        Assert.IsType<YamlInstanceConfigurationSource>(builder.Sources[2]);
        Assert.Equal(4, builder.Sources.Count);
    }

    [Fact]
    public async Task Health_is_503_Starting_before_the_reconciler_publishes()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<InstanceHealth>();
        await using var app = builder.Build();
        app.MapGetHealth();
        await app.StartAsync(TestContext.Current.CancellationToken);
        using var client = app.GetTestClient();

        using var response = await client.GetAsync(GetHealthEndpoint.Route, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(
            """{"status":"Starting","reasons":[]}""",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private static YamlInstanceConfigurationProvider Load(string path, bool optional)
    {
        var provider = new YamlInstanceConfigurationProvider(path, optional);
        provider.Load();
        return provider;
    }

    private string Write(string content)
    {
        var path = Path.Combine(_root, "codenetix.instance.yml");
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Limpieza de mejor esfuerzo.
        }
    }
}
