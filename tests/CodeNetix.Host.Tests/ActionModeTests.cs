using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeNetix.Agents.ChatClients;
using CodeNetix.Contracts;
using CodeNetix.GitHub.Features.NotifyIssue;
using CodeNetix.Host.Configuration;
using CodeNetix.Host.Secrets;
using CodeNetix.Sandbox;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace CodeNetix.Host.Tests;

/// <summary>
/// Historia 6.1 (FR-25, FR-26, AD-12): <c>codenetix action</c> en proceso, con el entorno del runner en memoria, GitHub
/// falso, chat falso y Sandbox falso. Kestrel escucha en un puerto efímero de 127.0.0.1 (nunca :5000, :5001 ni :8080).
/// Una prueba por fila de la matriz de la spec.
/// </summary>
public sealed partial class ActionModeTests : IDisposable
{
    private const string Sender = "alice";
    private const string RunnerToken = "ghs_runnerToken0123456789abcdefABCDEF";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "codenetix-action-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _runnerTemp;
    private readonly StringWriter _stdout = new();
    private readonly StringWriter _stderr = new();

    public ActionModeTests()
    {
        _runnerTemp = Path.Combine(_root, "runner-temp");
        Directory.CreateDirectory(_runnerTemp);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Stdout => _stdout.ToString();

    private string Stderr => _stderr.ToString();

    private string[] StdoutLines => Stdout.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');

    // --- Fila 1: issue etiquetada por quien puede escribir → PR, eventos en stdout, aviso publicado, exit 0 ---

    [Fact]
    public async Task Labeled_issue_by_a_writer_runs_to_PullRequestCreated_dumps_the_RunEvents_and_exits_0()
    {
        await using var gitHub = await FakeGitHub.StartAsync(Ct);
        var harness = new Harness(gitHub);
        var dataDirectory = Path.Combine(_root, "data");
        var env = Environment(gitHub);
        env["CODENETIX:Persistence:DataDirectory"] = dataDirectory; // persistente para inspeccionar ing_events

        var exitCode = await RunAsync(env, harness);

        Assert.True(exitCode == CodeNetixCommandLine.ExitOk, Stdout + Stderr);
        Assert.Contains($"CodeNetix action: issue o/r#12 etiquetada 'codenetix' por {Sender}.", StdoutLines);
        Assert.Contains("[1] RunCreated github:o/r#12", StdoutLines);
        Assert.Contains("[2] StateChanged → Received", StdoutLines);
        Assert.Contains(StdoutLines, l => l.EndsWith("StateChanged Deciding → PullRequestCreated", StringComparison.Ordinal));
        Assert.Equal($"Resultado: PullRequestCreated {FakePullRequestPublisher.Url}", StdoutLines[^1]);

        // Cada seq una sola vez y en orden.
        var seqs = StdoutLines.Select(l => SeqPattern().Match(l)).Where(m => m.Success).Select(m => long.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).Distinct().ToList();
        Assert.Equal(Enumerable.Range(1, seqs.Count).Select(i => (long)i), seqs);

        // FR-26 sobre el sender y aviso en la issue publicado antes de salir (se espera al orquestador, no solo al Estado).
        Assert.Contains(gitHub.Requests, r => r.Kind == $"permission:o/r:{Sender}");
        Assert.NotEmpty(gitHub.CommentsOn("o", "r", 12));
        Assert.Contains(StdoutLines, l => l.Contains("IssueCommented PullRequestCreated", StringComparison.Ordinal));
        Assert.Equal(1, harness.Publisher.PublishCalls);
        Assert.All(harness.Sandbox.Sessions, s => Assert.True(s.Disposals >= 1));

        // EventEnvelope{Source=GitHubAction} con la clave idempotente y sin el JSON del evento ni el token.
        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(dataDirectory, "codenetix.db")};Pooling=False"))
        {
            await connection.OpenAsync(Ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT source, actor, dedup_key, object_key, status, payload_json FROM ing_events";
            await using var row = await command.ExecuteReaderAsync(Ct);
            Assert.True(await row.ReadAsync(Ct));
            Assert.Equal("GitHubAction", row.GetString(0));
            Assert.Equal(Sender, row.GetString(1));
            Assert.Equal("gh-action:o/r#12:run:4242:1", row.GetString(2));
            Assert.Equal("github:o/r#12", row.GetString(3));
            Assert.Equal("Accepted", row.GetString(4));
            var payload = row.GetString(5);
            Assert.DoesNotContain("Texto de la issue", payload, StringComparison.Ordinal);
            Assert.DoesNotContain(CodeNetixFactory.CompleteSettings["CODENETIX:GitHub:Token"]!, payload, StringComparison.Ordinal);
            Assert.False(await row.ReadAsync(Ct));
        }

        // Solo el Egress Gateway: ni :5000 ni :5001.
        var configuration = new ConfigurationBuilder().Build();
        Assert.Equal(["http://+:8080"], ActionMode.ListenUrls(configuration));
    }

    // --- Historia 7.1: el sondeo es solo de serve ---

    [Fact]
    public async Task Action_mode_never_registers_the_GitHub_poller_even_with_polling_repositories()
    {
        await using var gitHub = await FakeGitHub.StartAsync(Ct);
        var harness = new Harness(gitHub);
        var env = Environment(gitHub);
        env["CODENETIX:Polling:Repositories"] = "o/r";
        var registered = true;

        var exitCode = await RunAsync(env, harness, configureBuilder: builder =>
            registered = builder.Services.Any(d => d.ImplementationType == typeof(CodeNetix.Ingestion.Features.PollGitHub.GitHubPollingService)
                || d.ServiceType == typeof(CodeNetix.Ingestion.Features.PollGitHub.GitHubPoller)));

        Assert.True(exitCode == CodeNetixCommandLine.ExitOk, Stdout + Stderr);
        Assert.False(registered);
        Assert.Equal(0, gitHub.AllIssueListCalls);
    }

    // --- Fila 2: Ejecución fallida → Resultado Failed, exit 1, directorio temporal borrado ---

    [Fact]
    public async Task Failed_run_prints_the_failure_exits_1_and_deletes_the_temporary_data_directory()
    {
        await using var gitHub = await FakeGitHub.StartAsync(Ct);
        var harness = new Harness(gitHub, new FakeChatClient(FakeChatClient.NotAutomatableAnswer("missing_information", "La issue no dice qué hay que cambiar.")));

        var exitCode = await RunAsync(Environment(gitHub), harness);

        Assert.Equal(CodeNetixCommandLine.ExitFailure, exitCode);
        Assert.Contains(StdoutLines, l => l.Contains("StateChanged Analyzing → Failed (NotAutomatable", StringComparison.Ordinal));
        Assert.StartsWith("Resultado: Failed (NotAutomatable", StdoutLines[^1], StringComparison.Ordinal);
        Assert.Equal(0, harness.Publisher.PublishCalls);
        Assert.Empty(Directory.GetDirectories(_runnerTemp, "codenetix-action-*"));
    }

    // --- Fila 3: evento que no aplica → exit 0 sin arrancar nada ---

    [Theory]
    [InlineData("push", "labeled", "codenetix", "open")]
    [InlineData("issues", "opened", "codenetix", "open")]
    [InlineData("issues", "labeled", "bug", "open")]
    [InlineData("issues", "labeled", "codenetix", "closed")]
    public async Task Event_that_does_not_apply_exits_0_without_starting_the_host(string eventName, string action, string label, string state)
    {
        await using var gitHub = await FakeGitHub.StartAsync(Ct);
        var harness = new Harness(gitHub);
        var env = Environment(gitHub, LabeledEvent(action: action, label: label, state: state));
        env["GITHUB_EVENT_NAME"] = eventName;

        var exitCode = await RunAsync(env, harness);

        Assert.Equal(CodeNetixCommandLine.ExitOk, exitCode);
        Assert.StartsWith("CodeNetix action: nada que hacer: ", Assert.Single(StdoutLines), StringComparison.Ordinal);
        Assert.Equal(0, gitHub.TotalCalls);
        Assert.Equal(0, harness.SandboxHealth.Calls);
        Assert.Empty(Directory.GetDirectories(_runnerTemp, "codenetix-action-*"));
    }

    // --- Fila 4: actor sin permiso → Ignored, sin Ejecución, exit 1 ---

    [Fact]
    public async Task Sender_without_write_access_is_ignored_and_exits_1()
    {
        await using var gitHub = await FakeGitHub.StartAsync(Ct);
        gitHub.Permission = "read";
        var harness = new Harness(gitHub);

        var exitCode = await RunAsync(Environment(gitHub), harness);

        Assert.Equal(CodeNetixCommandLine.ExitFailure, exitCode);
        Assert.Contains($"no autorizado ({Sender} no tiene permiso de escritura en o/r)", Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("Ejecución ", Stdout, StringComparison.Ordinal);
        Assert.Empty(harness.Sandbox.Sessions);
        Assert.Equal(0, gitHub.IssueCalls);
    }

    // --- Fila 5: GitHub no responde al autorizar → nada persistido, exit 1 ---

    [Fact]
    public async Task GitHub_unavailable_while_authorizing_persists_nothing_and_exits_1()
    {
        await using var gitHub = await FakeGitHub.StartAsync(Ct);
        gitHub.PermissionStatus = 502;
        var harness = new Harness(gitHub);
        var dataDirectory = Path.Combine(_root, "data-unavailable");
        var env = Environment(gitHub);
        env["CODENETIX:Persistence:DataDirectory"] = dataDirectory;

        var exitCode = await RunAsync(env, harness);

        Assert.Equal(CodeNetixCommandLine.ExitFailure, exitCode);
        Assert.Contains($"No se puede autorizar a {Sender} en o/r", Stderr, StringComparison.Ordinal);
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(dataDirectory, "codenetix.db")};Pooling=False");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM ing_events";
        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync(Ct))!);
    }

    // --- Fila 6: fuera de Actions o evento inválido → exit 2 ---

    [Theory]
    [InlineData("missing-path")]
    [InlineData("missing-file")]
    [InlineData("broken-json")]
    [InlineData("repository-mismatch")]
    [InlineData("extra-args")]
    public async Task Outside_Actions_or_invalid_event_exits_2(string variant)
    {
        await using var gitHub = await FakeGitHub.StartAsync(Ct);
        var harness = new Harness(gitHub);
        var env = Environment(gitHub);
        string[] args = [];
        switch (variant)
        {
            case "missing-path":
                env.Remove("GITHUB_EVENT_PATH");
                break;
            case "missing-file":
                env["GITHUB_EVENT_PATH"] = Path.Combine(_root, "no-existe.json");
                break;
            case "broken-json":
                File.WriteAllText(env["GITHUB_EVENT_PATH"]!, "{ esto no es json");
                break;
            case "repository-mismatch":
                env["GITHUB_REPOSITORY"] = "otro/repo";
                break;
            case "extra-args":
                args = ["--follow"];
                break;
        }

        var exitCode = await RunAsync(env, harness, args);

        Assert.Equal(CodeNetixCommandLine.ExitUsage, exitCode);
        Assert.Contains(ActionMode.Usage, Stderr, StringComparison.Ordinal);
        Assert.Equal(0, gitHub.TotalCalls);
    }

    // --- Fila 7: Diagnostic → motivos en stderr, sin Evento, exit 1 ---

    [Fact]
    public async Task Diagnostic_instance_prints_the_reasons_and_exits_1_without_an_event()
    {
        await using var gitHub = await FakeGitHub.StartAsync(Ct);
        var harness = new Harness(gitHub);
        var env = Environment(gitHub);
        env.Remove("CODENETIX:Providers:Anthropic:ApiKey");

        var exitCode = await RunAsync(env, harness);

        Assert.Equal(CodeNetixCommandLine.ExitFailure, exitCode);
        Assert.Contains("modo diagnóstico", Stderr, StringComparison.Ordinal);
        Assert.Contains(InstanceOptionsValidator.MissingProviderApiKey, Stderr, StringComparison.Ordinal);
        Assert.Equal(0, gitHub.PermissionCalls);
        Assert.Empty(harness.Sandbox.Sessions);
    }

    // --- Fila 8: credenciales del runner (GITHUB_TOKEN + GITHUB_API_URL/GITHUB_SERVER_URL), sin GET /user ni fugas ---

    [Fact]
    public async Task Runner_credentials_are_used_without_GET_user_and_the_token_is_never_printed()
    {
        await using var gitHub = await FakeGitHub.StartAsync(Ct);
        // Un GITHUB_TOKEN real recibe 403 en GET /user; si se invocara, IssueNotifier fallaría y la aserción también.
        gitHub.UserStatus = StatusCodes.Status403Forbidden;
        // La explicación del Analista cita el token: llega al detalle del fallo y debe salir enmascarada.
        var harness = new Harness(gitHub, new FakeChatClient(FakeChatClient.NotAutomatableAnswer("missing_information", $"Falta información; el token {RunnerToken} no sirve.")));
        harness.Sandbox.OnSessionCreated = session =>
        {
            // Sembrar un comentario previo con el marcador de otro usuario obliga a IssueNotifier a consultar
            // IGitHubIdentity.GetLoginAsync: así el test ejercita realmente GitHubIdentity y fallaría si llamara a GET /user.
            gitHub.AddComment("o", "r", "12", $"Marcador copiado\n\n{IssueNotifier.Marker(session.RunId, NotificationKind.NotAutomatable)}", "otro-usuario");
        };

        var env = Environment(gitHub);
        var apiKey = env["CODENETIX:Providers:Anthropic:ApiKey"]!;
        env.Remove("CODENETIX:GitHub:Token");
        env.Remove("CODENETIX:GitHub:ApiUrl");
        env.Remove("CODENETIX:GitHub:CloneBaseUrl");
        env["GITHUB_TOKEN"] = RunnerToken;
        env["GITHUB_API_URL"] = gitHub.BaseUrl;
        env["GITHUB_SERVER_URL"] = LocalGitRepos.Default.BaseUrl;

        ConsoleLoggerOptions? consoleOptions = null;
        string? formattedHostLog = null;
        var exitCode = await RunAsync(
            env,
            harness,
            configureBuilder: builder =>
            {
                builder.Services.AddSingleton<IRunCompletionObserver>(sp => new DelegateRunCompletionObserver(_ =>
                {
                    consoleOptions = sp.GetRequiredService<IOptions<ConsoleLoggerOptions>>().Value;
                    var formatter = sp.GetServices<ConsoleFormatter>().Single(f => f.Name == RedactingConsoleFormatter.FormatterName);
                    using var writer = new StringWriter();
                    var entry = new LogEntry<string>(
                        LogLevel.Warning,
                        "CodeNetix.Host.Test",
                        new EventId(1),
                        $"Host log con {RunnerToken} y {apiKey}",
                        new InvalidOperationException($"Excepción con {RunnerToken} y {apiKey}"),
                        (state, _) => state);
                    formatter.Write(in entry, scopeProvider: null, writer);
                    formattedHostLog = writer.ToString();
                }));
            });

        Assert.Equal(CodeNetixCommandLine.ExitFailure, exitCode);
        Assert.Contains("RepositoryPrepared", Stdout, StringComparison.Ordinal); // clon desde GITHUB_SERVER_URL
        Assert.NotEmpty(gitHub.Requests);
        Assert.All(gitHub.Requests.Where(r => r.Kind != "other"), r => Assert.Equal($"Bearer {RunnerToken}", r.Authorization));
        Assert.Equal(0, gitHub.UserCalls);
        Assert.DoesNotContain(gitHub.Requests, r => r.Path.EndsWith("/user", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, gitHub.CommentPosts); // ignoró el comentario suplantado y publicó el suyo
        Assert.DoesNotContain(RunnerToken, Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(RunnerToken, Stderr, StringComparison.Ordinal);

        // Logs del host: siempre a stderr y formateados con RedactingConsoleFormatter, enmascarando ambos secretos.
        Assert.NotNull(consoleOptions);
        Assert.Equal(LogLevel.Trace, consoleOptions.LogToStandardErrorThreshold);
        Assert.Equal(RedactingConsoleFormatter.FormatterName, consoleOptions.FormatterName);
        Assert.NotNull(formattedHostLog);
        Assert.DoesNotContain(RunnerToken, formattedHostLog, StringComparison.Ordinal);
        Assert.DoesNotContain(apiKey, formattedHostLog, StringComparison.Ordinal);
    }

    // --- Revisión 1 (#5): Login por defecto solo si el token vino de GITHUB_TOKEN ---

    [Fact]
    public async Task Explicit_CodeNetix_GitHub_Token_does_not_force_actions_bot_login()
    {
        await using var gitHub = await FakeGitHub.StartAsync(Ct);
        var harness = new Harness(gitHub, new FakeChatClient(FakeChatClient.NotAutomatableAnswer("missing_information", "Falta detalle.")));
        harness.Sandbox.OnSessionCreated = session =>
        {
            gitHub.AddComment("o", "r", "12", $"Marcador previo\n\n{IssueNotifier.Marker(session.RunId, NotificationKind.NotAutomatable)}", "otro-usuario");
        };

        var env = Environment(gitHub);
        // CODENETIX:GitHub:Token explícito (PAT) aunque GITHUB_TOKEN también esté presente en el runner, sin Login.
        env["GITHUB_TOKEN"] = RunnerToken;

        var exitCode = await RunAsync(env, harness);

        Assert.Equal(CodeNetixCommandLine.ExitFailure, exitCode);
        Assert.Equal(1, gitHub.UserCalls); // GitHubIdentity resolvió el login del PAT vía GET /user
    }

    // --- Revisión 1 (#1): appsettings*.json del directorio de trabajo no se carga ---

    [Fact]
    public async Task AppSettings_sources_are_removed_and_cannot_override_provider_config()
    {
        var repoDir = Path.Combine(_root, "repo-with-appsettings");
        Directory.CreateDirectory(repoDir);
        File.WriteAllText(
            Path.Combine(repoDir, "appsettings.json"),
            """{"CODENETIX":{"Providers":{"Anthropic":{"BaseUrl":"https://evil.example/v1"}}}}""");

        var configBuilder = new ConfigurationBuilder()
            .SetBasePath(repoDir)
            .AddJsonFile("appsettings.json", optional: false)
            .AddInMemoryCollection(new Dictionary<string, string?> { ["CODENETIX:Providers:Anthropic:ApiKey"] = "sk-ant-test0123456789abcdef" });

        configBuilder.RemoveAppSettingsSources();
        var config = configBuilder.Build();

        Assert.Null(config["CODENETIX:Providers:Anthropic:BaseUrl"]);
        Assert.DoesNotContain(configBuilder.Sources, s => s is JsonConfigurationSource);

        // Y en ActionMode no queda ninguna fuente appsettings*.json en el builder.
        await using var gitHub = await FakeGitHub.StartAsync(Ct);
        var harness = new Harness(gitHub);
        var env = Environment(gitHub, LabeledEvent(action: "opened"));
        var hasAppSettingsSource = false;
        var exitCode = await RunAsync(
            env,
            harness,
            configureBuilder: builder =>
            {
                hasAppSettingsSource = ((IConfigurationBuilder)builder.Configuration).Sources
                    .OfType<JsonConfigurationSource>()
                    .Any(s => s.Path is not null && Path.GetFileName(s.Path).StartsWith("appsettings", StringComparison.OrdinalIgnoreCase));
            });

        Assert.Equal(CodeNetixCommandLine.ExitOk, exitCode);
        Assert.False(hasAppSettingsSource);
    }

    // --- Revisión 1 (#2): timeouts de protección si no llega runId o si la Ejecución ya era terminal ---

    [Fact]
    public async Task Dispatch_timeout_exits_1_when_event_never_receives_a_run()
    {
        await using var gitHub = await FakeGitHub.StartAsync(Ct);
        var harness = new Harness(gitHub);

        var exitCode = await RunAsync(
            Environment(gitHub),
            harness,
            configureBuilder: builder => builder.Services.AddSingleton<IEventReader>(new AlwaysPendingEventReader()),
            dispatchTimeout: TimeSpan.FromMilliseconds(150));

        Assert.Equal(CodeNetixCommandLine.ExitFailure, exitCode);
        Assert.Contains("Tiempo agotado", Stderr, StringComparison.Ordinal);
        Assert.Empty(Directory.GetDirectories(_runnerTemp, "codenetix-action-*"));
    }

    [Fact]
    public async Task Terminal_run_without_completion_signal_exits_after_grace_timeout()
    {
        await using var gitHub = await FakeGitHub.StartAsync(Ct);
        var harness = new Harness(gitHub);
        var externalRunId = Guid.CreateVersion7();

        var exitCode = await RunAsync(
            Environment(gitHub),
            harness,
            configureBuilder: builder =>
            {
                builder.Services.AddSingleton<IEventReader>(new FixedDispatchedEventReader(externalRunId));
                builder.Services.AddSingleton<IRunReader>(new FixedTerminalRunReader(externalRunId));
            },
            terminalGraceTimeout: TimeSpan.FromMilliseconds(150));

        Assert.Equal(CodeNetixCommandLine.ExitOk, exitCode);
        Assert.Equal("Resultado: PullRequestCreated", StdoutLines[^1]);
    }

    // --- Revisión 1 (#3): excepciones inesperadas se capturan, redactan, cierran stop-commands y limpian ---

    [Fact]
    public async Task Unexpected_exception_is_caught_redacted_closes_stop_commands_and_cleans_temp_dir()
    {
        await using var gitHub = await FakeGitHub.StartAsync(Ct);
        var harness = new Harness(gitHub);
        var env = Environment(gitHub);
        env["GITHUB_ACTIONS"] = "true";
        env["GITHUB_TOKEN"] = RunnerToken;

        var exitCode = await RunAsync(
            env,
            harness,
            configureBuilder: builder =>
            {
                var descriptor = builder.Services.Last(d => d.ServiceType == typeof(IGitHubActionEventIngestion));
                builder.Services.AddSingleton<IGitHubActionEventIngestion>(sp =>
                {
                    var inner = descriptor.ImplementationType is not null
                        ? (IGitHubActionEventIngestion)ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType)
                        : throw new InvalidOperationException("Se esperaba ImplementationType.");
                    return new ThrowingOnSubmitIngestion(inner, $"fallo inesperado con {RunnerToken}");
                });
            });

        Assert.Equal(CodeNetixCommandLine.ExitFailure, exitCode);
        Assert.Contains("Error inesperado", Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(RunnerToken, Stderr, StringComparison.Ordinal);
        var lines = StdoutLines;
        var start = StopCommandsPattern().Match(lines[0]);
        Assert.True(start.Success, lines[0]);
        Assert.Equal($"::{start.Groups[1].Value}::", lines[^1]);
        Assert.Empty(Directory.GetDirectories(_runnerTemp, "codenetix-action-*"));
    }

    // --- Revisión 1 (#8): Kestrel de action devuelve 404 en /api, /hubs y /openapi ---

    [Fact]
    public async Task Action_Kestrel_server_returns_404_for_api_hubs_and_openapi()
    {
        await using var gitHub = await FakeGitHub.StartAsync(Ct);
        var coder = new BlockingCodingAgent();
        var harness = new Harness(gitHub) { Coder = coder };
        IServer? server = null;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var run = RunAsync(
            Environment(gitHub),
            harness,
            configureBuilder: builder =>
            {
                builder.Services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(sp =>
                    new ServerCapturingHostedService(sp.GetRequiredService<IServer>(), s => server = s));
            },
            cancellationToken: cancel.Token);

        await coder.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        try
        {
            Assert.NotNull(server);
            var address = server.Features.Get<IServerAddressesFeature>()!.Addresses.First();
            using var client = new HttpClient { BaseAddress = new Uri(address) };
            foreach (var path in new[] { "/api/health", "/api/runs", "/hubs/runs", "/openapi/v1.json" })
            {
                using var response = await client.GetAsync(path, Ct);
                Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{path} devolvió {response.StatusCode}");
            }
        }
        finally
        {
            await cancel.CancelAsync();
        }

        Assert.Equal(CodeNetixCommandLine.ExitFailure, await run.WaitAsync(TimeSpan.FromSeconds(60), Ct));
    }

    // --- Fila 9: con GITHUB_ACTIONS=true, stdout entre ::stop-commands::<token> y ::<token>:: ---

    [Fact]
    public async Task On_GitHub_Actions_stdout_is_wrapped_in_stop_commands()
    {
        await using var gitHub = await FakeGitHub.StartAsync(Ct);
        var harness = new Harness(gitHub, new FakeChatClient(FakeChatClient.NotAutomatableAnswer("missing_information", "::set-env name=X::inyectado")));
        var env = Environment(gitHub);
        env["GITHUB_ACTIONS"] = "true";

        var exitCode = await RunAsync(env, harness);

        Assert.Equal(CodeNetixCommandLine.ExitFailure, exitCode);
        var lines = StdoutLines;
        var start = StopCommandsPattern().Match(lines[0]);
        Assert.True(start.Success, lines[0]);
        var token = start.Groups[1].Value;
        Assert.Equal($"::{token}::", lines[^1]);
        Assert.Single(lines, l => l.Contains(token, StringComparison.Ordinal) && l != lines[0]);
        Assert.Contains(lines[1..^1], l => l.Contains("[1] RunCreated", StringComparison.Ordinal));
    }

    // --- Fila 10: job interrumpido → el host se detiene, exit 1 ---

    [Fact]
    public async Task Interrupted_job_stops_the_host_and_exits_1()
    {
        await using var gitHub = await FakeGitHub.StartAsync(Ct);
        var coder = new BlockingCodingAgent();
        var harness = new Harness(gitHub) { Coder = coder };
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var run = RunAsync(Environment(gitHub), harness, cancellationToken: cancel.Token);
        await coder.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        await cancel.CancelAsync();
        var exitCode = await run.WaitAsync(TimeSpan.FromSeconds(60), Ct);

        Assert.Equal(CodeNetixCommandLine.ExitFailure, exitCode);
        Assert.Contains(ActionMode.InterruptedMessage, Stderr, StringComparison.Ordinal);
        Assert.Contains("[1] RunCreated github:o/r#12", StdoutLines);
        Assert.DoesNotContain(StdoutLines, l => l.StartsWith("Resultado:", StringComparison.Ordinal));
        Assert.Empty(Directory.GetDirectories(_runnerTemp, "codenetix-action-*"));
    }

    // --- Infraestructura ---

    private Dictionary<string, string?> Environment(FakeGitHub gitHub, string? eventJson = null)
    {
        var eventPath = Path.Combine(_root, "event-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(eventPath, eventJson ?? LabeledEvent());
        return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["GITHUB_EVENT_NAME"] = "issues",
            ["GITHUB_EVENT_PATH"] = eventPath,
            ["GITHUB_REPOSITORY"] = "o/r",
            ["GITHUB_RUN_ID"] = "4242",
            ["GITHUB_RUN_ATTEMPT"] = "1",
            ["RUNNER_TEMP"] = _runnerTemp,
            ["CODENETIX:GitHub:Token"] = CodeNetixFactory.CompleteSettings["CODENETIX:GitHub:Token"],
            ["CODENETIX:Providers:Anthropic:ApiKey"] = CodeNetixFactory.CompleteSettings["CODENETIX:Providers:Anthropic:ApiKey"],
            ["CODENETIX:GitHub:ApiUrl"] = gitHub.BaseUrl,
            ["CODENETIX:GitHub:CloneBaseUrl"] = LocalGitRepos.Default.BaseUrl,
            ["CODENETIX:Sandbox:ConnectProxyPort"] = "0",
        };
    }

    private static string LabeledEvent(string action = "labeled", string label = "codenetix", string state = "open") =>
        JsonSerializer.Serialize(new
        {
            action,
            label = new { name = label },
            issue = new { number = 12, state, title = "Texto de la issue", body = "Texto de la issue" },
            repository = new { full_name = "O/R" },
            sender = new { login = Sender, type = "User" },
        });

    private Task<int> RunAsync(
        Dictionary<string, string?> env,
        Harness harness,
        string[]? args = null,
        CancellationToken? cancellationToken = null,
        Action<WebApplicationBuilder>? configureBuilder = null,
        TimeSpan? dispatchTimeout = null,
        TimeSpan? terminalGraceTimeout = null) =>
        ActionMode.RunAsync(
            args ?? [],
            _stdout,
            _stderr,
            new ActionModeHooks
            {
                ConfigureConfiguration = configuration => ActionModeHooks.ReplaceEnvironment(configuration, env),
                ConfigureBuilder = builder =>
                {
                    builder.WebHost.UseUrls("http://127.0.0.1:0");
                    harness.Register(builder.Services);
                    configureBuilder?.Invoke(builder);
                },
                PollInterval = TimeSpan.FromMilliseconds(20),
                DispatchTimeout = dispatchTimeout,
                TerminalGraceTimeout = terminalGraceTimeout,
            },
            cancellationToken ?? Ct);

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

    [GeneratedRegex(@"^\[(\d+)\] ")]
    private static partial Regex SeqPattern();

    [GeneratedRegex("^::stop-commands::([0-9a-f]{32})$")]
    private static partial Regex StopCommandsPattern();

    /// <summary>Los fakes de <see cref="CodeNetixFactory"/> para el modo Action: ni Docker, ni Proveedor, ni push real.</summary>
    private sealed class Harness(FakeGitHub gitHub, FakeChatClient? chat = null)
    {
        public FakeGitHub GitHub { get; } = gitHub;

        public FakeSandboxManager Sandbox { get; } = new();

        public CountingSandboxHealthCheck SandboxHealth { get; } = new();

        public FakePullRequestPublisher Publisher { get; } = new();

        public FakeChatClient Chat { get; } = chat ?? new FakeChatClient();

        public ICodingAgent? Coder { get; init; }

        public void Register(IServiceCollection services)
        {
            services.AddSingleton<ISandboxManager>(Sandbox);
            services.AddSingleton<ISandboxHealthCheck>(SandboxHealth);
            services.AddSingleton<ISandboxReconciler>(new FakeSandboxReconciler());
            services.AddSingleton<ISandboxToolsProvisioner>(new FakeSandboxToolsProvisioner());
            services.AddSingleton<ITestRunner>(new FakeTestRunner());
            services.AddSingleton<IAgentChatClientFactory>(new FakeChatClientFactory(Chat));
            services.AddSingleton<IPullRequestPublisher>(Publisher);
            if (Coder is not null)
            {
                services.AddSingleton(Coder);
            }
        }
    }

    private sealed class CountingSandboxHealthCheck : ISandboxHealthCheck
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<IReadOnlyList<string>> CheckAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult<IReadOnlyList<string>>([]);
        }
    }

    private sealed class FakePullRequestPublisher : IPullRequestPublisher
    {
        public const string Url = "https://github.com/o/r/pull/77";

        private readonly ConcurrentDictionary<Guid, PullRequestInfo> _published = new();
        private int _publishCalls;

        public int PublishCalls => Volatile.Read(ref _publishCalls);

        public Task<PullRequestInfo> PublishAsync(PullRequestRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _publishCalls);
            var info = new PullRequestInfo(
                Number: 77,
                Url: Url,
                BranchName: $"codenetix/{request.RunId}",
                CommitSha: "abcdef0123456789abcdef0123456789abcdef01",
                Title: request.Title,
                CreatedAt: DateTimeOffset.UtcNow);
            _published[request.RunId] = info;
            return Task.FromResult(info);
        }

        public Task<PullRequestInfo?> FindByRunAsync(Guid runId, CancellationToken cancellationToken) =>
            Task.FromResult(_published.TryGetValue(runId, out var pr) ? pr : null);
    }

    private sealed class BlockingCodingAgent : ICodingAgent
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<CodingResult> RunAsync(CodingInput input, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new CodingResult.Completed("ok", "fake-coder");
        }
    }

    private sealed class DelegateRunCompletionObserver(Action<Guid> onCompleted) : IRunCompletionObserver
    {
        public void OnOrchestrationCompleted(Guid runId) => onCompleted(runId);
    }

    private sealed class AlwaysPendingEventReader : IEventReader
    {
        public Task<EventStatusInfo?> FindAsync(Guid eventId, CancellationToken cancellationToken) =>
            Task.FromResult<EventStatusInfo?>(new EventStatusInfo(eventId, EventStatus.Accepted, null, null));
    }

    private sealed class FixedDispatchedEventReader(Guid runId) : IEventReader
    {
        public Task<EventStatusInfo?> FindAsync(Guid eventId, CancellationToken cancellationToken) =>
            Task.FromResult<EventStatusInfo?>(new EventStatusInfo(eventId, EventStatus.Accepted, null, runId));
    }

    private sealed class FixedTerminalRunReader(Guid runId) : IRunReader
    {
        public Task<IReadOnlyList<RunSummary>> ListRecentAsync(int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RunSummary>>([]);

        public Task<RunSummary?> FindAsync(string id, CancellationToken cancellationToken) =>
            Task.FromResult<RunSummary?>(new RunSummary(
                runId,
                runId.ToString("N")[..8],
                RunState.PullRequestCreated,
                null,
                null,
                Guid.CreateVersion7(),
                "github:o/r#12",
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow));

        public Task<IReadOnlyList<RunEvent>> GetEventsAsync(Guid id, long afterSeq, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RunEvent>>([]);

        public Task<Spec?> GetSpecAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult<Spec?>(null);

        public Task<TestReport?> GetTestReportAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult<TestReport?>(null);

        public Task<IReadOnlyList<RunVerdict>> GetVerdictsAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RunVerdict>>([]);
    }

    private sealed class ThrowingOnSubmitIngestion(IGitHubActionEventIngestion inner, string message) : IGitHubActionEventIngestion
    {
        public GitHubActionEventParse Parse(GitHubActionEventInput input) => inner.Parse(input);

        public Task<ManualEventResult> SubmitAsync(GitHubActionTrigger trigger, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(message);
    }

    private sealed class ServerCapturingHostedService(IServer server, Action<IServer> capture) : Microsoft.Extensions.Hosting.IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            capture(server);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
