using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CodeNetix.Host.Access;
using CodeNetix.Host.Configuration;
using CodeNetix.Host.Features.ReceiveWebhook;
using CodeNetix.Host.Secrets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CodeNetix.Host.Tests;

/// <summary>Matriz de la historia 1.3: admin-token, middleware de autenticación y puertos.</summary>
public sealed class AccessTests : IDisposable
{
    private const string WelcomeMarker = "Bienvenida de prueba";
    private const string ConfiguredToken = "configured-access-token-0123456789";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "codenetix-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _dataDirectory;
    private readonly string _webRoot;

    public AccessTests()
    {
        _dataDirectory = Path.Combine(_root, "data");
        _webRoot = Path.Combine(_root, "wwwroot");
        Directory.CreateDirectory(_webRoot);
        File.WriteAllText(Path.Combine(_webRoot, "index.html"), $"<!doctype html><html><body>{WelcomeMarker}</body></html>");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string TokenPath => Path.Combine(_dataDirectory, AdminTokenStore.FileName);

    /// <summary>Sin <c>AccessToken</c>: el token efectivo es el de <c>admin.token</c>.</summary>
    private CodeNetixFactory CreateFileTokenFactory(Dictionary<string, string?>? extra = null)
    {
        var settings = new Dictionary<string, string?>(CodeNetixFactory.CompleteSettings)
        {
            [CodeNetixFactory.AccessTokenKey] = null,
        };
        foreach (var (key, value) in extra ?? [])
        {
            settings[key] = value;
        }

        return new CodeNetixFactory(_dataDirectory, _webRoot, settings);
    }

    private CodeNetixFactory CreateFactory(Dictionary<string, string?>? settings = null) =>
        new(_dataDirectory, _webRoot, settings ?? new Dictionary<string, string?>(CodeNetixFactory.CompleteSettings));

    // --- admin.token -------------------------------------------------------------

    [Fact]
    public async Task First_start_creates_admin_token_file_with_prefix_and_0600()
    {
        await using var factory = CreateFileTokenFactory();
        using var client = factory.CreateClient();
        (await client.GetAsync("/api/health", Ct)).EnsureSuccessStatusCode();

        Assert.True(File.Exists(TokenPath), $"No existe {TokenPath}");
        var token = await File.ReadAllTextAsync(TokenPath, Ct);
        Assert.StartsWith(AdminTokenStore.TokenPrefix, token, StringComparison.Ordinal);
        Assert.Equal(AdminTokenStore.TokenPrefix.Length + 43, token.Length); // 32 bytes en base64url sin relleno
        Assert.Matches("^cnx_[A-Za-z0-9_-]+$", token);

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(TokenPath));
        }

        // El archivo es el token efectivo.
        using var authorized = factory.CreateAuthorizedClient(token);
        using var response = await authorized.GetAsync("/api/nope", Ct);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Restart_reuses_the_token_without_rewriting_the_file()
    {
        await using (var first = CreateFileTokenFactory())
        {
            using var client = first.CreateClient();
            (await client.GetAsync("/api/health", Ct)).EnsureSuccessStatusCode();
        }

        var token = await File.ReadAllTextAsync(TokenPath, Ct);
        var writtenAt = DateTime.UtcNow.AddDays(-1);
        File.SetLastWriteTimeUtc(TokenPath, writtenAt);

        await using (var second = CreateFileTokenFactory())
        {
            using var client = second.CreateAuthorizedClient(token);
            using var response = await client.GetAsync("/api/nope", Ct);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        Assert.Equal(token, await File.ReadAllTextAsync(TokenPath, Ct));
        Assert.Equal(writtenAt, File.GetLastWriteTimeUtc(TokenPath));
    }

    [Fact]
    public async Task Existing_file_is_reused_as_is()
    {
        Directory.CreateDirectory(_dataDirectory);
        const string Existing = "cnx_operator-provided-token-0123456789";
        await File.WriteAllTextAsync(TokenPath, Existing + "\n", Ct);
        if (!OperatingSystem.IsWindows())
        {
            // 0644: permisos más abiertos de lo debido.
            File.SetUnixFileMode(TokenPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }

        await using var factory = CreateFileTokenFactory();
        using var client = factory.CreateAuthorizedClient(Existing);
        using var response = await client.GetAsync("/api/nope", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(Existing + "\n", await File.ReadAllTextAsync(TokenPath, Ct));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(TokenPath));
        }
    }

    [Fact]
    public async Task Configured_AccessToken_wins_and_file_token_is_rejected()
    {
        await using var factory = CreateFactory(new Dictionary<string, string?>(CodeNetixFactory.CompleteSettings)
        {
            [CodeNetixFactory.AccessTokenKey] = ConfiguredToken,
        });

        using (var client = factory.CreateAuthorizedClient(ConfiguredToken))
        using (var response = await client.GetAsync("/api/nope", Ct))
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        // El archivo se genera igualmente, pero su token no vale.
        Assert.True(File.Exists(TokenPath));
        var fileToken = await File.ReadAllTextAsync(TokenPath, Ct);
        using (var client = factory.CreateAuthorizedClient(fileToken))
        using (var response = await client.GetAsync("/api/nope", Ct))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task Logs_name_the_token_path_but_never_its_value()
    {
        var logs = new CapturingLoggerProvider();
        await using var factory = CreateFileTokenFactory();
        await using var withLogs = factory.WithWebHostBuilder(b =>
            b.ConfigureTestServices(s => s.AddSingleton<ILoggerProvider>(logs)));
        using var client = withLogs.CreateClient();
        (await client.GetAsync("/api/health", Ct)).EnsureSuccessStatusCode();

        var token = await File.ReadAllTextAsync(TokenPath, Ct);
        var all = string.Join('\n', logs.Messages);
        Assert.Contains(TokenPath, all, StringComparison.Ordinal);
        Assert.DoesNotContain(token, all, StringComparison.Ordinal);
    }

    // --- Middleware de autenticación -----------------------------------------------

    [Theory]
    [InlineData("/api/nope")]
    [InlineData("/API/nope")]
    [InlineData("/api")]
    public async Task Api_without_token_is_401_with_challenge_never_404(string path)
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(path, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(response.Headers.WwwAuthenticate, h => h.Scheme == "Bearer");
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain(WelcomeMarker, body, StringComparison.Ordinal);
        Assert.DoesNotContain(CodeNetixFactory.TestAccessToken, body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Bearer otro")]
    [InlineData("Bearer")]
    [InlineData("Bearer ")]
    [InlineData("Basic " + CodeNetixFactory.TestAccessToken)]
    [InlineData(CodeNetixFactory.TestAccessToken)]
    [InlineData("Bearer " + CodeNetixFactory.TestAccessToken + "x")]
    public async Task Wrong_token_or_scheme_is_401(string authorization)
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/nope");
        request.Headers.TryAddWithoutValidation("Authorization", authorization);

        using var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("Bearer")]
    [InlineData("bearer")]
    [InlineData("BEARER")]
    public async Task Correct_token_passes_through_with_case_insensitive_scheme(string scheme)
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/nope");
        request.Headers.TryAddWithoutValidation("Authorization", $"{scheme} {CodeNetixFactory.TestAccessToken}");

        using var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("/hubs/runs")]
    [InlineData("/HUBS/runs/negotiate")]
    public async Task Hub_without_token_is_401(string path)
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(path, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(response.Headers.WwwAuthenticate, h => h.Scheme == "Bearer");
    }

    [Fact]
    public async Task Health_is_anonymous()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/health", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<HealthBody>(Ct);
        Assert.Equal("Ready", body?.Status);
    }

    [Theory]
    [InlineData("GET", "/api/health/")]
    [InlineData("GET", "/API/Health/")]
    [InlineData("HEAD", "/api/health")]
    [InlineData("HEAD", "/api/health/")]
    public async Task Health_with_HEAD_or_trailing_slash_is_anonymous(string method, string path)
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);

        using var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/health//")]
    [InlineData("/api/health/x")]
    public async Task Health_lookalikes_still_require_token(string path)
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(path, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Only_GET_health_is_anonymous()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/api/health", content: null, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Panel_stays_anonymous()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(WelcomeMarker, await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    // --- Puertos -------------------------------------------------------------------

    [Fact]
    public async Task Webhook_on_5001_when_Ready_is_404_not_available_and_never_401()
    {
        await using var factory = CreateFactory();
        _ = factory.Server;
        await WaitForStatusAsync(factory, "Ready");

        var context = await SendAsync(factory, ListenPorts.Webhooks, HttpMethods.Post, "/webhooks/sentry");

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.Contains(ReceiveWebhookEndpoint.SourceNotAvailableTitle, await ReadBodyAsync(context), StringComparison.Ordinal);

        // GitHub (7.2) sin CODENETIX__GitHub__WebhookSecret: desactivado, 404 y nunca 401.
        var gitHub = await SendAsync(factory, ListenPorts.Webhooks, HttpMethods.Post, "/webhooks/github");

        Assert.Equal(StatusCodes.Status404NotFound, gitHub.Response.StatusCode);
        Assert.Contains(ReceiveWebhookEndpoint.GitHubNotConfiguredTitle, await ReadBodyAsync(gitHub), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Webhook_on_5001_in_Diagnostic_is_503()
    {
        await using var factory = CreateFactory(new Dictionary<string, string?>()); // sin keys -> Diagnostic
        _ = factory.Server;
        await WaitForStatusAsync(factory, "Diagnostic");

        var context = await SendAsync(factory, ListenPorts.Webhooks, HttpMethods.Post, "/webhooks/github");

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/api/health")]
    [InlineData("/api/nope")]
    [InlineData("/hubs/runs")]
    [InlineData("/runs/x")]
    public async Task Panel_or_api_on_5001_is_404(string path)
    {
        await using var factory = CreateFactory();
        _ = factory.Server;

        var context = await SendAsync(factory, ListenPorts.Webhooks, HttpMethods.Get, path);

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.DoesNotContain(WelcomeMarker, await ReadBodyAsync(context), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ListenPorts.Webhooks, "/api/")]
    [InlineData(ListenPorts.Main, "/webhooks/")]
    [InlineData(0, "/webhooks/")]
    public async Task Port_guard_404_never_echoes_the_path(int port, string prefix)
    {
        const string Secret = "sk-ant-api03-ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789abcd";
        const string NonPattern = "local-key-0123456789abcd";
        await using var factory = CreateFactory();
        _ = factory.Server;

        foreach (var value in new[] { Secret, NonPattern })
        {
            var context = await SendAsync(factory, port, HttpMethods.Get, prefix + value);
            var body = await ReadBodyAsync(context);

            Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
            Assert.DoesNotContain(value, body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Webhook_on_5001_while_Starting_is_503()
    {
        // Host mínimo: InstanceHealth sin marcar (Starting), sin StartupReconciler.
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<InstanceHealth>();
        await using var app = builder.Build();
        app.MapReceiveWebhook();
        await app.StartAsync(Ct);

        var context = await app.GetTestServer().SendAsync(
            c =>
            {
                c.Connection.LocalPort = ListenPorts.Webhooks;
                c.Request.Method = HttpMethods.Post;
                c.Request.Path = "/webhooks/github";
            },
            Ct);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(ListenPorts.Main)]
    public async Task Webhook_on_5000_is_404(int port)
    {
        await using var factory = CreateFactory();
        _ = factory.Server;
        await WaitForStatusAsync(factory, "Ready");

        var context = await SendAsync(factory, port, HttpMethods.Post, "/webhooks/github", CodeNetixFactory.TestAccessToken);

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        var body = await ReadBodyAsync(context);
        Assert.DoesNotContain(ReceiveWebhookEndpoint.SourceNotAvailableTitle, body, StringComparison.Ordinal);
        Assert.DoesNotContain(ReceiveWebhookEndpoint.GitHubNotConfiguredTitle, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Host_header_does_not_select_the_webhook_port()
    {
        await using var factory = CreateFactory();
        _ = factory.Server;

        var context = await factory.Server.SendAsync(
            c =>
            {
                c.Request.Method = HttpMethods.Get;
                c.Request.Path = "/api/nope";
                c.Request.Host = new HostString("localhost", ListenPorts.Webhooks);
            },
            Ct);

        // Por LocalPort (0 -> :5000): sigue exigiendo token.
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    // --- Exposed -------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0123456789abcde")] // 15
    [InlineData("  0123456789abcde  ")] // 15 tras recortar
    public void Exposed_requires_an_AccessToken_of_16_or_more(string? token)
    {
        var reason = AccessOptionsValidator.Validate(new HostInstanceOptions { Exposed = true, AccessToken = token });

        Assert.Equal(AccessOptionsValidator.ExposedWithoutAccessToken, reason);
    }

    [Theory]
    [InlineData(true, "0123456789abcdef")]
    [InlineData(false, null)]
    [InlineData(false, "short")]
    public void Valid_access_configuration_passes(bool exposed, string? token)
    {
        Assert.Null(AccessOptionsValidator.Validate(new HostInstanceOptions { Exposed = exposed, AccessToken = token }));
    }

    // --- Redacción -------------------------------------------------------------------

    [Fact]
    public void Generated_tokens_are_redacted_by_pattern()
    {
        var token = AdminTokenStore.Generate();
        var redactor = new SecretRedactor([]);

        var output = redactor.Redact($"Authorization: Bearer {token}");

        Assert.DoesNotContain(token, output, StringComparison.Ordinal);
        Assert.Equal($"Authorization: Bearer cnx…{token[^4..]}", output);
        Assert.Equal("cnx_short", redactor.Redact("cnx_short"));
    }

    // --- Utilidades ------------------------------------------------------------------

    private static async Task<HttpContext> SendAsync(CodeNetixFactory factory, int localPort, string method, string path, string? token = null) =>
        await factory.Server.SendAsync(
            c =>
            {
                c.Connection.LocalPort = localPort;
                c.Request.Method = method;
                c.Request.Path = path;
                if (token is not null)
                {
                    c.Request.Headers.Authorization = $"Bearer {token}";
                }
            },
            Ct);

    private static async Task<string> ReadBodyAsync(HttpContext context)
    {
        using var reader = new StreamReader(context.Response.Body);
        return await reader.ReadToEndAsync(Ct);
    }

    private static async Task WaitForStatusAsync(CodeNetixFactory factory, string status)
    {
        using var client = factory.CreateClient();
        var body = await client.GetFromJsonAsync<HealthBody>("/api/health", Ct);
        Assert.Equal(status, body?.Status);
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

    private sealed record HealthBody(string Status);

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Messages);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                messages.Enqueue(formatter(state, exception));
        }
    }
}

/// <summary>Arranque con <c>Exposed=true</c>: redirige <see cref="Console.Error"/>, no paralelo.</summary>
[Collection(nameof(ConsoleCollection))]
public sealed class ExposedStartupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codenetix-tests-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("")]
    [InlineData("0123456789abcde")] // 15
    public async Task Exposed_without_valid_AccessToken_exits_1_naming_both_variables(string accessToken)
    {
        var dataDirectory = Path.Combine(_root, "data");
        var stderr = new StringWriter();
        var originalError = Console.Error;
        Console.SetError(stderr);
        int exitCode;
        try
        {
            // Si la validación fallase, el host arrancaría: el límite evita colgar la suite.
            exitCode = await CodeNetixCommandLine.RunAsync(
                [
                    "serve",
                    "--CODENETIX:Host:Exposed=true",
                    $"--CODENETIX:Host:AccessToken={accessToken}",
                    $"--CODENETIX:Persistence:DataDirectory={dataDirectory}",
                ])
                .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }
        finally
        {
            Console.SetError(originalError);
        }

        var output = stderr.ToString();
        Assert.Equal(CodeNetixCommandLine.ExitFailure, exitCode);
        Assert.Contains("CODENETIX__Host__Exposed", output, StringComparison.Ordinal);
        Assert.Contains("CODENETIX__Host__AccessToken", output, StringComparison.Ordinal);
        if (accessToken.Length > 0)
        {
            Assert.DoesNotContain(accessToken, output, StringComparison.Ordinal);
        }

        // El host no se llegó a construir: ni base de datos ni admin.token.
        Assert.False(Directory.Exists(dataDirectory), "El arranque llegó a crear el directorio de datos.");
    }

    [Fact]
    public async Task Non_boolean_Exposed_exits_1_without_echoing_the_value()
    {
        const string Value = "yes-secret-ish";
        var dataDirectory = Path.Combine(_root, "data");

        var (exitCode, output) = await RunServeAsync(
            $"--CODENETIX:Host:Exposed={Value}",
            $"--CODENETIX:Persistence:DataDirectory={dataDirectory}");

        Assert.Equal(CodeNetixCommandLine.ExitFailure, exitCode);
        Assert.Contains("CODENETIX__Host__Exposed", output, StringComparison.Ordinal);
        Assert.DoesNotContain(Value, output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(dataDirectory), "El arranque llegó a crear el directorio de datos.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("  \n")]
    public async Task Empty_admin_token_file_aborts_startup_naming_the_path(string content)
    {
        var dataDirectory = Path.Combine(_root, "data");
        Directory.CreateDirectory(dataDirectory);
        var tokenPath = Path.Combine(dataDirectory, AdminTokenStore.FileName);
        await File.WriteAllTextAsync(tokenPath, content, TestContext.Current.CancellationToken);

        int exitCode;
        string output;
        try
        {
            (exitCode, output) = await RunServeAsync(
                "--CODENETIX:Host:AccessToken=",
                $"--CODENETIX:Persistence:DataDirectory={dataDirectory}");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }

        Assert.Equal(CodeNetixCommandLine.ExitFailure, exitCode);
        Assert.Contains(tokenPath, output, StringComparison.Ordinal);
        Assert.Equal(content, await File.ReadAllTextAsync(tokenPath, TestContext.Current.CancellationToken));
    }

    /// <summary><c>codenetix serve</c> con stderr capturado; el límite evita colgar la suite si llegase a arrancar.</summary>
    private static async Task<(int ExitCode, string Stderr)> RunServeAsync(params string[] args)
    {
        var stderr = new StringWriter();
        var originalError = Console.Error;
        Console.SetError(stderr);
        try
        {
            var exitCode = await CodeNetixCommandLine.RunAsync(["serve", .. args])
                .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            return (exitCode, stderr.ToString());
        }
        finally
        {
            Console.SetError(originalError);
        }
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
