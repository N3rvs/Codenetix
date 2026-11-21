using System.Collections.Concurrent;
using System.Formats.Tar;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CodeNetix.Agents;
using CodeNetix.Contracts;
using CodeNetix.GitHub;
using CodeNetix.Pipeline.Features.RunPipeline;
using CodeNetix.Sandbox;
using CodeNetix.Sandbox.Docker;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CodeNetix.Host.Tests;

/// <summary>
/// Pruebas de la Historia 3.1 (Revisores independientes emiten Veredictos):
/// paralelismo, aislamiento de entrada (FR-14), esquema <c>verdict.v1.json</c> (FR-15), fallback tras reintentos
/// o error de proveedor, proveedores heterogéneos, <c>ComputeDiffAsync</c> fuera del Sandbox (AD-8),
/// <c>ExportWorkspaceAsync</c>, persistencia en <c>run_stages</c>/<c>run_verdicts</c> y API <c>GET /api/runs/{id}</c>.
/// </summary>
public sealed class ReviewerTests : IDisposable
{
    private const string IssueUrl = "https://github.com/o/r/issues/77";
    private const string CoderSecretOutput = "CODER_INTERNAL_LOG_MUST_NEVER_LEAK_TO_REVIEWER";

    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    private readonly string _root;
    private readonly string _data;
    private readonly string _webRoot;

    public ReviewerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "codenetix-reviewer-" + Guid.NewGuid().ToString("N"));
        _data = Path.Combine(_root, "data");
        _webRoot = Path.Combine(_root, "wwwroot");
        Directory.CreateDirectory(_data);
        Directory.CreateDirectory(_webRoot);
        File.WriteAllText(Path.Combine(_webRoot, "index.html"), "<html></html>");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
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

    // --- 1. Paralelismo, run_stages, run_verdicts, VerdictRecorded y aislamiento (FR-14, AD-5, AD-11) ---

    [Fact]
    public async Task Active_reviewers_run_in_parallel_with_isolated_input_and_persist_stages_and_verdicts()
    {
        await using var factory = CreateFactory();

        // Sincronizar los 3 revisores con una barrera: si no corriesen en paralelo, expiraría.
        var barrier = new CountdownEvent(3);
        var capturedPrompts = new ConcurrentBag<(string System, string User)>();

        factory.Sandbox.OnSessionCreated = session =>
        {
            session.ExecHandler = cmd => cmd.Executable == "claude"
                ? new SandboxExecResult(0, $$"""{"result":"{{CoderSecretOutput}}"}""", string.Empty, TimedOut: false, Truncated: false)
                : new SandboxExecResult(0, string.Empty, string.Empty, TimedOut: false, Truncated: false);

            session.ExportWorkspaceHandler = async (dest, ct) =>
            {
                await WriteTarAsync(dest,
                [
                    ("workspace/README.md", "# modified in sandbox\n"),
                    ("workspace/src/NewFeature.cs", "public sealed class NewFeature { }\n"),
                ], ct);
            };
        };

        factory.ChatClient.Handler = async (call, ct) =>
        {
            var system = call.Messages.First(m => m.Role == ChatRole.System).Text;
            var user = call.Messages.First(m => m.Role == ChatRole.User).Text;
            if (!system.Contains("Reviewer of CodeNetix", StringComparison.Ordinal))
            {
                return FakeChatClient.ValidAnalysisJson;
            }

            capturedPrompts.Add((system, user));
            barrier.Signal();
            var allStarted = await Task.Run(() => barrier.Wait(TimeSpan.FromSeconds(5), ct), ct);
            Assert.True(allStarted, "Los 3 revisores deben ejecutarse en paralelo.");

            return FakeChatClient.ValidVerdictJson;
        };

        using var client = factory.CreateAuthorizedClient();
        var run = await RunToTerminalAsync(client);

        Assert.Equal("Failed", run.GetProperty("state").GetString());
        Assert.Equal("StageError", run.GetProperty("failureReason").GetString());
        Assert.Equal("Creación de PR no disponible", run.GetProperty("failureDetail").GetString());

        // Aislamiento (FR-14, AD-5): ningún prompt de Revisor contiene la salida del Programador,
        // y todos contienen Spec, Diff y TestReport.
        Assert.Equal(3, capturedPrompts.Count);
        foreach (var (_, user) in capturedPrompts)
        {
            Assert.DoesNotContain(CoderSecretOutput, user, StringComparison.Ordinal);
            Assert.Contains("<untrusted-data label=\"spec\"", user, StringComparison.Ordinal);
            Assert.Contains("<untrusted-data label=\"test-report\"", user, StringComparison.Ordinal);
            Assert.Contains("<untrusted-data label=\"unified-diff\"", user, StringComparison.Ordinal);
            Assert.Contains("NewFeature.cs", user, StringComparison.Ordinal);
        }

        // API devuelve los 3 veredictos ordenados por ronda/revisor.
        var verdicts = run.GetProperty("verdicts").EnumerateArray().ToList();
        Assert.Equal(3, verdicts.Count);
        Assert.Equal(["security", "quality", "tests"], verdicts.Select(v => v.GetProperty("reviewer").GetString()));
        Assert.All(verdicts, v =>
        {
            Assert.Equal(1, v.GetProperty("round").GetInt32());
            Assert.Equal("approved", v.GetProperty("decision").GetString());
            Assert.NotEqual(Guid.Empty, v.GetProperty("stageId").GetGuid());
        });

        // 3 eventos VerdictRecorded con stageId coincidente con su etapa.
        var verdictEvents = run.GetProperty("events").EnumerateArray()
            .Where(e => e.GetProperty("type").GetString() == "VerdictRecorded")
            .ToList();
        Assert.Equal(3, verdictEvents.Count);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(verdicts[i].GetProperty("stageId").GetGuid(), verdictEvents[i].GetProperty("stageId").GetGuid());
            Assert.Equal(1, verdictEvents[i].GetProperty("round").GetInt32());
            Assert.Equal(verdicts[i].GetProperty("reviewer").GetString(), verdictEvents[i].GetProperty("reviewer").GetString());
            Assert.Equal("approved", verdictEvents[i].GetProperty("decision").GetString());
            Assert.Equal(0, verdictEvents[i].GetProperty("findingCount").GetInt32());
        }

        // Persistencia en run_stages (3 filas Review, ronda 1) y run_verdicts (3 filas) filtrando por run_id.
        var runId = run.GetProperty("runId").GetGuid();
        var runIdParam = runId.ToString("D").ToUpperInvariant();
        await using var connection = await OpenReadOnlyAsync();
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM run_stages WHERE run_id = $id AND kind = 'Review' AND round = 1 AND completed_at IS NOT NULL;";
            cmd.Parameters.AddWithValue("$id", runIdParam);
            var stageCount = Convert.ToInt32(await cmd.ExecuteScalarAsync(Ct));
            Assert.Equal(3, stageCount);
        }

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM run_verdicts WHERE run_id = $id AND round = 1;";
            cmd.Parameters.AddWithValue("$id", runIdParam);
            var verdictCount = Convert.ToInt32(await cmd.ExecuteScalarAsync(Ct));
            Assert.Equal(3, verdictCount);
        }
    }

    // --- 2. Hallazgos estructurados válidos (FR-15) ---

    [Fact]
    public async Task Structured_findings_are_validated_persisted_and_exposed_in_API()
    {
        var settings = new Dictionary<string, string?>(CodeNetixFactory.CompleteSettings)
        {
            ["CODENETIX:Limits:MaxReviewRounds"] = "1",
        };
        await using var factory = CreateFactory(settings);
        factory.ChatClient.Handler = (call, _) =>
        {
            var system = call.Messages.First(m => m.Role == ChatRole.System).Text;
            if (!system.Contains("Reviewer of CodeNetix", StringComparison.Ordinal))
            {
                return Task.FromResult(FakeChatClient.ValidAnalysisJson);
            }

            if (system.Contains("Security Reviewer", StringComparison.Ordinal))
            {
                return Task.FromResult(
                    """
                    {
                      "decision": "changes_requested",
                      "summary": "Se detectó concatenación directa en consulta SQL.",
                      "findings": [
                        {
                          "severity": "high",
                          "file": "src/ProfileController.cs",
                          "line": 42,
                          "message": "Usa parámetros SQL en lugar de interpolación de cadenas."
                        }
                      ]
                    }
                    """);
            }

            return Task.FromResult(FakeChatClient.ValidVerdictJson);
        };

        using var client = factory.CreateAuthorizedClient();
        var run = await RunToTerminalAsync(client);

        var security = run.GetProperty("verdicts").EnumerateArray()
            .Single(v => v.GetProperty("reviewer").GetString() == "security");
        Assert.Equal("changes_requested", security.GetProperty("decision").GetString());
        Assert.Contains("SQL", security.GetProperty("summary").GetString(), StringComparison.Ordinal);

        var finding = Assert.Single(security.GetProperty("findings").EnumerateArray());
        Assert.Equal("high", finding.GetProperty("severity").GetString());
        Assert.Equal("src/ProfileController.cs", finding.GetProperty("file").GetString());
        Assert.Equal(42, finding.GetProperty("line").GetInt32());
        Assert.Contains("parámetros SQL", finding.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    // --- 3. Respuesta inválida tras agotar SchemaRetries → fallback ChangesRequested sin abortar a los demás ---

    [Fact]
    public async Task Invalid_verdict_after_retries_becomes_changes_requested_without_aborting_other_reviewers()
    {
        var settings = new Dictionary<string, string?>(CodeNetixFactory.CompleteSettings)
        {
            ["CODENETIX:Agents:SchemaRetries"] = "2",
            ["CODENETIX:Limits:MaxReviewRounds"] = "1",
        };
        await using var factory = CreateFactory(settings);

        var securityAttempts = 0;
        factory.ChatClient.Handler = (call, _) =>
        {
            var system = call.Messages.First(m => m.Role == ChatRole.System).Text;
            if (!system.Contains("Reviewer of CodeNetix", StringComparison.Ordinal))
            {
                return Task.FromResult(FakeChatClient.ValidAnalysisJson);
            }

            if (system.Contains("Security Reviewer", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref securityAttempts);
                return Task.FromResult("not valid json at all");
            }

            return Task.FromResult(FakeChatClient.ValidVerdictJson);
        };

        using var client = factory.CreateAuthorizedClient();
        var run = await RunToTerminalAsync(client);

        // 1 intento inicial + 2 reintentos = 3 intentos para Security.
        Assert.Equal(3, Volatile.Read(ref securityAttempts));

        var verdicts = run.GetProperty("verdicts").EnumerateArray().ToList();
        Assert.Equal(3, verdicts.Count);

        var security = verdicts.Single(v => v.GetProperty("reviewer").GetString() == "security");
        Assert.Equal("changes_requested", security.GetProperty("decision").GetString());
        Assert.Contains("Veredicto inválido", security.GetProperty("summary").GetString(), StringComparison.Ordinal);
        Assert.Equal(3, security.GetProperty("attempts").GetInt32());

        // Los otros dos Revisores completan normalmente como approved.
        Assert.Equal("approved", verdicts.Single(v => v.GetProperty("reviewer").GetString() == "quality").GetProperty("decision").GetString());
        Assert.Equal("approved", verdicts.Single(v => v.GetProperty("reviewer").GetString() == "tests").GetProperty("decision").GetString());

        // Se emitieron 3 eventos ReviewerOutputRejected para Security con su stageId.
        var rejectedEvents = run.GetProperty("events").EnumerateArray()
            .Where(e => e.GetProperty("type").GetString() == "ReviewerOutputRejected")
            .ToList();
        Assert.Equal(3, rejectedEvents.Count);
        var securityStageId = security.GetProperty("stageId").GetGuid();
        Assert.All(rejectedEvents, e =>
        {
            Assert.Equal(securityStageId, e.GetProperty("stageId").GetGuid());
            Assert.Equal("security", e.GetProperty("reviewer").GetString());
        });
    }

    // --- 4. Error de proveedor en un Revisor → ChangesRequested sin abortar a los demás ---

    [Fact]
    public async Task Provider_error_in_one_reviewer_becomes_changes_requested_while_others_succeed()
    {
        var settings = new Dictionary<string, string?>(CodeNetixFactory.CompleteSettings)
        {
            ["CODENETIX:Limits:MaxReviewRounds"] = "1",
        };
        await using var factory = CreateFactory(settings);

        factory.ChatClient.Handler = (call, _) =>
        {
            var system = call.Messages.First(m => m.Role == ChatRole.System).Text;
            if (!system.Contains("Reviewer of CodeNetix", StringComparison.Ordinal))
            {
                return Task.FromResult(FakeChatClient.ValidAnalysisJson);
            }

            if (system.Contains("Tests Reviewer", StringComparison.Ordinal))
            {
                throw new HttpRequestException("Upstream 503 Service Unavailable", null, HttpStatusCode.ServiceUnavailable);
            }

            return Task.FromResult(FakeChatClient.ValidVerdictJson);
        };

        using var client = factory.CreateAuthorizedClient();
        var run = await RunToTerminalAsync(client);

        var verdicts = run.GetProperty("verdicts").EnumerateArray().ToList();
        Assert.Equal(3, verdicts.Count);

        var tests = verdicts.Single(v => v.GetProperty("reviewer").GetString() == "tests");
        Assert.Equal("changes_requested", tests.GetProperty("decision").GetString());
        Assert.Contains("Error del Proveedor", tests.GetProperty("summary").GetString(), StringComparison.Ordinal);
        Assert.Contains("503", tests.GetProperty("summary").GetString(), StringComparison.Ordinal);

        Assert.Equal("approved", verdicts.Single(v => v.GetProperty("reviewer").GetString() == "security").GetProperty("decision").GetString());
        Assert.Equal("approved", verdicts.Single(v => v.GetProperty("reviewer").GetString() == "quality").GetProperty("decision").GetString());
    }

    // --- 5. Proveedores heterogéneos por Revisor (StructuredOutput=false vs true) ---

    [Fact]
    public async Task Heterogeneous_providers_per_reviewer_respect_endpoint_model_and_structured_output_fallback()
    {
        var settings = new Dictionary<string, string?>(CodeNetixFactory.CompleteSettings)
        {
            ["CODENETIX:Providers:LocalOllama:ApiKey"] = "ollama-local-key",
            ["CODENETIX:Providers:LocalOllama:BaseUrl"] = "http://127.0.0.1:11434/v1/",
            ["CODENETIX:Providers:LocalOllama:StructuredOutput"] = "false",
            ["CODENETIX:Agents:Reviewers:Security:Provider"] = "LocalOllama",
            ["CODENETIX:Agents:Reviewers:Security:Model"] = "qwen2.5-coder:32b",
            ["CODENETIX:Agents:Reviewers:Quality:Provider"] = "Anthropic",
            ["CODENETIX:Agents:Reviewers:Quality:Model"] = "claude-opus-4",
            // Tests no tiene override → usa el general de Reviewers o Analyst (Anthropic / claude-sonnet-5-5).
        };

        await using var factory = CreateFactory(settings);
        var callsByModel = new ConcurrentDictionary<string, FakeChatClient.Call>();

        factory.ChatClient.Handler = (call, _) =>
        {
            var system = call.Messages.First(m => m.Role == ChatRole.System).Text;
            if (!system.Contains("Reviewer of CodeNetix", StringComparison.Ordinal))
            {
                return Task.FromResult(FakeChatClient.ValidAnalysisJson);
            }

            callsByModel[call.Options?.ModelId ?? string.Empty] = call;
            return Task.FromResult(FakeChatClient.ValidVerdictJson);
        };

        using var client = factory.CreateAuthorizedClient();
        var run = await RunToTerminalAsync(client);

        var verdicts = run.GetProperty("verdicts").EnumerateArray().ToList();
        Assert.Equal("qwen2.5-coder:32b", verdicts.Single(v => v.GetProperty("reviewer").GetString() == "security").GetProperty("model").GetString());
        Assert.Equal("claude-opus-4", verdicts.Single(v => v.GetProperty("reviewer").GetString() == "quality").GetProperty("model").GetString());
        Assert.Equal("claude-sonnet-5-5", verdicts.Single(v => v.GetProperty("reviewer").GetString() == "tests").GetProperty("model").GetString());

        // Security usa StructuredOutput=false: sin response_format y con el esquema incrustado en el prompt de sistema.
        var securityCall = callsByModel["qwen2.5-coder:32b"];
        Assert.Null(securityCall.Options?.ResponseFormat);
        Assert.Contains("JSON Schema", securityCall.Messages.First(m => m.Role == ChatRole.System).Text, StringComparison.Ordinal);

        // Quality usa Anthropic (StructuredOutput=true): con response_format json_schema.
        var qualityCall = callsByModel["claude-opus-4"];
        Assert.IsType<ChatResponseFormatJson>(qualityCall.Options?.ResponseFormat);

        // La fábrica registró el proveedor LocalOllama para Security y Anthropic para Quality.
        Assert.Contains(factory.ChatClientFactory.Created, c => c.Endpoint.Provider == "LocalOllama" && c.Model == "qwen2.5-coder:32b");
        Assert.Contains(factory.ChatClientFactory.Created, c => c.Endpoint.Provider == "Anthropic" && c.Model == "claude-opus-4");
    }

    // --- 6. ComputeDiffAsync (archivos nuevos, modificados, exclusiones .git/bin/obj) y ExportWorkspaceAsync ---

    [Fact]
    public async Task ComputeDiffAsync_includes_new_and_modified_files_and_excludes_git_bin_obj()
    {
        var workspacesDir = Path.Combine(_root, "ws-diff-test");
        var workspace = new RepositoryWorkspace(
            Options.Create(new GitHubOptions
            {
                CloneBaseUrl = LocalGitRepos.Default.BaseUrl,
                WorkspacesDirectory = workspacesDir,
            }),
            NullLogger<RepositoryWorkspace>.Instance);

        var runId = Guid.CreateVersion7();
        var prepared = await workspace.PrepareAsync(runId, "o", "r", Ct);
        var gitConfigBefore = await File.ReadAllTextAsync(Path.Combine(prepared.WorkspacePath, ".git", "config"), Ct);

        await using var tar = new MemoryStream();
        await WriteTarAsync(tar,
        [
            ("workspace/README.md", "# updated readme\n"),
            ("workspace/src/AddedFile.cs", "namespace Demo;\npublic class AddedFile { }\n"),
            ("workspace/.git/config", "MALICIOUS_GIT_CONFIG"),
            ("workspace/bin/Debug/app.dll", "binary-artifact"),
            ("workspace/obj/Debug/temp.cache", "obj-artifact"),
        ], Ct);
        tar.Position = 0;

        var diff = await workspace.ComputeDiffAsync(runId, tar, prepared.BaseSha, Ct);

        Assert.Contains("README.md", diff.ChangedFiles);
        Assert.Contains("src/AddedFile.cs", diff.ChangedFiles);
        Assert.DoesNotContain(diff.ChangedFiles, f => f.Contains(".git", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(diff.ChangedFiles, f => f.Contains("bin/", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(diff.ChangedFiles, f => f.Contains("obj/", StringComparison.OrdinalIgnoreCase));

        Assert.Contains("+# updated readme", diff.UnifiedDiff, StringComparison.Ordinal);
        Assert.Contains("new file mode", diff.UnifiedDiff, StringComparison.Ordinal);
        Assert.Contains("+public class AddedFile { }", diff.UnifiedDiff, StringComparison.Ordinal);

        // .git/config del clon del host jamás fue sobrescrito por el tar del Sandbox.
        var gitConfigAfter = await File.ReadAllTextAsync(Path.Combine(prepared.WorkspacePath, ".git", "config"), Ct);
        Assert.Equal(gitConfigBefore, gitConfigAfter);
    }

    [Fact]
    public async Task ComputeDiffAsync_rejects_path_traversal_in_sandbox_tar()
    {
        var workspacesDir = Path.Combine(_root, "ws-traversal-test");
        var workspace = new RepositoryWorkspace(
            Options.Create(new GitHubOptions
            {
                CloneBaseUrl = LocalGitRepos.Default.BaseUrl,
                WorkspacesDirectory = workspacesDir,
            }),
            NullLogger<RepositoryWorkspace>.Instance);

        var runId = Guid.CreateVersion7();
        var prepared = await workspace.PrepareAsync(runId, "o", "r", Ct);

        await using var tar = new MemoryStream();
        await WriteTarAsync(tar, [("workspace/../escape.txt", "evil")], Ct);
        tar.Position = 0;

        await Assert.ThrowsAsync<RepositoryPreparationException>(() =>
            workspace.ComputeDiffAsync(runId, tar, prepared.BaseSha, Ct));
    }

    [Fact]
    public async Task SandboxSession_ExportWorkspaceAsync_fetches_workspace_tar_from_docker_API()
    {
        await using var engine = await FakeDockerEngine.StartAsync();
        await using var expectedTar = new MemoryStream();
        await WriteTarAsync(expectedTar, [("workspace/hello.txt", "world")], Ct);
        engine.GetArchiveBytes = expectedTar.ToArray();

        using var docker = new DockerClient(DockerEndpoint.Parse(engine.Endpoint));
        await using var session = new SandboxSession(
            docker,
            Guid.CreateVersion7(),
            FakeDockerEngine.ContainerId,
            "codenetix-sandbox:latest",
            "codenetix-ws-test",
            TimeSpan.FromMinutes(10),
            NullLogger<SandboxSession>.Instance,
            Ct);

        await using var exported = new MemoryStream();
        await session.ExportWorkspaceAsync(exported, Ct);

        Assert.Equal(engine.GetArchiveBytes, exported.ToArray());
        Assert.Contains(engine.Requests, r =>
            r.Method == "GET"
            && r.Route == $"/containers/{FakeDockerEngine.ContainerId}/archive"
            && r.Query.Contains("path=%2Fworkspace", StringComparison.OrdinalIgnoreCase));
    }

    // --- 7. Consulta por API antes de la revisión devuelve lista vacía de verdicts ---

    [Fact]
    public async Task GetRun_returns_empty_verdicts_list_when_run_fails_before_review()
    {
        await using var factory = CreateFactory();
        factory.ChatClient.Respond(FakeChatClient.NotAutomatableAnswer("out_of_scope", "No es un cambio de código .NET."));

        using var client = factory.CreateAuthorizedClient();
        var run = await RunToTerminalAsync(client);

        Assert.Equal("Failed", run.GetProperty("state").GetString());
        Assert.Equal("NotAutomatable", run.GetProperty("failureReason").GetString());
        Assert.Equal(JsonValueKind.Array, run.GetProperty("verdicts").ValueKind);
        Assert.Empty(run.GetProperty("verdicts").EnumerateArray());
    }

    // --- 8. Validación de configuración de Revisores, herencia de Analyst.Model y override solo de modelo ---

    [Theory]
    [InlineData("CODENETIX:Agents:Reviewers:Provider", "UnknownProvider", "CODENETIX__Agents__Reviewers__Provider")]
    [InlineData("CODENETIX:Agents:Reviewers:Security:Provider", "UnknownProvider", "CODENETIX__Agents__Reviewers__Security__Provider")]
    public async Task Invalid_reviewer_provider_puts_health_in_Diagnostic_mode_with_expected_variable(string key, string value, string expectedVar)
    {
        var settings = new Dictionary<string, string?>(CodeNetixFactory.CompleteSettings)
        {
            [key] = value,
        };
        await using var factory = CreateFactory(settings);
        using var client = factory.CreateAuthorizedClient();

        using var response = await client.GetAsync("/api/health", Ct);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("Diagnostic", json.RootElement.GetProperty("status").GetString());
        var reasons = json.RootElement.GetProperty("reasons").EnumerateArray().Select(r => r.GetString()!).ToList();
        Assert.Contains(reasons, r => r.Contains(expectedVar, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reviewers_inherit_custom_analyst_model_and_model_only_specialty_override_inherits_provider()
    {
        var settings = new Dictionary<string, string?>(CodeNetixFactory.CompleteSettings)
        {
            ["CODENETIX:Agents:Analyst:Model"] = "claude-custom-analyst",
            ["CODENETIX:Agents:Reviewers:Quality:Model"] = "claude-quality-only",
        };
        await using var factory = CreateFactory(settings);
        using var client = factory.CreateAuthorizedClient();

        var run = await RunToTerminalAsync(client);

        var verdicts = run.GetProperty("verdicts").EnumerateArray().ToList();
        Assert.Equal("claude-custom-analyst", verdicts.Single(v => v.GetProperty("reviewer").GetString() == "security").GetProperty("model").GetString());
        Assert.Equal("claude-quality-only", verdicts.Single(v => v.GetProperty("reviewer").GetString() == "quality").GetProperty("model").GetString());
        Assert.Equal("claude-custom-analyst", verdicts.Single(v => v.GetProperty("reviewer").GetString() == "tests").GetProperty("model").GetString());
    }

    // --- 9. Fallo en exportación o diff impide transición a Reviewing y llamadas a Revisores ---

    [Fact]
    public async Task Export_or_diff_failure_fails_StageError_without_entering_Reviewing_or_calling_reviewers()
    {
        await using var factory = CreateFactory();
        factory.Sandbox.OnSessionCreated = session =>
        {
            session.ExportWorkspaceHandler = (_, _) => throw new SandboxUnavailableException("docker archive unavailable");
        };

        using var client = factory.CreateAuthorizedClient();
        var run = await RunToTerminalAsync(client);

        Assert.Equal("Failed", run.GetProperty("state").GetString());
        Assert.Equal("StageError", run.GetProperty("failureReason").GetString());
        Assert.StartsWith(RunOrchestrator.DiffFailedPrefix, run.GetProperty("failureDetail").GetString(), StringComparison.Ordinal);
        Assert.Empty(run.GetProperty("verdicts").EnumerateArray());
        Assert.DoesNotContain(run.GetProperty("events").EnumerateArray(), e =>
            e.GetProperty("type").GetString() == "StateChanged" && e.GetProperty("to").GetString() == "Reviewing");
        // Solo la llamada del Analista (ningún Revisor fue invocado).
        Assert.Single(factory.ChatClient.Calls);
    }

    // --- 10. Validación semántica VerdictRules con reintento hasta éxito ---

    [Fact]
    public async Task Semantic_verdict_rule_violation_is_rejected_and_retried_to_success()
    {
        await using var factory = CreateFactory();
        var securityAttempts = 0;

        factory.ChatClient.Handler = (call, _) =>
        {
            var system = call.Messages.First(m => m.Role == ChatRole.System).Text;
            if (!system.Contains("Reviewer of CodeNetix", StringComparison.Ordinal))
            {
                return Task.FromResult(FakeChatClient.ValidAnalysisJson);
            }

            if (system.Contains("Security Reviewer", StringComparison.Ordinal))
            {
                var attempt = Interlocked.Increment(ref securityAttempts);
                if (attempt == 1)
                {
                    // JSON válido según esquema, pero viola VerdictRules (path traversal en findings[0].file).
                    return Task.FromResult(
                        """
                        {
                          "decision": "changes_requested",
                          "summary": "Hallazgo con ruta inválida.",
                          "findings": [
                            { "severity": "high", "file": "../escape.cs", "line": 10, "message": "Ruta peligrosa." }
                          ]
                        }
                        """);
                }
            }

            return Task.FromResult(FakeChatClient.ValidVerdictJson);
        };

        using var client = factory.CreateAuthorizedClient();
        var run = await RunToTerminalAsync(client);

        var security = run.GetProperty("verdicts").EnumerateArray()
            .Single(v => v.GetProperty("reviewer").GetString() == "security");
        Assert.Equal("approved", security.GetProperty("decision").GetString());
        Assert.Equal(2, security.GetProperty("attempts").GetInt32());

        var rejected = Assert.Single(run.GetProperty("events").EnumerateArray(), e =>
            e.GetProperty("type").GetString() == "ReviewerOutputRejected");
        Assert.Equal("security", rejected.GetProperty("reviewer").GetString());
        Assert.Equal(1, rejected.GetProperty("attempt").GetInt32());
        Assert.Contains("findings[0].file", rejected.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    // --- 11. Redacción de secretos en run_verdicts y GET /api/runs/{id} ---

    [Fact]
    public async Task Secrets_in_verdict_summary_finding_and_provider_error_are_redacted()
    {
        var secretKey = CodeNetixFactory.CompleteSettings["CODENETIX:Providers:Anthropic:ApiKey"]!;
        await using var factory = CreateFactory();

        factory.ChatClient.Handler = (call, _) =>
        {
            var system = call.Messages.First(m => m.Role == ChatRole.System).Text;
            if (!system.Contains("Reviewer of CodeNetix", StringComparison.Ordinal))
            {
                return Task.FromResult(FakeChatClient.ValidAnalysisJson);
            }

            if (system.Contains("Security Reviewer", StringComparison.Ordinal))
            {
                return Task.FromResult(
                    $$"""
                    {
                      "decision": "changes_requested",
                      "summary": "Se expuso la clave {{secretKey}} en el código.",
                      "findings": [
                        {
                          "severity": "critical",
                          "file": "src/{{secretKey}}.cs",
                          "line": 5,
                          "message": "No hardcodees {{secretKey}}."
                        }
                      ]
                    }
                    """);
            }

            if (system.Contains("Tests Reviewer", StringComparison.Ordinal))
            {
                throw new HttpRequestException($"upstream rejected {secretKey}", null, HttpStatusCode.Unauthorized);
            }

            return Task.FromResult(FakeChatClient.ValidVerdictJson);
        };

        using var client = factory.CreateAuthorizedClient();
        var run = await RunToTerminalAsync(client);

        Assert.DoesNotContain(secretKey, run.GetRawText(), StringComparison.Ordinal);

        var runIdParam = run.GetProperty("runId").GetGuid().ToString("D").ToUpperInvariant();
        await using var connection = await OpenReadOnlyAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT group_concat(summary || ' ' || findings_json, ' ') FROM run_verdicts WHERE run_id = $id;";
        cmd.Parameters.AddWithValue("$id", runIdParam);
        var stored = (string)(await cmd.ExecuteScalarAsync(Ct))!;
        Assert.DoesNotContain(secretKey, stored, StringComparison.Ordinal);
    }

    // --- 12. codenetix.yml restringe subconjunto de revisores y models.reviewers ---

    [Fact]
    public async Task Repo_config_restricts_active_reviewers_and_overrides_reviewers_model()
    {
        using var repos = new LocalGitRepos();
        repos.Create("acme", "subset", new Dictionary<string, string>
        {
            ["codenetix.yml"] = "version: 1\nreviewers:\n  - security\n  - quality\nmodels:\n  reviewers: claude-repo-reviewer\n",
            ["src/App.cs"] = "class App {}\n",
        });

        var settings = new Dictionary<string, string?>(CodeNetixFactory.CompleteSettings)
        {
            [CodeNetixFactory.CloneBaseUrlKey] = repos.BaseUrl,
        };
        await using var factory = CreateFactory(settings);
        using var client = factory.CreateAuthorizedClient();

        var run = await RunToTerminalAsync(client, "https://github.com/acme/subset/issues/77");

        var verdicts = run.GetProperty("verdicts").EnumerateArray().ToList();
        Assert.Equal(2, verdicts.Count);
        Assert.Equal(["security", "quality"], verdicts.Select(v => v.GetProperty("reviewer").GetString()));
        Assert.All(verdicts, v => Assert.Equal("claude-repo-reviewer", v.GetProperty("model").GetString()));
    }

    // --- 13. Exclusión Windows .git. rechazada como ruta no segura ---

    [Fact]
    public async Task ComputeDiffAsync_rejects_windows_trailing_dot_git_bypass()
    {
        var workspacesDir = Path.Combine(_root, "ws-gitdot-test");
        var workspace = new RepositoryWorkspace(
            Options.Create(new GitHubOptions
            {
                CloneBaseUrl = LocalGitRepos.Default.BaseUrl,
                WorkspacesDirectory = workspacesDir,
            }),
            NullLogger<RepositoryWorkspace>.Instance);

        var runId = Guid.CreateVersion7();
        var prepared = await workspace.PrepareAsync(runId, "o", "r", Ct);

        await using var tar = new MemoryStream();
        await WriteTarAsync(tar, [("workspace/.git./config", "MALICIOUS")], Ct);
        tar.Position = 0;

        await Assert.ThrowsAsync<RepositoryPreparationException>(() =>
            workspace.ComputeDiffAsync(runId, tar, prepared.BaseSha, Ct));
    }

    // --- Helpers ---

    private CodeNetixFactory CreateFactory(IReadOnlyDictionary<string, string?>? settings = null) =>
        new(_data, _webRoot, settings ?? CodeNetixFactory.CompleteSettings);

    private async Task<JsonElement> RunToTerminalAsync(HttpClient client, string issueUrl = IssueUrl)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/events/manual",
            new { url = issueUrl, requestId = Guid.NewGuid().ToString("N") },
            Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var created = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var eventId = created.RootElement.GetProperty("eventId").GetGuid();

        var stopAt = DateTimeOffset.UtcNow + Deadline;
        Guid? runId = null;
        while (DateTimeOffset.UtcNow < stopAt && runId is null)
        {
            using var evRes = await client.GetAsync($"/api/events/{eventId}", Ct);
            using var evDoc = JsonDocument.Parse(await evRes.Content.ReadAsStringAsync(Ct));
            if (evDoc.RootElement.TryGetProperty("runId", out var r) && r.ValueKind == JsonValueKind.String)
            {
                runId = r.GetGuid();
                break;
            }

            await Task.Delay(25, Ct);
        }

        Assert.NotNull(runId);

        while (DateTimeOffset.UtcNow < stopAt)
        {
            using var runRes = await client.GetAsync($"/api/runs/{runId}", Ct);
            using var runDoc = JsonDocument.Parse(await runRes.Content.ReadAsStringAsync(Ct));
            var state = runDoc.RootElement.GetProperty("state").GetString();
            if (state is "Failed" or "PullRequestCreated" or "Cancelled")
            {
                return runDoc.RootElement.Clone();
            }

            await Task.Delay(25, Ct);
        }

        throw new TimeoutException($"La Ejecución {runId} no alcanzó estado terminal en {Deadline}.");
    }

    private async Task<SqliteConnection> OpenReadOnlyAsync()
    {
        var dbPath = Path.Combine(_data, "codenetix.db");
        var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        await conn.OpenAsync(Ct);
        return conn;
    }

    private static async Task WriteTarAsync(Stream destination, IReadOnlyList<(string Path, string Content)> files, CancellationToken ct)
    {
        await using var writer = new TarWriter(destination, TarEntryFormat.Pax, leaveOpen: true);
        foreach (var (path, content) in files)
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            var entry = new PaxTarEntry(TarEntryType.RegularFile, path)
            {
                DataStream = new MemoryStream(bytes),
            };
            await writer.WriteEntryAsync(entry, ct);
        }
    }
}
