using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Formats.Tar;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CodeNetix.Contracts;
using CodeNetix.Pipeline.Features.RunPipeline;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace CodeNetix.Host.Tests;

/// <summary>
/// Pruebas de la Historia 3.2 (Rondas de revisión, FR-16, AD-16):
/// unanimidad en Ronda 1, iteración <c>Reviewing → Coding</c> con feedback estructurado y re-ejecución de tests,
/// precedencia de <c>Rejected</c> (<c>ReviewRejected</c>) en Ronda 1 y 2, agotamiento de rondas
/// (<c>ReviewRoundsExhausted</c>) con límite por defecto y por <c>codenetix.yml</c>, cupo independiente de
/// <c>MaxCodingAttempts</c> por ronda, línea base única y aislamiento de Revisores.
/// </summary>
public sealed class ReviewRoundsTests : IDisposable
{
    private const string IssueUrl = "https://github.com/o/r/issues/88";
    private const string CoderSecretOutput = "CODER_INTERNAL_OUTPUT_NEVER_LEAKS_TO_REVIEWERS";

    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    private readonly string _root;
    private readonly string _data;
    private readonly string _webRoot;

    public ReviewRoundsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "codenetix-review-rounds-" + Guid.NewGuid().ToString("N"));
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

    // --- 1. Unanimidad en Ronda 1 ---

    [Fact]
    public async Task Unanimous_approval_in_round_1_finishes_at_PR_bridge_without_starting_round_2()
    {
        var coder = new RecordingCodingAgent();
        var testRuns = 0;

        await using var baseFactory = CreateFactory();
        baseFactory.TestRunner.RunHandler = (_, _) =>
        {
            Interlocked.Increment(ref testRuns);
            return PassingReport();
        };

        await using var factory = baseFactory.WithWebHostBuilder(b =>
            b.ConfigureTestServices(s => s.AddSingleton<ICodingAgent>(coder)));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", CodeNetixFactory.TestAccessToken);

        var run = await RunToTerminalAsync(client);

        Assert.Equal("Failed", run.GetProperty("state").GetString());
        Assert.Equal("StageError", run.GetProperty("failureReason").GetString());
        Assert.Equal("Creación de PR no disponible", run.GetProperty("failureDetail").GetString());

        Assert.Single(coder.Inputs);
        Assert.Equal(1, Volatile.Read(ref testRuns));

        var verdicts = run.GetProperty("verdicts").EnumerateArray().ToList();
        Assert.Equal(3, verdicts.Count);
        Assert.All(verdicts, v =>
        {
            Assert.Equal(1, v.GetProperty("round").GetInt32());
            Assert.Equal("approved", v.GetProperty("decision").GetString());
        });

        Assert.DoesNotContain(run.GetProperty("events").EnumerateArray(), e =>
            e.GetProperty("type").GetString() == "StateChanged"
            && e.GetProperty("from").GetString() == "Reviewing"
            && e.GetProperty("to").GetString() == "Coding");
    }

    // --- 2. ChangesRequested en Ronda 1 → Approved en Ronda 2 ---

    [Fact]
    public async Task Changes_requested_in_round_1_transitions_to_coding_with_redacted_feedback_reuses_baseline_and_approves_in_round_2()
    {
        var secretKey = CodeNetixFactory.CompleteSettings["CODENETIX:Providers:Anthropic:ApiKey"]!;
        var coder = new RecordingCodingAgent();
        var expectedBaseline = new TestBaseline(new HashSet<string>(["Tests.Existing.A"], StringComparer.Ordinal));
        var observedBaselines = new ConcurrentQueue<TestBaseline>();
        var exportRound = 0;
        var reviewerPromptsRound2 = new ConcurrentBag<string>();
        var securityCalls = 0;

        await using var baseFactory = CreateFactory();
        baseFactory.TestRunner.BaselineResult = expectedBaseline;
        baseFactory.TestRunner.RunHandler = (_, baseline) =>
        {
            observedBaselines.Enqueue(baseline);
            return PassingReport();
        };

        baseFactory.Sandbox.OnSessionCreated = session =>
        {
            session.ExportWorkspaceHandler = async (dest, ct) =>
            {
                var current = Interlocked.Increment(ref exportRound);
                var content = current == 1
                    ? "public class ProfileController { /* v1 unsafe */ }\n"
                    : "public class ProfileController { /* v2 parameterized */ }\n";
                await WriteTarAsync(dest, [("workspace/src/ProfileController.cs", content)], ct);
            };
        };

        baseFactory.ChatClient.Handler = (call, _) =>
        {
            var system = call.Messages.First(m => m.Role == ChatRole.System).Text;
            var user = call.Messages.First(m => m.Role == ChatRole.User).Text;
            if (!system.Contains("Reviewer of CodeNetix", StringComparison.Ordinal))
            {
                return Task.FromResult(FakeChatClient.ValidAnalysisJson);
            }

            if (system.Contains("Security Reviewer", StringComparison.Ordinal))
            {
                var n = Interlocked.Increment(ref securityCalls);
                if (n == 1)
                {
                    return Task.FromResult(
                        $$"""
                        {
                          "decision": "changes_requested",
                          "summary": "Concatenación SQL detectada con clave {{secretKey}}.",
                          "findings": [
                            {
                              "severity": "high",
                              "file": "src/ProfileController.cs",
                              "line": 42,
                              "message": "Usa parámetros SQL y no expongas {{secretKey}}."
                            }
                          ]
                        }
                        """);
                }

                reviewerPromptsRound2.Add(user);
                return Task.FromResult(FakeChatClient.ValidVerdictJson);
            }

            if (user.Contains("v2 parameterized", StringComparison.Ordinal))
            {
                reviewerPromptsRound2.Add(user);
            }

            return Task.FromResult(FakeChatClient.ValidVerdictJson);
        };

        await using var factory = baseFactory.WithWebHostBuilder(b =>
            b.ConfigureTestServices(s => s.AddSingleton<ICodingAgent>(coder)));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", CodeNetixFactory.TestAccessToken);

        var run = await RunToTerminalAsync(client);

        Assert.Equal("Failed", run.GetProperty("state").GetString());
        Assert.Equal("StageError", run.GetProperty("failureReason").GetString());
        Assert.Equal("Creación de PR no disponible", run.GetProperty("failureDetail").GetString());

        // Transición Reviewing → Coding registrada.
        Assert.Contains(run.GetProperty("events").EnumerateArray(), e =>
            e.GetProperty("type").GetString() == "StateChanged"
            && e.GetProperty("from").GetString() == "Reviewing"
            && e.GetProperty("to").GetString() == "Coding");

        // Programador llamado en Ronda 1 (sin feedback) y en Ronda 2 (con hallazgos redactados).
        Assert.Equal(2, coder.Inputs.Count);
        Assert.Null(coder.Inputs[0].Feedback);
        var round2Feedback = coder.Inputs[1].Feedback;
        Assert.NotNull(round2Feedback);
        Assert.Contains("[security]", round2Feedback, StringComparison.Ordinal);
        Assert.Contains("Concatenación SQL detectada", round2Feedback, StringComparison.Ordinal);
        Assert.Contains("(high)", round2Feedback, StringComparison.Ordinal);
        Assert.Contains("src/ProfileController.cs:42", round2Feedback, StringComparison.Ordinal);
        Assert.Contains("Usa parámetros SQL", round2Feedback, StringComparison.Ordinal);
        Assert.DoesNotContain(secretKey, round2Feedback, StringComparison.Ordinal);
        // Los Revisores que aprobaron en Ronda 1 no aparecen en el feedback de cambios solicitados.
        Assert.DoesNotContain("[quality]", round2Feedback, StringComparison.Ordinal);
        Assert.DoesNotContain("[tests]", round2Feedback, StringComparison.Ordinal);

        // Línea base capturada una sola vez y reutilizada en las 2 ejecuciones de tests.
        Assert.Equal(1, baseFactory.TestRunner.BaselineCalls);
        Assert.Equal(2, observedBaselines.Count);
        Assert.All(observedBaselines, b => Assert.Equal(expectedBaseline.ExistingTests, b.ExistingTests));

        // Aislamiento intacto en Ronda 2: los Revisores ven el nuevo diff y nunca la salida del Programador.
        Assert.Equal(3, reviewerPromptsRound2.Count);
        Assert.All(reviewerPromptsRound2, p =>
        {
            Assert.Contains("v2 parameterized", p, StringComparison.Ordinal);
            Assert.DoesNotContain(CoderSecretOutput, p, StringComparison.Ordinal);
        });

        // 6 veredictos ordenados (3 de Ronda 1, 3 de Ronda 2) expuestos en la API.
        var verdicts = run.GetProperty("verdicts").EnumerateArray().ToList();
        Assert.Equal(6, verdicts.Count);
        Assert.Equal([1, 1, 1, 2, 2, 2], verdicts.Select(v => v.GetProperty("round").GetInt32()));
        Assert.Equal(
            ["security", "quality", "tests", "security", "quality", "tests"],
            verdicts.Select(v => v.GetProperty("reviewer").GetString()));
        Assert.Equal("changes_requested", verdicts[0].GetProperty("decision").GetString());
        Assert.All(verdicts.Skip(3), v => Assert.Equal("approved", v.GetProperty("decision").GetString()));

        // Persistencia en run_stages: 6 etapas Review distintas (3 de Ronda 1 y 3 de Ronda 2, todas completadas),
        // y los veredictos de Ronda 2 referencian los stageId de Ronda 2.
        var runIdParam = run.GetProperty("runId").GetGuid().ToString("D").ToUpperInvariant();
        await using var connection = await OpenReadOnlyAsync();
        var round1StageIds = new HashSet<Guid>();
        var round2StageIds = new HashSet<Guid>();
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT id, round, completed_at FROM run_stages WHERE run_id = $id AND kind = 'Review';";
            cmd.Parameters.AddWithValue("$id", runIdParam);
            await using var reader = await cmd.ExecuteReaderAsync(Ct);
            while (await reader.ReadAsync(Ct))
            {
                var stageId = Guid.Parse(reader.GetString(0));
                var stageRound = reader.GetInt32(1);
                Assert.False(reader.IsDBNull(2));
                if (stageRound == 1)
                {
                    Assert.True(round1StageIds.Add(stageId));
                }
                else if (stageRound == 2)
                {
                    Assert.True(round2StageIds.Add(stageId));
                }
            }
        }

        Assert.Equal(3, round1StageIds.Count);
        Assert.Equal(3, round2StageIds.Count);
        Assert.Empty(round1StageIds.Intersect(round2StageIds));
        Assert.All(verdicts.Take(3), v => Assert.Contains(v.GetProperty("stageId").GetGuid(), round1StageIds));
        Assert.All(verdicts.Skip(3), v => Assert.Contains(v.GetProperty("stageId").GetGuid(), round2StageIds));
    }

    // --- 3. Rejected en Ronda 1 (con rondas disponibles) ---

    [Fact]
    public async Task Rejected_in_round_1_fails_immediately_with_ReviewRejected_even_when_rounds_remain()
    {
        var coder = new RecordingCodingAgent();
        var secretKey = CodeNetixFactory.CompleteSettings["CODENETIX:Providers:Anthropic:ApiKey"]!;

        await using var baseFactory = CreateFactory();
        baseFactory.ChatClient.Handler = (call, _) =>
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
                      "decision": "rejected",
                      "summary": "Exfiltración de credenciales {{secretKey}} inaceptable.",
                      "findings": []
                    }
                    """);
            }

            if (system.Contains("Quality Reviewer", StringComparison.Ordinal))
            {
                return Task.FromResult(
                    """
                    {
                      "decision": "changes_requested",
                      "summary": "Renombra la variable temporal.",
                      "findings": []
                    }
                    """);
            }

            return Task.FromResult(FakeChatClient.ValidVerdictJson);
        };

        await using var factory = baseFactory.WithWebHostBuilder(b =>
            b.ConfigureTestServices(s => s.AddSingleton<ICodingAgent>(coder)));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", CodeNetixFactory.TestAccessToken);

        var run = await RunToTerminalAsync(client);

        Assert.Equal("Failed", run.GetProperty("state").GetString());
        Assert.Equal("ReviewRejected", run.GetProperty("failureReason").GetString());
        var detail = run.GetProperty("failureDetail").GetString()!;
        Assert.Contains("security", detail, StringComparison.Ordinal);
        Assert.Contains("Exfiltración de credenciales", detail, StringComparison.Ordinal);
        Assert.DoesNotContain(secretKey, detail, StringComparison.Ordinal);
        // Los Revisores con ChangesRequested o Approved no aparecen en el detalle de ReviewRejected.
        Assert.DoesNotContain("quality", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("tests", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("Renombra la variable", detail, StringComparison.Ordinal);

        // No inició Ronda 2.
        Assert.Single(coder.Inputs);
        var verdicts = run.GetProperty("verdicts").EnumerateArray().ToList();
        Assert.Equal(3, verdicts.Count);
        Assert.All(verdicts, v => Assert.Equal(1, v.GetProperty("round").GetInt32()));
    }

    // --- 4. Rejected en Ronda 2 ---

    [Fact]
    public async Task Rejected_in_round_2_fails_with_ReviewRejected_and_exposes_verdicts_from_both_rounds()
    {
        var securityCalls = 0;
        var qualityCalls = 0;

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
                var n = Interlocked.Increment(ref securityCalls);
                return Task.FromResult(n == 1
                    ? """{"decision":"changes_requested","summary":"Falta validar entrada.","findings":[]}"""
                    : FakeChatClient.ValidVerdictJson);
            }

            if (system.Contains("Quality Reviewer", StringComparison.Ordinal))
            {
                var n = Interlocked.Increment(ref qualityCalls);
                return Task.FromResult(n == 2
                    ? """{"decision":"rejected","summary":"La corrección rompió el contrato público.","findings":[]}"""
                    : FakeChatClient.ValidVerdictJson);
            }

            return Task.FromResult(FakeChatClient.ValidVerdictJson);
        };

        using var client = factory.CreateAuthorizedClient();
        var run = await RunToTerminalAsync(client);

        Assert.Equal("Failed", run.GetProperty("state").GetString());
        Assert.Equal("ReviewRejected", run.GetProperty("failureReason").GetString());
        Assert.Contains("contrato público", run.GetProperty("failureDetail").GetString(), StringComparison.Ordinal);

        var verdicts = run.GetProperty("verdicts").EnumerateArray().ToList();
        Assert.Equal(6, verdicts.Count);
        Assert.Equal([1, 1, 1, 2, 2, 2], verdicts.Select(v => v.GetProperty("round").GetInt32()));
        Assert.Equal("rejected", verdicts.Single(v =>
            v.GetProperty("round").GetInt32() == 2
            && v.GetProperty("reviewer").GetString() == "quality").GetProperty("decision").GetString());
    }

    // --- 5. Rondas agotadas sin unanimidad (MaxReviewRounds = 2) ---

    [Fact]
    public async Task Exhausting_max_review_rounds_without_unanimity_fails_with_ReviewRoundsExhausted()
    {
        var coder = new RecordingCodingAgent();

        await using var baseFactory = CreateFactory();
        baseFactory.ChatClient.Handler = (call, _) =>
        {
            var system = call.Messages.First(m => m.Role == ChatRole.System).Text;
            if (!system.Contains("Reviewer of CodeNetix", StringComparison.Ordinal))
            {
                return Task.FromResult(FakeChatClient.ValidAnalysisJson);
            }

            if (system.Contains("Tests Reviewer", StringComparison.Ordinal))
            {
                return Task.FromResult(
                    """
                    {
                      "decision": "changes_requested",
                      "summary": "Falta cobertura del caso límite.",
                      "findings": [
                        { "severity": "medium", "file": "tests/ProfileTests.cs", "line": 12, "message": "Añade caso nulo." }
                      ]
                    }
                    """);
            }

            return Task.FromResult(FakeChatClient.ValidVerdictJson);
        };

        await using var factory = baseFactory.WithWebHostBuilder(b =>
            b.ConfigureTestServices(s => s.AddSingleton<ICodingAgent>(coder)));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", CodeNetixFactory.TestAccessToken);

        var run = await RunToTerminalAsync(client);

        Assert.Equal("Failed", run.GetProperty("state").GetString());
        Assert.Equal("ReviewRoundsExhausted", run.GetProperty("failureReason").GetString());
        var exhaustedDetail = run.GetProperty("failureDetail").GetString()!;
        Assert.Contains("tests", exhaustedDetail, StringComparison.Ordinal);
        Assert.Contains("cobertura", exhaustedDetail, StringComparison.Ordinal);
        // Los Revisores que aprobaron no aparecen en el detalle de ReviewRoundsExhausted.
        Assert.DoesNotContain("security", exhaustedDetail, StringComparison.Ordinal);
        Assert.DoesNotContain("quality", exhaustedDetail, StringComparison.Ordinal);

        // Ejecutó exactamente 2 rondas completas (no inicia Ronda 3).
        Assert.Equal(2, coder.Inputs.Count);
        var verdicts = run.GetProperty("verdicts").EnumerateArray().ToList();
        Assert.Equal(6, verdicts.Count);
        Assert.Equal([1, 1, 1, 2, 2, 2], verdicts.Select(v => v.GetProperty("round").GetInt32()));
    }

    // --- 6. MaxReviewRounds = 1 en codenetix.yml ---

    [Fact]
    public async Task Max_review_rounds_1_in_codenetix_yml_exhausts_after_round_1_without_starting_round_2()
    {
        using var repos = new LocalGitRepos();
        repos.Create("acme", "oneround", new Dictionary<string, string>
        {
            ["codenetix.yml"] = "version: 1\nmax_review_rounds: 1\n",
            ["src/App.cs"] = "class App {}\n",
        });

        var settings = new Dictionary<string, string?>(CodeNetixFactory.CompleteSettings)
        {
            [CodeNetixFactory.CloneBaseUrlKey] = repos.BaseUrl,
        };

        var coder = new RecordingCodingAgent();
        await using var baseFactory = CreateFactory(settings);
        baseFactory.ChatClient.Handler = (call, _) =>
        {
            var system = call.Messages.First(m => m.Role == ChatRole.System).Text;
            if (!system.Contains("Reviewer of CodeNetix", StringComparison.Ordinal))
            {
                return Task.FromResult(FakeChatClient.ValidAnalysisJson);
            }

            if (system.Contains("Quality Reviewer", StringComparison.Ordinal))
            {
                return Task.FromResult("""{"decision":"changes_requested","summary":"Simplifica el método.","findings":[]}""");
            }

            return Task.FromResult(FakeChatClient.ValidVerdictJson);
        };

        await using var factory = baseFactory.WithWebHostBuilder(b =>
            b.ConfigureTestServices(s => s.AddSingleton<ICodingAgent>(coder)));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", CodeNetixFactory.TestAccessToken);

        var run = await RunToTerminalAsync(client, "https://github.com/acme/oneround/issues/88");

        Assert.Equal("Failed", run.GetProperty("state").GetString());
        Assert.Equal("ReviewRoundsExhausted", run.GetProperty("failureReason").GetString());
        Assert.Single(coder.Inputs);
        Assert.Equal(3, run.GetProperty("verdicts").EnumerateArray().Count());
    }

    // --- 7. Cupo independiente de MaxCodingAttempts en Ronda 2 ---

    [Fact]
    public async Task Round_2_gets_its_own_max_coding_attempts_quota_while_total_attempts_accumulate()
    {
        var settings = new Dictionary<string, string?>(CodeNetixFactory.CompleteSettings)
        {
            ["CODENETIX:Limits:MaxCodingAttempts"] = "2",
            ["CODENETIX:Limits:MaxReviewRounds"] = "2",
        };

        var coder = new RecordingCodingAgent();
        var testCall = 0;
        var securityCalls = 0;

        await using var baseFactory = CreateFactory(settings);
        baseFactory.TestRunner.RunHandler = (_, _) =>
        {
            var n = Interlocked.Increment(ref testCall);
            // Ronda 1: intento 1 falla, intento 2 pasa.
            // Ronda 2: intento 3 falla, intento 4 pasa.
            return n is 1 or 3 ? FailingReport($"Fallo en ejecución {n}") : PassingReport();
        };

        baseFactory.ChatClient.Handler = (call, _) =>
        {
            var system = call.Messages.First(m => m.Role == ChatRole.System).Text;
            if (!system.Contains("Reviewer of CodeNetix", StringComparison.Ordinal))
            {
                return Task.FromResult(FakeChatClient.ValidAnalysisJson);
            }

            if (system.Contains("Security Reviewer", StringComparison.Ordinal))
            {
                var n = Interlocked.Increment(ref securityCalls);
                return Task.FromResult(n == 1
                    ? """{"decision":"changes_requested","summary":"Añade validación.","findings":[]}"""
                    : FakeChatClient.ValidVerdictJson);
            }

            return Task.FromResult(FakeChatClient.ValidVerdictJson);
        };

        await using var factory = baseFactory.WithWebHostBuilder(b =>
            b.ConfigureTestServices(s => s.AddSingleton<ICodingAgent>(coder)));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", CodeNetixFactory.TestAccessToken);

        var run = await RunToTerminalAsync(client);

        Assert.Equal("Failed", run.GetProperty("state").GetString());
        Assert.Equal("StageError", run.GetProperty("failureReason").GetString());
        Assert.Equal("Creación de PR no disponible", run.GetProperty("failureDetail").GetString());

        // 4 intentos totales de Programador (2 en Ronda 1 + 2 en Ronda 2).
        Assert.Equal(4, coder.Inputs.Count);
        Assert.Equal([1, 2, 3, 4], coder.Inputs.Select(i => i.Attempt));

        // En Ronda 2 intento 2 (coder.Inputs[3]), el feedback preserva los hallazgos de Ronda 1 y añade el fallo de tests del intento 1 de Ronda 2.
        var round2RetryFeedback = coder.Inputs[3].Feedback;
        Assert.NotNull(round2RetryFeedback);
        Assert.Contains("Añade validación.", round2RetryFeedback, StringComparison.Ordinal);
        Assert.Contains("Fallo en ejecución 3", round2RetryFeedback, StringComparison.Ordinal);

        // En SQLite, coding_attempts = 4 y failed_coding_attempts = 2.
        var runIdParam = run.GetProperty("runId").GetGuid().ToString("D").ToUpperInvariant();
        await using var connection = await OpenReadOnlyAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT coding_attempts, failed_coding_attempts FROM run_runs WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", runIdParam);
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));
        Assert.Equal(4, reader.GetInt32(0));
        Assert.Equal(2, reader.GetInt32(1));

        // 6 veredictos (Ronda 1 y Ronda 2).
        Assert.Equal(6, run.GetProperty("verdicts").EnumerateArray().Count());
    }

    [Fact]
    public async Task Exhausting_max_coding_attempts_in_round_2_fails_with_TestsFailed()
    {
        var settings = new Dictionary<string, string?>(CodeNetixFactory.CompleteSettings)
        {
            ["CODENETIX:Limits:MaxCodingAttempts"] = "2",
            ["CODENETIX:Limits:MaxReviewRounds"] = "2",
        };

        var coder = new RecordingCodingAgent();
        var testCall = 0;

        await using var baseFactory = CreateFactory(settings);
        baseFactory.TestRunner.RunHandler = (_, _) =>
        {
            var n = Interlocked.Increment(ref testCall);
            // Ronda 1 pasa en el intento 1; Ronda 2 falla en ambos intentos (2 y 3).
            return n == 1 ? PassingReport() : FailingReport("CS1002: ; expected");
        };

        baseFactory.ChatClient.Handler = (call, _) =>
        {
            var system = call.Messages.First(m => m.Role == ChatRole.System).Text;
            if (!system.Contains("Reviewer of CodeNetix", StringComparison.Ordinal))
            {
                return Task.FromResult(FakeChatClient.ValidAnalysisJson);
            }

            if (system.Contains("Security Reviewer", StringComparison.Ordinal))
            {
                return Task.FromResult("""{"decision":"changes_requested","summary":"Corrige el filtro.","findings":[]}""");
            }

            return Task.FromResult(FakeChatClient.ValidVerdictJson);
        };

        await using var factory = baseFactory.WithWebHostBuilder(b =>
            b.ConfigureTestServices(s => s.AddSingleton<ICodingAgent>(coder)));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", CodeNetixFactory.TestAccessToken);

        var run = await RunToTerminalAsync(client);

        Assert.Equal("Failed", run.GetProperty("state").GetString());
        Assert.Equal("TestsFailed", run.GetProperty("failureReason").GetString());
        Assert.Contains("CS1002", run.GetProperty("failureDetail").GetString(), StringComparison.Ordinal);

        Assert.Equal(3, coder.Inputs.Count);
        // Los 3 veredictos de la Ronda 1 siguen siendo visibles en la API.
        Assert.Equal(3, run.GetProperty("verdicts").EnumerateArray().Count());
    }

    // --- 8. Pruebas unitarias de formateo de feedback y resúmenes ---

    [Fact]
    public void BuildReviewFeedback_formats_summary_and_findings_with_and_without_line()
    {
        var verdicts = new List<(ReviewerKind, Verdict)>
        {
            (
                ReviewerKind.Security,
                new Verdict(
                    VerdictDecision.ChangesRequested,
                    "Inyección potencial.",
                    ImmutableArray.Create(
                        new ReviewFinding("high", "src/Repo.cs", 25, "Usa parámetros."),
                        new ReviewFinding("low", "src/Helper.cs", null, "Evita reflexión.")))),
        };

        var feedback = RunOrchestrator.BuildReviewFeedback(1, verdicts);

        Assert.DoesNotContain("\r", feedback, StringComparison.Ordinal);
        Assert.Contains("ronda 1:\n[security] Inyección potencial.\n- (high) src/Repo.cs:25: Usa parámetros.\n- (low) src/Helper.cs: Evita reflexión.", feedback, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildRejectionDetail_and_BuildRoundsExhaustedDetail_join_reviewer_summaries_and_collapse_newlines()
    {
        var rejections = new List<(ReviewerKind, Verdict)>
        {
            (ReviewerKind.Security, new Verdict(VerdictDecision.Rejected, "Fuga de\r\nsecretos.", [])),
            (ReviewerKind.Quality, new Verdict(VerdictDecision.Rejected, "Rompe\ncompatibilidad.", [])),
        };

        var rejectionDetail = RunOrchestrator.BuildRejectionDetail(rejections);
        Assert.Equal("Revisión rechazada: security: Fuga de secretos.; quality: Rompe compatibilidad.", rejectionDetail);

        var exhaustedDetail = RunOrchestrator.BuildRoundsExhaustedDetail(
            2,
            [(ReviewerKind.Tests, new Verdict(VerdictDecision.ChangesRequested, "Faltan\r\npruebas.", []))]);
        Assert.Equal("Rondas de revisión agotadas (2): tests: Faltan pruebas.", exhaustedDetail);
    }

    // --- Helpers ---

    private CodeNetixFactory CreateFactory(IReadOnlyDictionary<string, string?>? settings = null) =>
        new(_data, _webRoot, settings ?? CodeNetixFactory.CompleteSettings);

    private static TestReport PassingReport() => new(
        BuildSucceeded: true,
        TotalTests: 3,
        PassedTests: 3,
        FailedTests: 0,
        SkippedTests: 0,
        NewTests: ["Tests.ProfileTests.New_test"],
        FailedTestCases: [],
        Duration: TimeSpan.FromMilliseconds(150));

    private static TestReport FailingReport(string buildOutput) => new(
        BuildSucceeded: false,
        TotalTests: 0,
        PassedTests: 0,
        FailedTests: 0,
        SkippedTests: 0,
        NewTests: [],
        FailedTestCases: [],
        Duration: TimeSpan.FromMilliseconds(100),
        BuildOutput: buildOutput);

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

    private sealed class RecordingCodingAgent : ICodingAgent
    {
        public List<CodingInput> Inputs { get; } = [];

        public Task<CodingResult> RunAsync(CodingInput input, CancellationToken cancellationToken)
        {
            Inputs.Add(input);
            return Task.FromResult<CodingResult>(new CodingResult.Completed(CoderSecretOutput, "claude-test"));
        }
    }
}
