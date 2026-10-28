using System.Net;
using System.Net.Http.Json;
using CodeNetix.Host.Features.SubmitManualEvent;
using CodeNetix.Ingestion.Features.SubmitManualEvent;
using CodeNetix.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace CodeNetix.Host.Tests;

/// <summary>Matriz de la historia 1.5: autorización del disparador contra el host y una API de GitHub falsa.</summary>
public sealed class TriggerAuthorizationTests : IAsyncLifetime
{
    private const string Route = SubmitManualEventEndpoint.Route;
    private const string IssueUrl = "https://github.com/O/R/issues/12";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "codenetix-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _dataDirectory;
    private readonly string _webRoot;
    private readonly StringWriter _stdout = new();
    private readonly StringWriter _stderr = new();
    private FakeGitHub _gitHub = null!;

    public TriggerAuthorizationTests()
    {
        _dataDirectory = Path.Combine(_root, "data");
        _webRoot = Path.Combine(_root, "wwwroot");
        Directory.CreateDirectory(_webRoot);
        File.WriteAllText(Path.Combine(_webRoot, "index.html"), "<!doctype html><html><body>Panel</body></html>");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string DatabasePath => Path.Combine(_dataDirectory, PersistenceOptions.DatabaseFileName);

    public async ValueTask InitializeAsync() => _gitHub = await FakeGitHub.StartAsync(Ct);

    private CodeNetixFactory CreateFactory() =>
        new(_dataDirectory, _webRoot, new Dictionary<string, string?>(CodeNetixFactory.CompleteSettings), gitHub: _gitHub);

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string requestId) =>
        client.PostAsJsonAsync(Route, new { url = IssueUrl, requestId }, Ct);

    // --- Matriz ------------------------------------------------------------------

    [Fact]
    public async Task With_write_permission_the_event_is_Accepted_with_the_token_login_as_actor()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateAuthorizedClient();

        using var response = await PostAsync(client, Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<SubmitBody>(Ct);
        Assert.Equal("Accepted", body!.Status);
        Assert.Null(body.Reason);
        Assert.Equal("bot", body.Actor);
        Assert.Equal("o/r", body.Repository);

        var row = Assert.Single(await ReadEventsAsync());
        Assert.Equal(("bot", "Accepted", (string?)null), (row.Actor, row.Status, row.IgnoredReason));

        // El módulo GitHub pregunta por el login del token sobre el repositorio canónico, con las cabeceras de la API.
        Assert.Contains(_gitHub.Requests, r => r.Kind == "permission:o/r:bot");
        Assert.All(_gitHub.Requests, r =>
        {
            Assert.Equal("Bearer " + CodeNetixFactory.CompleteSettings["CODENETIX:GitHub:Token"], r.Authorization);
            Assert.Equal("application/vnd.github+json", r.Accept);
            Assert.Equal("2022-11-28", r.ApiVersion);
            Assert.False(string.IsNullOrEmpty(r.UserAgent));
        });
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("maintain")]
    [InlineData("write")]
    public async Task Admin_maintain_and_write_are_Accepted(string permission)
    {
        _gitHub.Permission = permission;
        await using var factory = CreateFactory();
        using var client = factory.CreateAuthorizedClient();

        using var response = await PostAsync(client, Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("Accepted", (await response.Content.ReadFromJsonAsync<SubmitBody>(Ct))!.Status);
        Assert.Equal("Accepted", Assert.Single(await ReadEventsAsync()).Status);
    }

    [Theory]
    [InlineData("read")]
    [InlineData("triage")]
    [InlineData("none")]
    public async Task Without_write_permission_the_event_is_Ignored_as_not_authorized(string permission)
    {
        _gitHub.Permission = permission;
        await using var factory = CreateFactory();
        using var client = factory.CreateAuthorizedClient();

        using var response = await PostAsync(client, Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<SubmitBody>(Ct);
        Assert.Equal("Ignored", body!.Status);
        Assert.Equal(ManualEventIngestion.UnauthorizedReason, body.Reason);

        var row = Assert.Single(await ReadEventsAsync());
        Assert.Equal(("bot", "Ignored", "no autorizado"), (row.Actor, row.Status, row.IgnoredReason));
    }

    [Fact]
    public async Task Repository_invisible_to_the_token_404_is_Ignored()
    {
        _gitHub.PermissionStatus = 404;
        await using var factory = CreateFactory();
        using var client = factory.CreateAuthorizedClient();

        using var response = await PostAsync(client, Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("Ignored", (await response.Content.ReadFromJsonAsync<SubmitBody>(Ct))!.Status);
        var row = Assert.Single(await ReadEventsAsync());
        Assert.Equal(("Ignored", "no autorizado"), (row.Status, row.IgnoredReason));
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task Invalid_token_is_502_with_the_token_reason_and_persists_nothing(int status)
    {
        _gitHub.UserStatus = status;
        await using var factory = CreateFactory();
        using var client = factory.CreateAuthorizedClient();

        using var response = await PostAsync(client, Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<ProblemBody>(Ct);
        Assert.Contains("el token de GitHub no es válido o no tiene permisos", problem!.Detail, StringComparison.Ordinal);
        Assert.Empty(await ReadEventsAsync());
        Assert.Equal(0, _gitHub.PermissionCalls);
    }

    [Fact]
    public async Task Invalid_token_on_the_permission_call_is_also_502()
    {
        _gitHub.PermissionStatus = 403;
        await using var factory = CreateFactory();
        using var client = factory.CreateAuthorizedClient();

        using var response = await PostAsync(client, Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Empty(await ReadEventsAsync());
    }

    [Theory]
    [InlineData(500, false)]
    [InlineData(503, false)]
    [InlineData(500, true)]
    public async Task GitHub_down_is_502_GitHub_no_responde_and_persists_nothing(int status, bool onPermission)
    {
        if (onPermission)
        {
            _gitHub.PermissionStatus = status;
        }
        else
        {
            _gitHub.UserStatus = status;
        }

        await using var factory = CreateFactory();
        using var client = factory.CreateAuthorizedClient();

        using var response = await PostAsync(client, Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemBody>(Ct);
        Assert.Contains("GitHub no responde", problem!.Detail, StringComparison.Ordinal);
        Assert.Empty(await ReadEventsAsync());
    }

    [Fact]
    public async Task GitHub_unreachable_is_502_GitHub_no_responde_and_persists_nothing()
    {
        // Puerto cerrado: conexión rechazada.
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var settings = new Dictionary<string, string?>(CodeNetixFactory.CompleteSettings)
        {
            [CodeNetixFactory.GitHubApiUrlKey] = $"http://127.0.0.1:{port}",
        };
        await using var factory = new CodeNetixFactory(_dataDirectory, _webRoot, settings);
        using var client = factory.CreateAuthorizedClient();

        using var response = await PostAsync(client, Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemBody>(Ct);
        Assert.Contains("GitHub no responde", problem!.Detail, StringComparison.Ordinal);
        Assert.Empty(await ReadEventsAsync());
    }

    [Fact]
    public async Task Retry_with_the_same_requestId_returns_the_existing_event_without_calling_GitHub()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateAuthorizedClient();
        var requestId = Guid.NewGuid().ToString();

        using var first = await PostAsync(client, requestId);
        var callsAfterFirst = _gitHub.TotalCalls;

        // Aunque GitHub caiga después, el reintento devuelve el Evento ya persistido.
        _gitHub.UserStatus = 500;
        _gitHub.PermissionStatus = 500;
        using var second = await PostAsync(client, requestId);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var a = await first.Content.ReadFromJsonAsync<SubmitBody>(Ct);
        var b = await second.Content.ReadFromJsonAsync<SubmitBody>(Ct);
        Assert.Equal(a!.EventId, b!.EventId);
        Assert.Equal("Accepted", b.Status);
        Assert.Equal(callsAfterFirst, _gitHub.TotalCalls);
        Assert.Single(await ReadEventsAsync());
    }

    [Fact]
    public async Task Retry_of_an_Ignored_event_returns_200_Ignored()
    {
        _gitHub.Permission = "read";
        await using var factory = CreateFactory();
        using var client = factory.CreateAuthorizedClient();
        var requestId = Guid.NewGuid().ToString();

        using var first = await PostAsync(client, requestId);
        using var second = await PostAsync(client, requestId);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var body = await second.Content.ReadFromJsonAsync<SubmitBody>(Ct);
        Assert.Equal(("Ignored", "no autorizado", "bot"), (body!.Status, body.Reason, body.Actor));
    }

    [Fact]
    public async Task Identity_is_cached_across_events()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateAuthorizedClient();

        using var first = await PostAsync(client, Guid.NewGuid().ToString());
        using var second = await PostAsync(client, Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(1, _gitHub.UserCalls);
        Assert.Equal(2, _gitHub.PermissionCalls);
        Assert.Equal(2, (await ReadEventsAsync()).Count);
    }

    [Fact]
    public async Task Failed_identity_is_not_cached()
    {
        _gitHub.UserStatus = 500;
        await using var factory = CreateFactory();
        using var client = factory.CreateAuthorizedClient();

        using var first = await PostAsync(client, Guid.NewGuid().ToString());
        _gitHub.UserStatus = 200;
        using var second = await PostAsync(client, Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.BadGateway, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(2, _gitHub.UserCalls);
    }

    [Fact]
    public async Task GitHub_token_never_appears_in_the_response()
    {
        _gitHub.UserStatus = 401;
        await using var factory = CreateFactory();
        using var client = factory.CreateAuthorizedClient();

        using var response = await PostAsync(client, Guid.NewGuid().ToString());

        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain(CodeNetixFactory.CompleteSettings["CODENETIX:GitHub:Token"]!, text, StringComparison.Ordinal);
    }

    // --- codenetix fix contra el host -------------------------------------------------

    [Fact]
    public async Task Fix_without_permission_prints_ignored_and_exits_1()
    {
        _gitHub.Permission = "read";
        await using var factory = CreateFactory();
        using var handler = factory.Server.CreateHandler();

        var exitCode = await FixMode.RunAsync([IssueUrl], FixConfig(factory), _stdout, _stderr, handler, [TimeSpan.Zero], Ct);

        Assert.Equal(CodeNetixCommandLine.ExitFailure, exitCode);
        var row = Assert.Single(await ReadEventsAsync());
        Assert.Equal(
            $"Evento {Guid.Parse(row.Id)} ignorado: no autorizado (bot no tiene permiso de escritura en o/r).",
            _stderr.ToString().Trim());
        Assert.Empty(_stdout.ToString());
    }

    [Fact]
    public async Task Fix_with_permission_prints_registered_and_exits_0()
    {
        await using var factory = CreateFactory();
        using var handler = factory.Server.CreateHandler();

        var exitCode = await FixMode.RunAsync([IssueUrl], FixConfig(factory), _stdout, _stderr, handler, [TimeSpan.Zero], Ct);

        Assert.Equal(CodeNetixCommandLine.ExitOk, exitCode);
        var row = Assert.Single(await ReadEventsAsync());
        Assert.Equal($"Evento {Guid.Parse(row.Id)} registrado.", _stdout.ToString().Trim());
    }

    [Fact]
    public async Task Fix_with_invalid_token_retries_and_exits_1_with_the_detail()
    {
        _gitHub.UserStatus = 401;
        await using var factory = CreateFactory();
        using var handler = factory.Server.CreateHandler();

        var exitCode = await FixMode.RunAsync([IssueUrl], FixConfig(factory), _stdout, _stderr, handler, [TimeSpan.Zero], Ct);

        Assert.Equal(CodeNetixCommandLine.ExitFailure, exitCode);
        Assert.Equal(1 + FixMode.MaxRetries, _gitHub.UserCalls); // un intento por petición: 502 se reintenta
        Assert.Contains("el token de GitHub no es válido o no tiene permisos", _stderr.ToString(), StringComparison.Ordinal);
        Assert.Empty(await ReadEventsAsync());
    }

    private static IConfiguration FixConfig(CodeNetixFactory factory) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CODENETIX:Host:Url"] = factory.Server.BaseAddress.ToString(),
            ["CODENETIX:Host:AccessToken"] = CodeNetixFactory.TestAccessToken,
        })
        .Build();

    // --- Utilidades ----------------------------------------------------------------

    private async Task<List<EventRow>> ReadEventsAsync()
    {
        if (!File.Exists(DatabasePath))
        {
            return [];
        }

        await using var connection = new SqliteConnection($"Data Source={DatabasePath};Mode=ReadOnly");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, actor, status, ignored_reason FROM ing_events ORDER BY id;";
        var rows = new List<EventRow>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(new EventRow(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return rows;
    }

    public async ValueTask DisposeAsync()
    {
        await _gitHub.DisposeAsync();
        _stdout.Dispose();
        _stderr.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // Limpieza de mejor esfuerzo.
        }
    }

    private sealed record SubmitBody(Guid EventId, bool Created, string? Status, string? Reason, string? Actor, string? Repository);

    private sealed record ProblemBody(int Status, string? Title, string? Detail);

    private sealed record EventRow(string Id, string? Actor, string Status, string? IgnoredReason);
}
