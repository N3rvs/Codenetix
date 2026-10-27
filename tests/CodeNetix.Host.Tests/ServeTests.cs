using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.Sqlite;

namespace CodeNetix.Host.Tests;

public sealed class ServeTests : IDisposable
{
    private const string WelcomeMarker = "Bienvenida de prueba";

    private readonly string _root;
    private readonly string _dataDirectory;
    private readonly string _webRoot;

    public ServeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "codenetix-tests-" + Guid.NewGuid().ToString("N"));
        _dataDirectory = Path.Combine(_root, "data"); // no existe: el host debe crearlo
        _webRoot = Path.Combine(_root, "wwwroot");
        Directory.CreateDirectory(_webRoot);
        File.WriteAllText(
            Path.Combine(_webRoot, "index.html"),
            $"<!doctype html><html><body>{WelcomeMarker}</body></html>");
    }

    private string DatabasePath => Path.Combine(_dataDirectory, "codenetix.db");

    [Fact]
    public async Task Health_returns_200_Ready()
    {
        await using var factory = new CodeNetixFactory(_dataDirectory, _webRoot);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/health", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<HealthBody>(TestContext.Current.CancellationToken);
        Assert.Equal("Ready", body?.Status);
    }

    [Fact]
    public async Task Startup_creates_database_in_wal_mode()
    {
        await using (var factory = new CodeNetixFactory(_dataDirectory, _webRoot))
        {
            using var client = factory.CreateClient();
            (await client.GetAsync("/api/health", TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        }

        Assert.True(File.Exists(DatabasePath), $"No existe {DatabasePath}");
        Assert.Equal("wal", ReadJournalMode());
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/runs/x")]
    public async Task Panel_routes_return_index_html(string path)
    {
        await using var factory = new CodeNetixFactory(_dataDirectory, _webRoot);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains(WelcomeMarker, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("/api/nope")]
    [InlineData("/api/runs/x")]
    public async Task Unknown_api_route_returns_404_not_index(string path)
    {
        await using var factory = new CodeNetixFactory(_dataDirectory, _webRoot);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain(WelcomeMarker, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Restart_reuses_existing_database()
    {
        await using (var first = new CodeNetixFactory(_dataDirectory, _webRoot))
        {
            using var client = first.CreateClient();
            (await client.GetAsync("/api/health", TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        }

        WriteMarkerRow();

        await using (var second = new CodeNetixFactory(_dataDirectory, _webRoot))
        {
            using var client = second.CreateClient();
            using var response = await client.GetAsync("/api/health", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Equal(1L, CountMarkerRows());
        Assert.Equal("wal", ReadJournalMode());
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

    private string ReadJournalMode() => (string)Scalar("PRAGMA journal_mode;")!;

    private void WriteMarkerRow()
    {
        Scalar("CREATE TABLE test_marker (id INTEGER); INSERT INTO test_marker VALUES (1);");
    }

    private long CountMarkerRows() => (long)Scalar("SELECT COUNT(*) FROM test_marker;")!;

    private object? Scalar(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={DatabasePath};Mode=ReadWrite;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private sealed record HealthBody(string Status);
}
