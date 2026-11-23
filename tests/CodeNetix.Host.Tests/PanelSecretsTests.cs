using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CodeNetix.Agents;
using CodeNetix.Contracts;
using CodeNetix.GitHub;
using CodeNetix.Host.Configuration;
using CodeNetix.Host.Features.ConfigureSecrets;
using CodeNetix.Host.Features.ReceiveWebhook;
using CodeNetix.Host.Secrets;
using CodeNetix.Persistence;
using CodeNetix.Pipeline.Data;
using CodeNetix.Pipeline.Domain;
using CodeNetix.Sandbox;
using CodeNetix.Sandbox.Gateway;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeNetix.Host.Tests;

/// <summary>
/// Matriz de la historia 8.1: claves desde el Panel. Guardar claves válidas lleva de <c>Diagnostic</c> a <c>Ready</c> sin
/// reiniciar; una clave inválida no guarda nada; una clave del entorno es de solo lectura; el secreto nunca aparece en
/// respuestas, logs ni en el archivo (cifrado); sin <c>admin-token</c> → 401; fuera de <c>:5000</c> → 404; tras rotar, la
/// Ejecución nueva y el Egress Gateway usan la clave nueva. GitHub y el Proveedor son falsos (Kestrel en 127.0.0.1).
/// </summary>
public sealed class PanelSecretsTests : IAsyncDisposable
{
    private const string Route = ConfigureSecretsEndpoint.Route;

    private const string PanelGitHubToken = "ghp_PanelTokenAlpha7Q2w9E4r6T8y0U1i3O5p";
    private const string RotatedGitHubToken = "ghp_PanelTokenBravo3Z5x7C9v1B2n4M6q8W0e";
    private const string PanelAnthropicKey = "sk-ant-panelKeyAlpha-Lk8Jh6Gf4Ds2Aq1Zx3Cv5Bn7";
    private const string RotatedAnthropicKey = "sk-ant-panelKeyBravo-Rt5Yu7Io9Pa1Sd3Fg5Hj7Kl9";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "codenetix-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _dataDirectory;
    private readonly string _webRoot;
    private readonly ConcurrentQueue<string> _logs = new();
    private FakeOpenAIServer? _provider;

    public PanelSecretsTests()
    {
        _dataDirectory = Path.Combine(_root, "data");
        _webRoot = Path.Combine(_root, "wwwroot");
        Directory.CreateDirectory(_webRoot);
        File.WriteAllText(Path.Combine(_webRoot, "index.html"), "<!doctype html><html><body>Panel</body></html>");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string SecretsFile => Path.Combine(_dataDirectory, PanelSecretStore.FileName);

    private string KeyFile => Path.Combine(_dataDirectory, PanelSecretStore.KeyFileName);

    // --- Diagnostic → Ready ----------------------------------------------------------------

    [Fact]
    public async Task First_launch_without_keys_is_Diagnostic_and_lists_the_three_keys_as_not_configured()
    {
        await using var factory = await CreateFactoryAsync();
        using var client = factory.CreateAuthorizedClient();

        var settings = await GetSettingsAsync(client);

        Assert.Equal("Diagnostic", settings.GetProperty("status").GetString());
        Assert.True(settings.GetProperty("keysMissing").GetBoolean());
        var reasons = settings.GetProperty("reasons").EnumerateArray().Select(r => r.GetString()).ToList();
        Assert.Contains(InstanceOptionsValidator.MissingGitHubToken, reasons);
        Assert.Contains(InstanceOptionsValidator.MissingProviderApiKey, reasons);

        var secrets = Secrets(settings);
        Assert.Equal(["GitHubToken", "AnthropicApiKey", "OpenAIApiKey"], secrets.Keys);
        foreach (var secret in secrets.Values)
        {
            Assert.False(secret.GetProperty("configured").GetBoolean());
            Assert.Equal("None", secret.GetProperty("source").GetString());
            Assert.True(secret.GetProperty("editable").GetBoolean());
            Assert.Equal(JsonValueKind.Null, secret.GetProperty("hint").ValueKind);
        }

        Assert.Equal("CODENETIX__GitHub__Token", secrets["GitHubToken"].GetProperty("environmentVariable").GetString());
        Assert.Equal("CODENETIX__Providers__Anthropic__ApiKey", secrets["AnthropicApiKey"].GetProperty("environmentVariable").GetString());
        Assert.False(File.Exists(SecretsFile));
    }

    [Fact]
    public async Task Saving_valid_keys_turns_Diagnostic_into_Ready_without_restart_and_new_runs_use_them()
    {
        await using var factory = await CreateFactoryAsync();
        using var client = factory.CreateAuthorizedClient();
        factory.GitHub!.OAuthScopes = "repo, workflow";
        Assert.Equal("Diagnostic", await HealthStatusAsync(client));

        using var response = await client.PutAsJsonAsync(Route, new { gitHubToken = PanelGitHubToken, anthropicApiKey = PanelAnthropicKey }, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);

        using var document = JsonDocument.Parse(body);
        var settings = document.RootElement;
        Assert.Equal("Ready", settings.GetProperty("status").GetString());
        Assert.Empty(settings.GetProperty("reasons").EnumerateArray());

        var secrets = Secrets(settings);
        Assert.Equal("Panel", secrets["GitHubToken"].GetProperty("source").GetString());
        Assert.Equal("…" + PanelGitHubToken[^4..], secrets["GitHubToken"].GetProperty("hint").GetString());
        Assert.Equal("Panel", secrets["AnthropicApiKey"].GetProperty("source").GetString());
        Assert.Equal("…" + PanelAnthropicKey[^4..], secrets["AnthropicApiKey"].GetProperty("hint").GetString());
        Assert.Equal("None", secrets["OpenAIApiKey"].GetProperty("source").GetString());

        var checks = settings.GetProperty("checks").EnumerateArray().ToDictionary(c => c.GetProperty("id").GetString()!, c => c.GetProperty("message").GetString()!);
        Assert.Contains("@" + FakeGitHub.DefaultLogin, checks["GitHubToken"], StringComparison.Ordinal);
        Assert.Contains("repo, workflow", checks["GitHubToken"], StringComparison.Ordinal);
        Assert.Contains("Anthropic", checks["AnthropicApiKey"], StringComparison.Ordinal);

        // Validación contra el servicio real (falsos): GitHub /user con Bearer y Anthropic /models con x-api-key.
        Assert.Contains(factory.GitHub.Requests, r => r.Kind == "user" && r.Authorization == "Bearer " + PanelGitHubToken);
        var models = Assert.Single(_provider!.Requests, r => r.PathAndQuery == "/v1/models");
        Assert.Equal(PanelAnthropicKey, models.Headers["x-api-key"]);
        Assert.Equal("Bearer " + PanelAnthropicKey, models.Headers["Authorization"]);
        Assert.Equal(SecretValidator.AnthropicVersion, models.Headers["anthropic-version"]);
        Assert.False(settings.GetProperty("keysMissing").GetBoolean());

        // El mismo proceso: /api/health ya está Ready y las opciones que leen los módulos tienen las claves.
        Assert.Equal("Ready", await HealthStatusAsync(client));
        Assert.Equal(PanelGitHubToken, factory.Services.GetRequiredService<IOptions<GitHubOptions>>().Value.Token);
        Assert.Equal(PanelAnthropicKey, factory.Services.GetRequiredService<IOptions<AgentsOptions>>().Value.Analyst!.ApiKey);
        Assert.Equal(PanelAnthropicKey, factory.Services.GetRequiredService<IOptionsMonitor<SandboxOptions>>().CurrentValue.Coder!.ApiKey);

        // El Dispatcher despierta: una Ejecución nueva llega al Analista con la key del Panel y GitHub ve el token del Panel.
        await RunToTerminalAsync(client, 1);
        var firstRun = factory.ChatClientFactory.Created;
        Assert.NotEmpty(firstRun);
        Assert.All(firstRun, c => Assert.Equal(PanelAnthropicKey, c.Endpoint.ApiKey));
        Assert.Contains(factory.GitHub.Requests, r => r.Kind.StartsWith("permission:", StringComparison.Ordinal) && r.Authorization == "Bearer " + PanelGitHubToken);

        // Rotación: la siguiente Ejecución usa la key nueva sin reiniciar.
        using var rotate = await client.PutAsJsonAsync(Route, new { anthropicApiKey = RotatedAnthropicKey }, Ct);
        Assert.Equal(HttpStatusCode.OK, rotate.StatusCode);
        await RunToTerminalAsync(client, 2);
        var secondRun = factory.ChatClientFactory.Created.Skip(firstRun.Count).ToList();
        Assert.NotEmpty(secondRun);
        Assert.All(secondRun, c => Assert.Equal(RotatedAnthropicKey, c.Endpoint.ApiKey));
    }

    [Fact]
    public async Task Saved_keys_are_encrypted_at_rest_and_survive_a_restart()
    {
        await using (var first = await CreateFactoryAsync())
        {
            using var client = first.CreateAuthorizedClient();
            using var response = await client.PutAsJsonAsync(Route, new { gitHubToken = PanelGitHubToken, anthropicApiKey = PanelAnthropicKey }, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.True(File.Exists(SecretsFile));
        Assert.True(File.Exists(KeyFile));
        var raw = await File.ReadAllBytesAsync(SecretsFile, Ct);
        var text = Encoding.UTF8.GetString(raw);
        foreach (var secret in new[] { PanelGitHubToken, PanelAnthropicKey })
        {
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
            Assert.DoesNotContain(secret[..16], text, StringComparison.Ordinal);
            Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes(secret)), text, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, await File.ReadAllTextAsync(KeyFile, Ct), StringComparison.Ordinal);
        }

        using (var envelope = JsonDocument.Parse(raw))
        {
            Assert.Equal(PanelSecretStore.FormatVersion, envelope.RootElement.GetProperty("version").GetInt32());
            Assert.True(envelope.RootElement.TryGetProperty("ciphertext", out _));
        }

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(SecretsFile));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(KeyFile));
        }

        SqliteConnection.ClearAllPools();

        // Nuevo proceso con el mismo /data: descifra las claves y arranca Ready directamente.
        await using var second = await CreateFactoryAsync();
        using var secondClient = second.CreateAuthorizedClient();
        Assert.Equal("Ready", await HealthStatusAsync(secondClient));
        var settings = await GetSettingsAsync(secondClient);
        Assert.Equal("Panel", Secrets(settings)["GitHubToken"].GetProperty("source").GetString());
        Assert.Equal(PanelGitHubToken, second.Services.GetRequiredService<IOptions<GitHubOptions>>().Value.Token);
    }

    // --- Validación ------------------------------------------------------------------------

    [Fact]
    public async Task Invalid_GitHub_token_is_rejected_with_a_clear_message_and_nothing_is_stored()
    {
        await using var factory = await CreateFactoryAsync();
        using var client = factory.CreateAuthorizedClient();
        factory.GitHub!.UserStatus = StatusCodes.Status401Unauthorized;

        using var response = await client.PutAsJsonAsync(Route, new { gitHubToken = PanelGitHubToken, anthropicApiKey = PanelAnthropicKey }, Ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(Ct);
        using var problem = JsonDocument.Parse(body);
        var errors = problem.RootElement.GetProperty("errors");
        Assert.StartsWith("GitHub rechaza el token", errors.GetProperty("gitHubToken").GetString(), StringComparison.Ordinal);
        Assert.False(errors.TryGetProperty("anthropicApiKey", out _));
        Assert.DoesNotContain(PanelGitHubToken, body, StringComparison.Ordinal);

        // Todo o nada: tampoco se guarda la key de Anthropic, que sí era válida.
        Assert.False(File.Exists(SecretsFile));
        Assert.Equal("Diagnostic", await HealthStatusAsync(client));
        Assert.All(Secrets(await GetSettingsAsync(client)).Values, s => Assert.Equal("None", s.GetProperty("source").GetString()));
    }

    [Fact]
    public async Task Invalid_provider_key_is_rejected_and_the_valid_GitHub_token_is_not_stored_either()
    {
        await using var factory = await CreateFactoryAsync();
        using var client = factory.CreateAuthorizedClient();
        _provider!.ModelsStatus = StatusCodes.Status401Unauthorized;

        using var response = await client.PutAsJsonAsync(Route, new { gitHubToken = PanelGitHubToken, anthropicApiKey = PanelAnthropicKey }, Ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(
            "Anthropic rechaza la key: no es válida, está mal copiada o se ha revocado.",
            problem.RootElement.GetProperty("errors").GetProperty("anthropicApiKey").GetString());
        Assert.False(File.Exists(SecretsFile));
        Assert.Null(factory.Services.GetRequiredService<IOptions<GitHubOptions>>().Value.Token);
    }

    [Fact]
    public async Task Unreachable_provider_is_reported_as_no_network()
    {
        // Un puerto local cerrado: conexión rechazada al momento.
        var closedPort = GetClosedPort();
        await using var factory = await CreateFactoryAsync(new Dictionary<string, string?>
        {
            ["CODENETIX:Providers:Anthropic:BaseUrl"] = $"http://127.0.0.1:{closedPort}/v1/",
        });
        using var client = factory.CreateAuthorizedClient();

        using var response = await client.PutAsJsonAsync(Route, new { anthropicApiKey = PanelAnthropicKey }, Ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.StartsWith("No se puede conectar con Anthropic", problem.RootElement.GetProperty("errors").GetProperty("anthropicApiKey").GetString(), StringComparison.Ordinal);
        Assert.False(File.Exists(SecretsFile));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"gitHubToken":"   "}""")]
    [InlineData("""{"gitHubToken":"ghp_abc def0123456789"}""")]
    [InlineData("""{"anthropicApiKey":"short"}""")]
    [InlineData("""{"gitHubToken":"ghp_abc​def0123456789"}""")]
    [InlineData("""{"gitHubToken":"ghp_abcñdef0123456789"}""")]
    [InlineData("""{"anthropicApiKey":"sk-ant-abc\tdef0123456789"}""")]
    public async Task Malformed_requests_are_400_without_calling_anyone(string json)
    {
        await using var factory = await CreateFactoryAsync();
        using var client = factory.CreateAuthorizedClient();
        var before = factory.GitHub!.UserCalls;

        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await client.PutAsync(Route, content, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, factory.GitHub.UserCalls);
        Assert.DoesNotContain(_provider!.Requests, r => r.PathAndQuery == "/v1/models");
        Assert.False(File.Exists(SecretsFile));
    }

    [Fact]
    public async Task A_save_that_would_leave_the_configuration_incomplete_is_rejected_and_Ready_is_kept()
    {
        // Un Proveedor local con key (Ready); añadir OpenAI dejaría dos Proveedores con key y ninguno es Anthropic.
        await using var factory = await CreateFactoryAsync(new Dictionary<string, string?>
        {
            ["CODENETIX:GitHub:Token"] = "ghp_EnvironmentToken0123456789abcdefghij",
            ["CODENETIX:Providers:Local:ApiKey"] = "local-key-0123456789abcdef",
            ["CODENETIX:Providers:Local:BaseUrl"] = "http://127.0.0.1:1/v1/",
            ["CODENETIX:Agents:Analyst:Model"] = "local-model",
        });
        using var client = factory.CreateAuthorizedClient();
        Assert.Equal("Ready", await HealthStatusAsync(client));

        using var response = await client.PutAsJsonAsync(Route, new { openAIApiKey = "sk-proj-openaiKey0123456789abcdefghijkl" }, Ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Contains("ninguno es Anthropic", problem.RootElement.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.False(File.Exists(SecretsFile));
        Assert.Equal("Ready", await HealthStatusAsync(client));
    }

    // --- Precedencia del entorno -----------------------------------------------------------

    [Fact]
    public async Task Environment_keys_are_shown_read_only_and_cannot_be_overwritten()
    {
        await using var factory = await CreateFactoryAsync(CodeNetixFactory.CompleteSettings);
        using var client = factory.CreateAuthorizedClient();
        var environmentToken = CodeNetixFactory.CompleteSettings["CODENETIX:GitHub:Token"]!;
        var environmentKey = CodeNetixFactory.CompleteSettings["CODENETIX:Providers:Anthropic:ApiKey"]!;

        using var getResponse = await client.GetAsync(Route, Ct);
        var getBody = await getResponse.Content.ReadAsStringAsync(Ct);
        using var getDocument = JsonDocument.Parse(getBody);
        var secrets = Secrets(getDocument.RootElement);
        Assert.Equal("Ready", getDocument.RootElement.GetProperty("status").GetString());
        Assert.Equal("Environment", secrets["GitHubToken"].GetProperty("source").GetString());
        Assert.True(secrets["GitHubToken"].GetProperty("configured").GetBoolean());
        Assert.False(secrets["GitHubToken"].GetProperty("editable").GetBoolean());
        Assert.Equal("…" + environmentToken[^4..], secrets["GitHubToken"].GetProperty("hint").GetString());
        Assert.Equal("Environment", secrets["AnthropicApiKey"].GetProperty("source").GetString());
        Assert.False(secrets["AnthropicApiKey"].GetProperty("editable").GetBoolean());
        Assert.True(secrets["OpenAIApiKey"].GetProperty("editable").GetBoolean());
        Assert.DoesNotContain(environmentToken, getBody, StringComparison.Ordinal);
        Assert.DoesNotContain(environmentKey, getBody, StringComparison.Ordinal);

        var userCalls = factory.GitHub!.UserCalls;
        using var putResponse = await client.PutAsJsonAsync(Route, new { gitHubToken = PanelGitHubToken }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, putResponse.StatusCode);
        using var problem = JsonDocument.Parse(await putResponse.Content.ReadAsStringAsync(Ct));
        Assert.Contains("CODENETIX__GitHub__Token", problem.RootElement.GetProperty("errors").GetProperty("gitHubToken").GetString(), StringComparison.Ordinal);
        Assert.Equal(userCalls, factory.GitHub.UserCalls);
        Assert.False(File.Exists(SecretsFile));
        Assert.Equal(environmentToken, factory.Services.GetRequiredService<IOptions<GitHubOptions>>().Value.Token);
    }

    [Fact]
    public async Task A_key_from_the_instance_file_is_reported_as_ConfigFile_and_is_read_only()
    {
        var yamlPath = Path.Combine(_root, "instance.yml");
        await File.WriteAllTextAsync(yamlPath, "GitHub:\n  Token: ghp_YamlToken0123456789abcdefghijklmnop\n", Ct);
        await using var factory = await CreateFactoryAsync(new Dictionary<string, string?> { ["CODENETIX:InstanceConfigPath"] = yamlPath });
        using var client = factory.CreateAuthorizedClient();

        var secrets = Secrets(await GetSettingsAsync(client));
        Assert.Equal("ConfigFile", secrets["GitHubToken"].GetProperty("source").GetString());
        Assert.False(secrets["GitHubToken"].GetProperty("editable").GetBoolean());

        using var response = await client.PutAsJsonAsync(Route, new { gitHubToken = PanelGitHubToken }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // --- Secretos fuera de respuestas y logs -------------------------------------------------

    [Fact]
    public async Task Secrets_never_appear_in_api_responses_or_logs_and_are_registered_with_the_redactor()
    {
        await using var baseFactory = await CreateFactoryAsync();
        await using var factory = baseFactory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddSingleton<ILoggerProvider>(new RawLoggerProvider(_logs))));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", CodeNetixFactory.TestAccessToken);

        var bodies = new List<string>();
        _provider!.ModelsStatus = StatusCodes.Status401Unauthorized;
        using (var rejected = await client.PutAsJsonAsync(Route, new { gitHubToken = PanelGitHubToken, anthropicApiKey = PanelAnthropicKey }, Ct))
        {
            bodies.Add(await rejected.Content.ReadAsStringAsync(Ct));
        }

        _provider.ModelsStatus = StatusCodes.Status200OK;
        using (var saved = await client.PutAsJsonAsync(Route, new { gitHubToken = PanelGitHubToken, anthropicApiKey = PanelAnthropicKey }, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            bodies.Add(await saved.Content.ReadAsStringAsync(Ct));
        }

        bodies.Add(await client.GetStringAsync(Route, Ct));
        bodies.Add(await client.GetStringAsync("/api/health", Ct));
        bodies.Add(await client.GetStringAsync("/api/runs", Ct));

        var logs = string.Join('\n', _logs);
        Assert.Contains("Claves actualizadas desde el Panel", logs, StringComparison.Ordinal);
        foreach (var secret in new[] { PanelGitHubToken, PanelAnthropicKey })
        {
            Assert.All(bodies, b => Assert.DoesNotContain(secret[..12], b, StringComparison.Ordinal));
            Assert.DoesNotContain(secret[..12], logs, StringComparison.Ordinal);
        }

        // El redactor (logs, /api, RunEvent, tráfico del Gateway) ya conoce las claves del Panel.
        var redactor = factory.Services.GetRequiredService<ISecretRedactor>();
        Assert.DoesNotContain(PanelAnthropicKey, redactor.Redact("x " + PanelAnthropicKey + " y"), StringComparison.Ordinal);
        Assert.DoesNotContain(PanelGitHubToken, redactor.Redact($"token={PanelGitHubToken}"), StringComparison.Ordinal);
    }

    // --- Acceso -----------------------------------------------------------------------------

    [Fact]
    public async Task Requests_without_a_valid_admin_token_get_401()
    {
        await using var factory = await CreateFactoryAsync();
        using var anonymous = factory.CreateClient();
        using var wrong = factory.CreateAuthorizedClient("not-the-admin-token-0123456789");

        foreach (var client in new[] { anonymous, wrong })
        {
            using var get = await client.GetAsync(Route, Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, get.StatusCode);
            using var put = await client.PutAsJsonAsync(Route, new { gitHubToken = PanelGitHubToken }, Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, put.StatusCode);
        }

        Assert.Equal(0, factory.GitHub!.UserCalls);
        Assert.False(File.Exists(SecretsFile));
    }

    [Theory]
    [InlineData("GET", ListenPorts.Webhooks)]
    [InlineData("PUT", ListenPorts.Webhooks)]
    [InlineData("GET", ListenPorts.LlmGateway)]
    [InlineData("PUT", ListenPorts.LlmGateway)]
    public async Task Endpoints_are_not_reachable_on_the_webhook_or_gateway_ports(string method, int port)
    {
        await using var factory = await CreateFactoryAsync();
        using var started = factory.CreateClient();

        var context = await factory.Server.SendAsync(
            c =>
            {
                c.Connection.LocalPort = port;
                c.Request.Method = method;
                c.Request.Path = Route;
                c.Request.Headers.Authorization = "Bearer " + CodeNetixFactory.TestAccessToken;
                if (method == "PUT")
                {
                    var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { gitHubToken = PanelGitHubToken }));
                    c.Request.ContentType = "application/json";
                    c.Request.Body = new MemoryStream(payload);
                    c.Request.ContentLength = payload.Length;
                }
            },
            Ct);

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.Equal(0, factory.GitHub!.UserCalls);
        Assert.False(File.Exists(SecretsFile));
    }

    // --- Rotación ---------------------------------------------------------------------------

    [Fact]
    public async Task After_rotation_the_gateway_injects_the_new_provider_key_for_a_new_run()
    {
        await using var factory = await CreateFactoryAsync(new Dictionary<string, string?>
        {
            ["CODENETIX:GitHub:Token"] = "ghp_EnvironmentToken0123456789abcdefghij",
        });
        using var client = factory.CreateAuthorizedClient();

        using (var first = await client.PutAsJsonAsync(Route, new { anthropicApiKey = PanelAnthropicKey }, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        Assert.Equal("Ready", await HealthStatusAsync(client));
        Assert.Equal(PanelAnthropicKey, (await SendThroughGatewayAsync(factory)).Headers["x-api-key"]);

        using (var rotate = await client.PutAsJsonAsync(Route, new { anthropicApiKey = RotatedAnthropicKey }, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, rotate.StatusCode);
        }

        var received = await SendThroughGatewayAsync(factory);
        Assert.Equal(RotatedAnthropicKey, received.Headers["x-api-key"]);
        Assert.Equal("Bearer " + RotatedAnthropicKey, received.Headers["Authorization"]);

        // Las dos keys siguen siendo secretos para el redactor.
        var redactor = factory.Services.GetRequiredService<ISecretRedactor>();
        var redacted = redactor.Redact($"{PanelAnthropicKey} {RotatedAnthropicKey}");
        Assert.DoesNotContain(PanelAnthropicKey, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(RotatedAnthropicKey, redacted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rotating_the_GitHub_token_changes_the_token_and_identity_used_by_GitHub_calls()
    {
        await using var factory = await CreateFactoryAsync();
        using var client = factory.CreateAuthorizedClient();
        using (var first = await client.PutAsJsonAsync(Route, new { gitHubToken = PanelGitHubToken, anthropicApiKey = PanelAnthropicKey }, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        var identity = factory.Services.GetRequiredService<IGitHubIdentity>();
        Assert.Equal(FakeGitHub.DefaultLogin, await identity.GetLoginAsync(Ct));

        factory.GitHub!.Login = "otra-cuenta";
        using (var rotate = await client.PutAsJsonAsync(Route, new { gitHubToken = RotatedGitHubToken }, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, rotate.StatusCode);
        }

        Assert.Equal("otra-cuenta", await identity.GetLoginAsync(Ct));
        var last = factory.GitHub.Requests.Last(r => r.Kind == "user");
        Assert.Equal("Bearer " + RotatedGitHubToken, last.Authorization);
    }

    // --- Revisión 1 -------------------------------------------------------------------------

    [Fact]
    public async Task A_value_that_fails_the_format_check_is_never_registered_with_the_redactor()
    {
        await using var factory = await CreateFactoryAsync();
        using var client = factory.CreateAuthorizedClient();

        using (var shortWord = await client.PutAsJsonAsync(Route, new { gitHubToken = "main" }, Ct))
        {
            Assert.Equal(HttpStatusCode.BadRequest, shortWord.StatusCode);
        }

        const string Invisible = "ghp_abc​def0123456789xyz";
        using (var invisible = await client.PutAsJsonAsync(Route, new { gitHubToken = Invisible }, Ct))
        {
            Assert.Equal(HttpStatusCode.BadRequest, invisible.StatusCode);
            using var problem = JsonDocument.Parse(await invisible.Content.ReadAsStringAsync(Ct));
            Assert.Equal(PanelSecretsService.InvalidCharactersMessage, problem.RootElement.GetProperty("errors").GetProperty("gitHubToken").GetString());
        }

        // Una respuesta posterior de la API que contiene "main" (la ruta en el ProblemDetails de un 404) no se enmascara.
        using var notFound = await client.GetAsync("/api/main-branch-status", Ct);
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        Assert.Contains("/api/main-branch-status", await notFound.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        var redactor = factory.Services.GetRequiredService<ISecretRedactor>();
        Assert.Equal("rama main", redactor.Redact("rama main"));
        Assert.Equal(Invisible, redactor.Redact(Invisible));
    }

    [Fact]
    public async Task The_health_recheck_after_saving_does_not_depend_on_the_request_staying_open()
    {
        await using var factory = await CreateFactoryAsync();
        using var client = factory.CreateAuthorizedClient();
        Assert.Equal("Diagnostic", await HealthStatusAsync(client));
        factory.SandboxHealth.Delay = TimeSpan.FromSeconds(3);

        // El cliente se va justo después de que las claves queden guardadas, mientras se comprueba Docker.
        using var cancel = new CancellationTokenSource();
        var put = client.PutAsJsonAsync(Route, new { gitHubToken = PanelGitHubToken, anthropicApiKey = PanelAnthropicKey }, cancel.Token);
        Assert.True(await WaitForAsync(() => File.Exists(SecretsFile)), "Las claves deberían haberse guardado.");
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => put);

        Assert.True(await WaitForAsync(async () => await HealthStatusAsync(client) == "Ready"), "La instancia debería pasar a Ready aunque el cliente se haya ido.");
    }

    [Fact]
    public async Task A_GitHub_redirect_is_not_followed_and_nothing_is_saved()
    {
        await using var target = await MiniServer.StartAsync(context => context.Response.WriteAsJsonAsync(new { login = "atacante" }));
        await using var redirecting = await MiniServer.StartAsync(context =>
        {
            context.Response.StatusCode = StatusCodes.Status302Found;
            context.Response.Headers.Location = target.BaseUrl + "/user";
            return Task.CompletedTask;
        });
        await using var factory = await CreateFactoryAsync(new Dictionary<string, string?> { [CodeNetixFactory.GitHubApiUrlKey] = redirecting.BaseUrl });
        using var client = factory.CreateAuthorizedClient();

        using var response = await client.PutAsJsonAsync(Route, new { gitHubToken = PanelGitHubToken }, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Contains("redirección (HTTP 302)", problem.RootElement.GetProperty("errors").GetProperty("gitHubToken").GetString(), StringComparison.Ordinal);
        Assert.Equal(1, redirecting.Calls);
        Assert.Equal(0, target.Calls);
        Assert.False(File.Exists(SecretsFile));
    }

    [Fact]
    public async Task A_slow_provider_or_a_stalled_GitHub_body_ends_in_a_422_timeout_message()
    {
        // Proveedor que no responde y GitHub que envía las cabeceras y deja el cuerpo a medias.
        await using var slowProvider = await MiniServer.StartAsync(async context => await Task.Delay(TimeSpan.FromSeconds(20), context.RequestAborted));
        await using var stalledGitHub = await MiniServer.StartAsync(async context =>
        {
            context.Response.ContentType = "application/json";
            context.Response.ContentLength = 1000;
            await context.Response.WriteAsync("""{"login":""", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
            await Task.Delay(TimeSpan.FromSeconds(20), context.RequestAborted);
        });
        await using var baseFactory = await CreateFactoryAsync(new Dictionary<string, string?>
        {
            ["CODENETIX:Providers:Anthropic:BaseUrl"] = slowProvider.BaseUrl + "/v1/",
            [CodeNetixFactory.GitHubApiUrlKey] = stalledGitHub.BaseUrl,
        });
        await using var factory = baseFactory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddSingleton(new SecretValidator(TimeSpan.FromSeconds(1)))));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", CodeNetixFactory.TestAccessToken);

        var stopwatch = Stopwatch.StartNew();
        using var response = await client.PutAsJsonAsync(Route, new { gitHubToken = PanelGitHubToken, anthropicApiKey = PanelAnthropicKey }, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"La comprobación debería cortarse en ~1 s ({stopwatch.Elapsed}).");
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var errors = problem.RootElement.GetProperty("errors");
        Assert.Equal("Anthropic no responde (sin respuesta en 1 s); vuelve a intentarlo.", errors.GetProperty("anthropicApiKey").GetString());
        Assert.Equal("GitHub no responde (sin respuesta en 1 s); vuelve a intentarlo.", errors.GetProperty("gitHubToken").GetString());
        Assert.False(File.Exists(SecretsFile));
    }

    [Fact]
    public async Task Rate_limited_answers_are_reported_as_rate_limit()
    {
        await using var factory = await CreateFactoryAsync();
        using var client = factory.CreateAuthorizedClient();
        factory.GitHub!.UserStatus = StatusCodes.Status429TooManyRequests;
        _provider!.ModelsStatus = StatusCodes.Status429TooManyRequests;

        using var response = await client.PutAsJsonAsync(Route, new { gitHubToken = PanelGitHubToken, anthropicApiKey = PanelAnthropicKey }, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var errors = problem.RootElement.GetProperty("errors");
        Assert.StartsWith("GitHub ha limitado las peticiones", errors.GetProperty("gitHubToken").GetString(), StringComparison.Ordinal);
        Assert.StartsWith("Anthropic ha limitado las peticiones", errors.GetProperty("anthropicApiKey").GetString(), StringComparison.Ordinal);
        Assert.False(File.Exists(SecretsFile));
    }

    [Fact]
    public async Task Docker_and_startup_reasons_keep_the_instance_Diagnostic_after_saving_keys()
    {
        const string DockerDown = "Docker no responde (prueba).";
        await using var factory = await CreateFactoryAsync();
        factory.SandboxHealth.Reasons = [DockerDown];
        using var client = factory.CreateAuthorizedClient();
        Assert.Equal("Diagnostic", await HealthStatusAsync(client));

        // Docker caído: las claves se guardan, pero la instancia sigue en Diagnostic solo por Docker.
        using (var first = await client.PutAsJsonAsync(Route, new { gitHubToken = PanelGitHubToken, anthropicApiKey = PanelAnthropicKey }, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            using var settings = JsonDocument.Parse(await first.Content.ReadAsStringAsync(Ct));
            Assert.Equal("Diagnostic", settings.RootElement.GetProperty("status").GetString());
            Assert.Equal([DockerDown], settings.RootElement.GetProperty("reasons").EnumerateArray().Select(r => r.GetString()));
            Assert.False(settings.RootElement.GetProperty("keysMissing").GetBoolean());
        }

        // Docker ya responde, pero una Ejecución que la reconciliación de arranque no pudo cerrar exige reiniciar.
        factory.SandboxHealth.Reasons = [];
        var stillActive = StartupReconciler.RunsStillActive(1);
        factory.Services.GetRequiredService<InstanceHealth>().RecordStartupReasons([stillActive]);
        using (var second = await client.PutAsJsonAsync(Route, new { anthropicApiKey = RotatedAnthropicKey }, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        }

        using var health = JsonDocument.Parse(await client.GetStringAsync("/api/health", Ct));
        Assert.Equal("Diagnostic", health.RootElement.GetProperty("status").GetString());
        Assert.Equal([stillActive], health.RootElement.GetProperty("reasons").EnumerateArray().Select(r => r.GetString()));
    }

    [Fact]
    public async Task Rotating_while_Ready_with_Docker_down_does_not_downgrade_the_instance()
    {
        await using var factory = await CreateFactoryAsync(new Dictionary<string, string?>
        {
            ["CODENETIX:GitHub:Token"] = "ghp_EnvironmentToken0123456789abcdefghij",
        });
        using var client = factory.CreateAuthorizedClient();
        using (var first = await client.PutAsJsonAsync(Route, new { anthropicApiKey = PanelAnthropicKey }, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        Assert.Equal("Ready", await HealthStatusAsync(client));
        factory.SandboxHealth.Reasons = ["Docker no responde (prueba)."];
        var dockerChecks = factory.SandboxHealth.Calls;

        using (var rotate = await client.PutAsJsonAsync(Route, new { anthropicApiKey = RotatedAnthropicKey }, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, rotate.StatusCode);
            using var settings = JsonDocument.Parse(await rotate.Content.ReadAsStringAsync(Ct));
            Assert.Equal("Ready", settings.RootElement.GetProperty("status").GetString());
        }

        Assert.Equal("Ready", await HealthStatusAsync(client));
        Assert.Equal(dockerChecks, factory.SandboxHealth.Calls);
        Assert.Equal(RotatedAnthropicKey, factory.Services.GetRequiredService<IOptions<AgentsOptions>>().Value.Analyst!.ApiKey);
    }

    [Fact]
    public async Task An_unreadable_secrets_file_is_reported_as_a_notice_without_blocking_the_instance()
    {
        Directory.CreateDirectory(_dataDirectory);
        await File.WriteAllTextAsync(SecretsFile, "esto no es un archivo cifrado", Ct);
        await using var factory = await CreateFactoryAsync(CodeNetixFactory.CompleteSettings);
        using var client = factory.CreateAuthorizedClient();

        var settings = await GetSettingsAsync(client);

        Assert.Equal("Ready", settings.GetProperty("status").GetString());
        var notice = settings.GetProperty("notice").GetString();
        Assert.Contains(PanelSecretStore.FileName, notice, StringComparison.Ordinal);
        Assert.Contains("vuelve a introducirlas", notice, StringComparison.Ordinal);
    }

    // --- Almacén ----------------------------------------------------------------------------

    [Fact]
    public void Store_round_trips_and_reports_a_tampered_or_keyless_file_without_throwing()
    {
        var directory = Path.Combine(_root, "store");
        var options = Options.Create(new PersistenceOptions { DataDirectory = directory });
        var store = new PanelSecretStore(options);
        Assert.Null(store.LoadError);
        Assert.Null(store.Current.Get(PanelSecretId.GitHubToken));

        store.Save(new PanelSecrets(new Dictionary<PanelSecretId, string>
        {
            [PanelSecretId.GitHubToken] = PanelGitHubToken,
            [PanelSecretId.OpenAIApiKey] = " sk-openai-0123456789abcdef ",
        }));

        var reloaded = new PanelSecretStore(options);
        Assert.Null(reloaded.LoadError);
        Assert.Equal(PanelGitHubToken, reloaded.Current.Get(PanelSecretId.GitHubToken));
        Assert.Equal("sk-openai-0123456789abcdef", reloaded.Current.Get(PanelSecretId.OpenAIApiKey));
        Assert.DoesNotContain(PanelGitHubToken, reloaded.Current.ToString(), StringComparison.Ordinal);

        // Manipulado: GCM detecta el cambio; se arranca sin claves y con un aviso que no cita valores.
        var path = Path.Combine(directory, PanelSecretStore.FileName);
        var envelope = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
        var ciphertext = Convert.FromBase64String(envelope["ciphertext"]!.GetValue<string>());
        ciphertext[0] ^= 0xFF;
        envelope["ciphertext"] = Convert.ToBase64String(ciphertext);
        File.WriteAllText(path, envelope.ToJsonString());

        var tampered = new PanelSecretStore(options);
        Assert.Null(tampered.Current.Get(PanelSecretId.GitHubToken));
        Assert.Contains(PanelSecretStore.FileName, tampered.LoadError, StringComparison.Ordinal);

        // Volver a guardar lo arregla (con una clave de cifrado nueva si falta la anterior).
        File.Delete(Path.Combine(directory, PanelSecretStore.KeyFileName));
        var keyless = new PanelSecretStore(options);
        Assert.Contains(PanelSecretStore.KeyFileName, keyless.LoadError, StringComparison.Ordinal);
        keyless.Save(new PanelSecrets(new Dictionary<PanelSecretId, string> { [PanelSecretId.GitHubToken] = RotatedGitHubToken }));
        Assert.Null(keyless.LoadError);
        Assert.Equal(RotatedGitHubToken, new PanelSecretStore(options).Current.Get(PanelSecretId.GitHubToken));
    }

    [Theory]
    [InlineData("ghp_0123456789abcdef", "…cdef")]
    [InlineData("12345678901", "…")]
    public void Hint_only_reveals_the_last_four_characters_of_long_values(string value, string expected) =>
        Assert.Equal(expected, PanelSecretCatalog.Hint(value));

    // --- Utilidades -------------------------------------------------------------------------

    /// <summary>Fábrica con un Proveedor falso como URL base de Anthropic y OpenAI; sin claves salvo en <paramref name="extra"/>.</summary>
    private async Task<CodeNetixFactory> CreateFactoryAsync(IReadOnlyDictionary<string, string?>? extra = null)
    {
        _provider ??= await FakeOpenAIServer.StartAsync(Ct);
        var settings = new Dictionary<string, string?>
        {
            ["CODENETIX:Providers:Anthropic:BaseUrl"] = _provider.BaseUrl,
            ["CODENETIX:Providers:OpenAI:BaseUrl"] = _provider.BaseUrl,
        };
        foreach (var (key, value) in extra ?? new Dictionary<string, string?>())
        {
            settings[key] = value;
        }

        return new CodeNetixFactory(_dataDirectory, _webRoot, settings);
    }

    private static async Task<JsonElement> GetSettingsAsync(HttpClient client)
    {
        using var response = await client.GetAsync(Route, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static Dictionary<string, JsonElement> Secrets(JsonElement settings) =>
        settings.GetProperty("secrets").EnumerateArray().ToDictionary(s => s.GetProperty("id").GetString()!, s => s.Clone());

    private static async Task<string?> HealthStatusAsync(HttpClient client)
    {
        using var document = JsonDocument.Parse(await client.GetStringAsync("/api/health", Ct));
        return document.RootElement.GetProperty("status").GetString();
    }

    private static async Task RunToTerminalAsync(HttpClient client, int number)
    {
        using var submit = await client.PostAsJsonAsync(
            "/api/events/manual",
            new { url = $"https://github.com/o/r/issues/{number}", requestId = Guid.NewGuid().ToString() },
            Ct);
        var submitBody = await submit.Content.ReadAsStringAsync(Ct);
        Assert.True(submit.StatusCode == HttpStatusCode.Created, submitBody);
        var eventId = JsonDocument.Parse(submitBody).RootElement.GetProperty("eventId").GetString();

        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            using var info = JsonDocument.Parse(await client.GetStringAsync($"/api/events/{eventId}", Ct));
            Assert.NotEqual("Ignored", info.RootElement.GetProperty("status").GetString());
            if (info.RootElement.GetProperty("runId").GetString() is { } runId)
            {
                using var run = JsonDocument.Parse(await client.GetStringAsync($"/api/runs/{runId}", Ct));
                var events = run.RootElement.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("type").GetString()).ToList();
                if (run.RootElement.GetProperty("state").GetString() is "Failed" or "Cancelled" or "PullRequestCreated"
                    && events.Contains("SandboxReleased"))
                {
                    return;
                }
            }

            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30), $"La Ejecución del Evento {eventId} no ha terminado.");
            await Task.Delay(50, Ct);
        }
    }

    private async Task<FakeOpenAIServer.Request> SendThroughGatewayAsync(CodeNetixFactory factory)
    {
        var runId = await SeedRunAsync(factory.Services);
        var gateway = factory.Services.GetRequiredService<ISandboxGateway>();
        var session = gateway.Register(runId);
        var before = _provider!.Requests.Count;
        try
        {
            var context = await factory.Server.SendAsync(
                c =>
                {
                    c.Connection.LocalPort = ListenPorts.LlmGateway;
                    c.Request.Method = HttpMethods.Post;
                    c.Request.Path = "/llm/v1/chat/completions";
                    c.Request.Headers["x-api-key"] = session.Token;
                    c.Request.ContentType = "application/json";
                    var payload = Encoding.UTF8.GetBytes("""{"model":"claude-sonnet-5-5","messages":[{"role":"user","content":"hola"}]}""");
                    c.Request.Body = new MemoryStream(payload);
                    c.Request.ContentLength = payload.Length;
                },
                Ct);
            Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        }
        finally
        {
            gateway.Revoke(runId);
        }

        return _provider.Requests.Skip(before).Single(r => r.PathAndQuery == "/v1/chat/completions");
    }

    private static async Task<Guid> SeedRunAsync(IServiceProvider services)
    {
        var factory = services.GetRequiredService<IDbContextFactory<PipelineDbContext>>();
        await using var db = await factory.CreateDbContextAsync(Ct);
        var (run, _, _) = PipelineRun.Create(Guid.CreateVersion7(), "github:o/r#" + Random.Shared.Next(1000, 9999), DateTimeOffset.UtcNow);
        db.Runs.Add(run);
        await db.SaveChangesAsync(Ct);
        return run.Id;
    }

    private static Task<bool> WaitForAsync(Func<bool> condition) => WaitForAsync(() => Task.FromResult(condition()));

    private static async Task<bool> WaitForAsync(Func<Task<bool>> condition, int timeoutSeconds = 15)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(25, Ct);
        }

        return await condition();
    }

    private static int GetClosedPort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null)
        {
            await _provider.DisposeAsync();
        }

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

    /// <summary>Servidor HTTP mínimo en 127.0.0.1 (puerto libre) que cuenta las peticiones recibidas.</summary>
    private sealed class MiniServer : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private int _calls;

        private MiniServer(Func<HttpContext, Task> handler)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            _app = builder.Build();
            _app.Run(async context =>
            {
                Interlocked.Increment(ref _calls);
                try
                {
                    await handler(context);
                }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
                {
                    // El cliente cortó la conexión (plazo de la comprobación).
                }
            });
        }

        public string BaseUrl { get; private set; } = string.Empty;

        public int Calls => Volatile.Read(ref _calls);

        public static async Task<MiniServer> StartAsync(Func<HttpContext, Task> handler)
        {
            var server = new MiniServer(handler);
            await server._app.StartAsync(Ct);
            server.BaseUrl = server._app.Urls.First().TrimEnd('/');
            return server;
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync(CancellationToken.None);
            await _app.DisposeAsync();
        }
    }

    /// <summary>Logs sin redactar: prueba que nada escribe la clave, ni siquiera antes del redactor.</summary>
    private sealed class RawLoggerProvider(ConcurrentQueue<string> messages) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new RawLogger(messages);

        public void Dispose()
        {
        }

        private sealed class RawLogger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                messages.Enqueue(formatter(state, exception) + (exception is null ? string.Empty : " " + exception));
        }
    }
}
