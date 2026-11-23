using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using CodeNetix.Host.Access;
using CodeNetix.Host.Features.SubmitManualEvent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CodeNetix.Host.Tests;

/// <summary><c>codenetix fix</c> contra un servidor local y contra el host real (historia 1.4).</summary>
public sealed class FixModeTests : IDisposable
{
    private const string IssueUrl = "https://github.com/O/R/issues/12";
    private const string Token = "fix-test-token-0123456789";
    private static readonly TimeSpan[] NoDelay = [TimeSpan.Zero];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "codenetix-tests-" + Guid.NewGuid().ToString("N"));
    private readonly StringWriter _stdout = new();
    private readonly StringWriter _stderr = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private Task<int> RunAsync(string[] args, IConfiguration configuration, HttpMessageHandler? handler = null) =>
        FixMode.RunAsync(args, configuration, _stdout, _stderr, handler, NoDelay, Ct);

    // --- Contra un servidor local -----------------------------------------------------

    [Fact]
    public async Task Success_prints_the_event_id_and_exits_0()
    {
        var eventId = Guid.CreateVersion7();
        await using var server = await FakeApi.StartAsync(_ => Respond(201, new { eventId, created = true, status = "Accepted" }));

        var exitCode = await RunAsync([IssueUrl], server.Config());

        Assert.Equal(CodeNetixCommandLine.ExitOk, exitCode);
        Assert.Equal($"Evento {eventId} registrado.", _stdout.ToString().Trim());
        var request = Assert.Single(server.Requests);
        Assert.Equal($"Bearer {Token}", request.Authorization);
        Assert.Equal(IssueUrl, request.Url);
        Assert.True(Guid.TryParse(request.RequestId, out _));
    }

    [Fact]
    public async Task Existing_event_200_is_also_success()
    {
        var eventId = Guid.CreateVersion7();
        await using var server = await FakeApi.StartAsync(_ => Respond(200, new { eventId, created = false, status = "Accepted" }));

        var exitCode = await RunAsync([IssueUrl], server.Config());

        Assert.Equal(CodeNetixCommandLine.ExitOk, exitCode);
        Assert.Contains(eventId.ToString(), _stdout.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(201)]
    [InlineData(200)]
    public async Task Ignored_event_prints_not_authorized_and_exits_1(int status)
    {
        var eventId = Guid.CreateVersion7();
        await using var server = await FakeApi.StartAsync(_ => Respond(status, new
        {
            eventId,
            created = status == 201,
            status = "Ignored",
            reason = "no autorizado",
            actor = "bot",
            repository = "o/r",
        }));

        var exitCode = await RunAsync([IssueUrl], server.Config());

        Assert.Equal(CodeNetixCommandLine.ExitFailure, exitCode);
        Assert.Equal($"Evento {eventId} ignorado: no autorizado (bot no tiene permiso de escritura en o/r).", _stderr.ToString().Trim());
        Assert.Empty(_stdout.ToString());
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task Accepted_status_prints_registered()
    {
        var eventId = Guid.CreateVersion7();
        await using var server = await FakeApi.StartAsync(_ => Respond(201, new { eventId, created = true, status = "Accepted", actor = "bot", repository = "o/r" }));

        var exitCode = await RunAsync([IssueUrl], server.Config());

        Assert.Equal(CodeNetixCommandLine.ExitOk, exitCode);
        Assert.Equal($"Evento {eventId} registrado.", _stdout.ToString().Trim());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Pending")]
    [InlineData("accepted")]
    public async Task Missing_or_unknown_status_is_an_unexpected_response(string? status)
    {
        var eventId = Guid.CreateVersion7();
        await using var server = await FakeApi.StartAsync(_ => Respond(201, new { eventId, created = true, status }));

        var exitCode = await RunAsync([IssueUrl], server.Config());

        Assert.Equal(CodeNetixCommandLine.ExitFailure, exitCode);
        Assert.Equal("Respuesta inesperada de CodeNetix (HTTP 201).", _stderr.ToString().Trim());
        Assert.Empty(_stdout.ToString());
    }

    [Fact]
    public async Task Bad_gateway_502_is_retried_and_then_prints_the_detail()
    {
        await using var server = await FakeApi.StartAsync(_ => Problem(502, "No se puede comprobar el permiso en GitHub: GitHub no responde."));

        var exitCode = await RunAsync([IssueUrl], server.Config());

        Assert.Equal(CodeNetixCommandLine.ExitFailure, exitCode);
        Assert.Equal(1 + FixMode.MaxRetries, server.Requests.Count);
        Assert.Single(server.Requests.Select(r => r.RequestId).Distinct());
        Assert.Equal("No se puede comprobar el permiso en GitHub: GitHub no responde.", _stderr.ToString().Trim());
    }

    [Theory]
    [InlineData(400, "URL no admitida. Prueba con una issue.")]
    [InlineData(501, "Soporte de Sentry disponible próximamente.")]
    [InlineData(503, "CodeNetix está en modo diagnóstico:\n- Falta CODENETIX__GitHub__Token.")]
    [InlineData(401, "Se requiere el admin-token.")]
    public async Task Api_error_prints_detail_exits_1_and_is_not_retried(int status, string detail)
    {
        await using var server = await FakeApi.StartAsync(_ => Problem(status, detail));

        var exitCode = await RunAsync([IssueUrl], server.Config());

        Assert.Equal(CodeNetixCommandLine.ExitFailure, exitCode);
        Assert.Equal(detail, _stderr.ToString().TrimEnd().ReplaceLineEndings("\n"));
        Assert.Empty(_stdout.ToString());
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task Transient_5xx_is_retried_with_the_same_requestId()
    {
        var eventId = Guid.CreateVersion7();
        await using var server = await FakeApi.StartAsync(n => n <= 2
            ? Problem(n == 1 ? 500 : 502, "fallo transitorio")
            : Respond(201, new { eventId, created = true, status = "Accepted" }));

        var exitCode = await RunAsync([IssueUrl], server.Config());

        Assert.Equal(CodeNetixCommandLine.ExitOk, exitCode);
        Assert.Equal(3, server.Requests.Count);
        Assert.Single(server.Requests.Select(r => r.RequestId).Distinct());
        Assert.Contains(eventId.ToString(), _stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Starting_503_is_retried_with_the_same_requestId()
    {
        var eventId = Guid.CreateVersion7();
        await using var server = await FakeApi.StartAsync(n => n == 1
            ? Problem(503, "CodeNetix está arrancando.", "Starting")
            : Respond(201, new { eventId, created = true, status = "Accepted" }));

        var exitCode = await RunAsync([IssueUrl], server.Config());

        Assert.Equal(CodeNetixCommandLine.ExitOk, exitCode);
        Assert.Equal(2, server.Requests.Count);
        Assert.Single(server.Requests.Select(r => r.RequestId).Distinct());
        Assert.Contains(eventId.ToString(), _stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diagnostic_503_is_final()
    {
        await using var server = await FakeApi.StartAsync(_ => Problem(503, "Modo diagnóstico.", "Diagnostic"));

        var exitCode = await RunAsync([IssueUrl], server.Config());

        Assert.Equal(CodeNetixCommandLine.ExitFailure, exitCode);
        Assert.Single(server.Requests);
        Assert.Equal("Modo diagnóstico.", _stderr.ToString().Trim());
    }

    [Fact]
    public async Task Starting_503_gives_up_after_three_retries()
    {
        await using var server = await FakeApi.StartAsync(_ => Problem(503, "CodeNetix está arrancando.", "Starting"));

        var exitCode = await RunAsync([IssueUrl], server.Config());

        Assert.Equal(CodeNetixCommandLine.ExitFailure, exitCode);
        Assert.Equal(1 + FixMode.MaxRetries, server.Requests.Count);
        Assert.Equal("CodeNetix está arrancando.", _stderr.ToString().Trim());
    }

    [Fact]
    public async Task Network_errors_are_retried_with_the_same_requestId()
    {
        var eventId = Guid.CreateVersion7();
        using var handler = new FlakyHandler(failures: 2, eventId);

        var exitCode = await RunAsync([IssueUrl], Config(new()
        {
            ["CODENETIX:Host:Url"] = "http://codenetix.test",
            ["CODENETIX:Host:AccessToken"] = Token,
        }), handler);

        Assert.Equal(CodeNetixCommandLine.ExitOk, exitCode);
        Assert.Equal(3, handler.RequestIds.Count);
        Assert.Single(handler.RequestIds.Distinct());
        Assert.Contains(eventId.ToString(), _stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Gives_up_after_three_retries()
    {
        await using var server = await FakeApi.StartAsync(_ => Problem(500, "sigue fallando"));

        var exitCode = await RunAsync([IssueUrl], server.Config());

        Assert.Equal(CodeNetixCommandLine.ExitFailure, exitCode);
        Assert.Equal(1 + FixMode.MaxRetries, server.Requests.Count);
        Assert.Single(server.Requests.Select(r => r.RequestId).Distinct());
        Assert.Contains("sigue fallando", _stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Network_error_is_retried_and_then_exits_1()
    {
        // Puerto cerrado: conexión rechazada en cada intento.
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var exitCode = await RunAsync([IssueUrl], Config(new()
        {
            ["CODENETIX:Host:Url"] = $"http://127.0.0.1:{port}",
            ["CODENETIX:Host:AccessToken"] = Token,
        }));

        Assert.Equal(CodeNetixCommandLine.ExitFailure, exitCode);
        Assert.Contains("No se puede contactar con CodeNetix", _stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Token_is_read_from_admin_token_file_without_AccessToken()
    {
        var dataDirectory = Path.Combine(_root, "data");
        Directory.CreateDirectory(dataDirectory);
        var tokenPath = Path.Combine(dataDirectory, AdminTokenStore.FileName);
        await File.WriteAllTextAsync(tokenPath, "cnx_from-file\n", Ct);
        const UnixFileMode Mode0644 = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(tokenPath, Mode0644);
        }

        await using var server = await FakeApi.StartAsync(_ => Respond(201, new { eventId = Guid.CreateVersion7(), created = true, status = "Accepted" }));

        var exitCode = await RunAsync([IssueUrl], Config(new()
        {
            ["CODENETIX:Host:Url"] = server.BaseUrl,
            ["CODENETIX:Persistence:DataDirectory"] = dataDirectory,
        }));

        Assert.Equal(CodeNetixCommandLine.ExitOk, exitCode);
        Assert.Equal("Bearer cnx_from-file", Assert.Single(server.Requests).Authorization);
        if (!OperatingSystem.IsWindows())
        {
            // El cliente es de solo lectura: no corrige permisos (eso lo hace serve).
            Assert.Equal(Mode0644, File.GetUnixFileMode(tokenPath));
        }
    }

    [Fact]
    public async Task AccessToken_wins_over_admin_token_file()
    {
        var dataDirectory = Path.Combine(_root, "data");
        Directory.CreateDirectory(dataDirectory);
        await File.WriteAllTextAsync(Path.Combine(dataDirectory, AdminTokenStore.FileName), "cnx_from-file", Ct);
        await using var server = await FakeApi.StartAsync(_ => Respond(201, new { eventId = Guid.CreateVersion7(), created = true, status = "Accepted" }));

        var exitCode = await RunAsync([IssueUrl], Config(new()
        {
            ["CODENETIX:Host:Url"] = server.BaseUrl,
            ["CODENETIX:Host:AccessToken"] = Token,
            ["CODENETIX:Persistence:DataDirectory"] = dataDirectory,
        }));

        Assert.Equal(CodeNetixCommandLine.ExitOk, exitCode);
        Assert.Equal($"Bearer {Token}", Assert.Single(server.Requests).Authorization);
    }

    [Fact]
    public async Task Missing_token_exits_1_without_creating_the_file()
    {
        var dataDirectory = Path.Combine(_root, "empty");
        await using var server = await FakeApi.StartAsync(_ => Respond(201, new { eventId = Guid.CreateVersion7(), created = true, status = "Accepted" }));

        var exitCode = await RunAsync([IssueUrl], Config(new()
        {
            ["CODENETIX:Host:Url"] = server.BaseUrl,
            ["CODENETIX:Persistence:DataDirectory"] = dataDirectory,
        }));

        Assert.Equal(CodeNetixCommandLine.ExitFailure, exitCode);
        Assert.Contains("admin-token", _stderr.ToString(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(dataDirectory));
        Assert.Empty(server.Requests);
    }

    public static TheoryData<string[]> UsageCases => new()
    {
        Array.Empty<string>(),
        new[] { "" },
        new[] { "   " },
        new[] { "https://github.com/o/r/issues/1", "extra" },
    };

    [Theory]
    [MemberData(nameof(UsageCases))]
    public async Task Without_url_prints_usage_and_exits_2(string[] args)
    {
        var exitCode = await RunAsync(args, Config(new()));

        Assert.Equal(CodeNetixCommandLine.ExitUsage, exitCode);
        Assert.Contains("codenetix fix <url-de-issue>", _stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Command_line_dispatches_fix_without_url_to_usage()
    {
        Assert.Equal(CodeNetixCommandLine.ExitUsage, await CodeNetixCommandLine.RunAsync(["fix"]));
        Assert.Contains("fix <url>", CodeNetixCommandLine.Usage, StringComparison.Ordinal);
    }

    // --- Contra el host real -----------------------------------------------------------

    [Fact]
    public async Task End_to_end_registers_the_event_in_under_two_seconds()
    {
        var dataDirectory = Path.Combine(_root, "host-data");
        var webRoot = Path.Combine(_root, "wwwroot");
        Directory.CreateDirectory(webRoot);
        await using var factory = new CodeNetixFactory(dataDirectory, webRoot, new Dictionary<string, string?>(CodeNetixFactory.CompleteSettings));
        using var handler = factory.Server.CreateHandler();
        var configuration = Config(new()
        {
            ["CODENETIX:Host:Url"] = factory.Server.BaseAddress.ToString(),
            ["CODENETIX:Host:AccessToken"] = CodeNetixFactory.TestAccessToken,
        });

        var stopwatch = Stopwatch.StartNew();
        var exitCode = await RunAsync([IssueUrl], configuration, handler);
        stopwatch.Stop();

        Assert.Equal(CodeNetixCommandLine.ExitOk, exitCode);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"Tardó {stopwatch.Elapsed}.");
        var match = System.Text.RegularExpressions.Regex.Match(_stdout.ToString(), "^Evento ([0-9a-f-]{36}) registrado\\.");
        Assert.True(match.Success, _stdout.ToString());

        await using var connection = new SqliteConnection($"Data Source={Path.Combine(dataDirectory, "codenetix.db")};Mode=ReadOnly");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, object_key FROM ing_events;";
        await using var reader = await command.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));
        Assert.Equal(Guid.Parse(match.Groups[1].Value), Guid.Parse(reader.GetString(0)));
        Assert.Equal("github:o/r#12", reader.GetString(1));
        Assert.False(await reader.ReadAsync(Ct));
    }

    [Fact]
    public async Task End_to_end_sentry_without_Sentry_configured_prints_the_reason_and_exits_1()
    {
        var dataDirectory = Path.Combine(_root, "host-data");
        var webRoot = Path.Combine(_root, "wwwroot");
        Directory.CreateDirectory(webRoot);
        await using var factory = new CodeNetixFactory(dataDirectory, webRoot, new Dictionary<string, string?>(CodeNetixFactory.CompleteSettings));
        using var handler = factory.Server.CreateHandler();

        var exitCode = await RunAsync(["https://acme.sentry.io/issues/123/"], Config(new()
        {
            ["CODENETIX:Host:Url"] = factory.Server.BaseAddress.ToString(),
            ["CODENETIX:Host:AccessToken"] = CodeNetixFactory.TestAccessToken,
        }), handler);

        Assert.Equal(CodeNetixCommandLine.ExitFailure, exitCode);
        Assert.Equal(CodeNetix.Ingestion.Features.ReadSentryContext.SentryApiOptions.NotConfiguredReason, _stderr.ToString().Trim());
    }

    // --- Utilidades --------------------------------------------------------------------

    private static IResult Respond(int status, object body) => Results.Json(body, statusCode: status);

    private static IResult Problem(int status, string detail, string? instanceStatus = null) => Results.Problem(
        statusCode: status,
        detail: detail,
        extensions: instanceStatus is null ? null : new Dictionary<string, object?> { ["instanceStatus"] = instanceStatus });

    /// <summary>Falla con <see cref="HttpRequestException"/> las primeras <c>failures</c> llamadas y después responde 201.</summary>
    private sealed class FlakyHandler(int failures, Guid eventId) : HttpMessageHandler
    {
        public List<string?> RequestIds { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            RequestIds.Add(body.RootElement.GetProperty("requestId").GetString());
            if (RequestIds.Count <= failures)
            {
                throw new HttpRequestException("conexión rechazada (simulada)");
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.Created)
            {
                Content = System.Net.Http.Json.JsonContent.Create(new { eventId, created = true, status = "Accepted" }),
            };
        }
    }

    public void Dispose()
    {
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

    internal sealed record RecordedRequest(string? Authorization, string? Url, string? RequestId);

    /// <summary>API falsa en Kestrel (127.0.0.1, puerto libre) que registra cada petición a <c>/api/events/manual</c>.</summary>
    private sealed class FakeApi : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly ConcurrentQueue<RecordedRequest> _requests = new();

        private FakeApi(Func<int, IResult> respond)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Services.AddProblemDetails();
            _app = builder.Build();
            _app.MapPost(SubmitManualEventEndpoint.Route, async (HttpContext context) =>
            {
                using var body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
                _requests.Enqueue(new RecordedRequest(
                    context.Request.Headers.Authorization.ToString(),
                    body.RootElement.TryGetProperty("url", out var url) ? url.GetString() : null,
                    body.RootElement.TryGetProperty("requestId", out var id) ? id.GetString() : null));
                return respond(_requests.Count);
            });
        }

        public IReadOnlyList<RecordedRequest> Requests => [.. _requests];

        public string BaseUrl => _app.Urls.First();

        public IConfiguration Config() => FixModeTests.Config(new()
        {
            ["CODENETIX:Host:Url"] = BaseUrl,
            ["CODENETIX:Host:AccessToken"] = Token,
        });

        public static async Task<FakeApi> StartAsync(Func<int, IResult> respond)
        {
            var api = new FakeApi(respond);
            await api._app.StartAsync(Ct);
            return api;
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync(CancellationToken.None);
            await _app.DisposeAsync();
        }
    }
}

/// <summary>
/// <c>codenetix fix</c> con la configuración real (entorno del proceso + YAML de instancia): modifica
/// variables de entorno, colección no paralela.
/// </summary>
[Collection(nameof(ConsoleCollection))]
public sealed class FixModeEnvironmentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codenetix-tests-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string?> _originalCodeNetixVariables = new(StringComparer.Ordinal);
    private readonly StringWriter _stdout = new();
    private readonly StringWriter _stderr = new();

    public FixModeEnvironmentTests()
    {
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var name = (string)entry.Key;
            if (name.StartsWith("CODENETIX__", StringComparison.OrdinalIgnoreCase))
            {
                _originalCodeNetixVariables[name] = (string?)entry.Value;
                Environment.SetEnvironmentVariable(name, null);
            }
        }

        Directory.CreateDirectory(_root);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(int ExitCode, string? Authorization)> RunAgainstFakeApiAsync(Func<string, string> yamlForUrl)
    {
        string? authorization = null;
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        app.MapPost(SubmitManualEventEndpoint.Route, (HttpContext context) =>
        {
            authorization = context.Request.Headers.Authorization.ToString();
            return Results.Json(new { eventId = Guid.CreateVersion7(), created = true, status = "Accepted" }, statusCode: 201);
        });
        await app.StartAsync(Ct);

        var yamlPath = Path.Combine(_root, "codenetix.instance.yml");
        await File.WriteAllTextAsync(yamlPath, yamlForUrl(app.Urls.First()), Ct);
        Environment.SetEnvironmentVariable("CODENETIX__InstanceConfigPath", yamlPath);

        var exitCode = await FixMode.RunAsync(["https://github.com/o/r/issues/1"], FixMode.BuildConfiguration(), _stdout, _stderr, cancellationToken: Ct);
        await app.StopAsync(Ct);
        return (exitCode, authorization);
    }

    [Fact]
    public async Task Url_and_AccessToken_are_read_from_the_instance_yaml()
    {
        var (exitCode, authorization) = await RunAgainstFakeApiAsync(url => $"""
            Host:
              Url: "{url}"
              AccessToken: yaml-access-token-0123456789
            """);

        Assert.Equal(CodeNetixCommandLine.ExitOk, exitCode);
        Assert.Equal("Bearer yaml-access-token-0123456789", authorization);
    }

    [Fact]
    public async Task DataDirectory_from_the_instance_yaml_locates_admin_token()
    {
        var dataDirectory = Path.Combine(_root, "data");
        Directory.CreateDirectory(dataDirectory);
        await File.WriteAllTextAsync(Path.Combine(dataDirectory, AdminTokenStore.FileName), "cnx_yaml-data-dir", Ct);

        var (exitCode, authorization) = await RunAgainstFakeApiAsync(url => $"""
            Host:
              Url: "{url}"
            Persistence:
              DataDirectory: "{dataDirectory.Replace("\\", "/", StringComparison.Ordinal)}"
            """);

        Assert.Equal(CodeNetixCommandLine.ExitOk, exitCode);
        Assert.Equal("Bearer cnx_yaml-data-dir", authorization);
    }

    [Fact]
    public async Task Environment_wins_over_the_instance_yaml()
    {
        Environment.SetEnvironmentVariable("CODENETIX__Host__AccessToken", "env-access-token-0123456789");

        var (exitCode, authorization) = await RunAgainstFakeApiAsync(url => $"""
            Host:
              Url: "{url}"
              AccessToken: yaml-access-token-0123456789
            """);

        Assert.Equal(CodeNetixCommandLine.ExitOk, exitCode);
        Assert.Equal("Bearer env-access-token-0123456789", authorization);
    }

    public void Dispose()
    {
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var name = (string)entry.Key;
            if (name.StartsWith("CODENETIX__", StringComparison.OrdinalIgnoreCase))
            {
                Environment.SetEnvironmentVariable(name, null);
            }
        }

        foreach (var (name, value) in _originalCodeNetixVariables)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        _stdout.Dispose();
        _stderr.Dispose();
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
