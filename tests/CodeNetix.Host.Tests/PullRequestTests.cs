using System.Collections.Immutable;
using System.Formats.Tar;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CodeNetix.Contracts;
using CodeNetix.GitHub;
using CodeNetix.Pipeline.Features.RunPipeline;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CodeNetix.Host.Tests;

/// <summary>
/// Pruebas de la Historia 3.3 (PR con traza completa, FR-17, AD-8, AD-11):
/// flujo completo hasta <c>PullRequestCreated</c> con 1 único commit sobre <c>BaseSha</c>, push real a
/// <c>codenetix/&lt;runId&gt;</c>, cuerpo trazable redactado, etiqueta <c>codenetix</c>, persistencia idempotente en
/// <c>gh_pull_requests</c>, guardián <see cref="GitSafetyGuard"/> contra push a rama por defecto y merge,
/// manejo de fallos en <c>POST /pulls</c> y <c>POST /labels</c>, y bloqueo de secretos en el commit.
/// </summary>
public sealed class PullRequestTests : IDisposable
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    private readonly string _root;
    private readonly string _data;
    private readonly string _webRoot;

    public PullRequestTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "codenetix-pr-tests-" + Guid.NewGuid().ToString("N"));
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

    // --- 1. Aprobación unánime → PR creado (commit único sobre BaseSha + push real + cuerpo trazable + etiqueta + API + SQLite) ---

    [Fact]
    public async Task Unanimous_approval_creates_single_commit_pushes_codenetix_branch_opens_labeled_pr_and_persists_record()
    {
        using var repos = new LocalGitRepos();
        var baseSha = repos.Create("acme", "shop", new Dictionary<string, string>
        {
            ["README.md"] = "# Shop\n",
            ["src/ProfileController.cs"] = "public class ProfileController { public void Save() {} }\n",
        });

        var settings = new Dictionary<string, string?>(CodeNetixFactory.CompleteSettings)
        {
            [CodeNetixFactory.CloneBaseUrlKey] = repos.BaseUrl,
        };

        var gitHubToken = CodeNetixFactory.CompleteSettings["CODENETIX:GitHub:Token"]!;
        var apiKey = CodeNetixFactory.CompleteSettings["CODENETIX:Providers:Anthropic:ApiKey"]!;

        await using var factory = new CodeNetixFactory(_data, _webRoot, settings);
        factory.Sandbox.OnSessionCreated = session =>
        {
            session.ExportWorkspaceHandler = async (dest, ct) =>
            {
                await WriteTarAsync(dest,
                [
                    ("workspace/README.md", "# Shop\n"),
                    ("workspace/src/ProfileController.cs", "public class ProfileController { public void Save() { SaveChangesAsync(); } }\n"),
                    ("workspace/tests/ProfileTests.cs", "public class ProfileTests { public void Save_persists() {} }\n"),
                ], ct);
            };
        };

        factory.ChatClient.Handler = (call, _) =>
        {
            var system = call.Messages.First(m => m.Role == ChatRole.System).Text;
            if (!system.Contains("Reviewer of CodeNetix", StringComparison.Ordinal))
            {
                return Task.FromResult(FakeChatClient.ValidAnalysisJson);
            }

            if (system.Contains("Quality Reviewer", StringComparison.Ordinal))
            {
                return Task.FromResult(
                    $$"""
                    {
                      "decision": "approved",
                      "summary": "Código claro y bien estructurado (sin fuga de {{apiKey}}).",
                      "findings": [
                        {
                          "severity": "info",
                          "file": "src/ProfileController.cs",
                          "line": 1,
                          "message": "Considera documentar el método Save."
                        }
                      ]
                    }
                    """);
            }

            return Task.FromResult(FakeChatClient.ValidVerdictJson);
        };

        using var client = factory.CreateAuthorizedClient();
        var run = await RunToTerminalAsync(client, "https://github.com/acme/shop/issues/42");
        var runId = run.GetProperty("runId").GetGuid();
        var expectedBranch = PullRequestBranch.For(runId);

        // 1) Estado final PullRequestCreated y transiciones Reviewing → Deciding → PullRequestCreated.
        Assert.Equal("PullRequestCreated", run.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, run.GetProperty("failureReason").ValueKind);
        var events = run.GetProperty("events").EnumerateArray().ToList();
        Assert.Contains(events, e =>
            e.GetProperty("type").GetString() == "StateChanged"
            && e.GetProperty("from").GetString() == "Reviewing"
            && e.GetProperty("to").GetString() == "Deciding");
        Assert.Contains(events, e =>
            e.GetProperty("type").GetString() == "StateChanged"
            && e.GetProperty("from").GetString() == "Deciding"
            && e.GetProperty("to").GetString() == "PullRequestCreated");

        var prEvent = Assert.Single(events, e => e.GetProperty("type").GetString() == "PullRequestOpened");

        // 2) Push real al repo local: rama codenetix/<runId> con exactamente 1 commit sobre BaseSha; main intacta.
        var branches = repos.ListBranches("acme", "shop");
        Assert.Contains("main", branches);
        Assert.Contains(expectedBranch, branches);
        Assert.Equal(baseSha, repos.BranchTip("acme", "shop", "main"));

        var commitSha = repos.BranchTip("acme", "shop", expectedBranch);
        Assert.NotEqual(baseSha, commitSha);
        Assert.Equal(1, repos.CommitCount("acme", "shop", baseSha, expectedBranch));
        Assert.Equal(baseSha, repos.ParentSha("acme", "shop", expectedBranch));

        var filesInCommit = repos.ListFilesInCommit("acme", "shop", commitSha);
        Assert.Contains("src/ProfileController.cs", filesInCommit);
        Assert.Contains("tests/ProfileTests.cs", filesInCommit);
        Assert.DoesNotContain(filesInCommit, f => f.Contains(".git", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("SaveChangesAsync", repos.ShowFile("acme", "shop", commitSha, "src/ProfileController.cs"), StringComparison.Ordinal);

        // 3) PR en GitHub falso: rama head/base, etiqueta codenetix y cuerpo trazable completo y redactado.
        var pr = Assert.Single(factory.GitHub!.PullRequestsOn("acme", "shop"));
        Assert.Equal(expectedBranch, pr.Head);
        Assert.Equal("main", pr.Base);
        Assert.Contains("#42", pr.Title, StringComparison.Ordinal);
        Assert.Equal(["codenetix"], factory.GitHub.LabelsOn("acme", "shop", pr.Number));

        Assert.Contains("https://github.com/acme/shop/issues/42", pr.Body, StringComparison.Ordinal);
        Assert.Contains(run.GetProperty("eventId").GetGuid().ToString("D"), pr.Body, StringComparison.Ordinal);
        Assert.Contains("El botón Guardar del perfil no persiste los cambios.", pr.Body, StringComparison.Ordinal);
        Assert.Contains("Al pulsar Guardar los cambios del perfil se persisten.", pr.Body, StringComparison.Ordinal);
        Assert.Contains("quality", pr.Body, StringComparison.Ordinal);
        Assert.Contains("src/ProfileController.cs:1", pr.Body, StringComparison.Ordinal);
        Assert.Contains("Considera documentar el método Save.", pr.Body, StringComparison.Ordinal);
        Assert.Contains("Compilación: OK", pr.Body, StringComparison.Ordinal);
        Assert.Contains("CodeNetix.GeneratedTests.AddedTest", pr.Body, StringComparison.Ordinal);
        Assert.Contains("no disponible", pr.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(gitHubToken, pr.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(apiKey, pr.Body, StringComparison.Ordinal);

        // 4) Evento PullRequestOpened y GET /api/runs/{id}.pullRequest coinciden.
        Assert.Equal(pr.Number, prEvent.GetProperty("number").GetInt64());
        Assert.Equal(pr.HtmlUrl, prEvent.GetProperty("url").GetString());
        Assert.Equal(expectedBranch, prEvent.GetProperty("branch").GetString());
        Assert.Equal(commitSha, prEvent.GetProperty("commitSha").GetString());

        var prJson = run.GetProperty("pullRequest");
        Assert.Equal(JsonValueKind.Object, prJson.ValueKind);
        Assert.Equal(pr.Number, prJson.GetProperty("number").GetInt64());
        Assert.Equal(pr.HtmlUrl, prJson.GetProperty("url").GetString());
        Assert.Equal(expectedBranch, prJson.GetProperty("branchName").GetString());
        Assert.Equal(commitSha, prJson.GetProperty("commitSha").GetString());
        Assert.Equal(pr.Title, prJson.GetProperty("title").GetString());

        // 5) Persistencia en gh_pull_requests y etapa Decision en run_stages.
        await using var connection = await OpenReadOnlyAsync();
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT number, url, branch_name, commit_sha, title, created_at FROM gh_pull_requests WHERE run_id = $id;";
            cmd.Parameters.AddWithValue("$id", runId.ToString("D").ToUpperInvariant());
            await using var reader = await cmd.ExecuteReaderAsync(Ct);
            Assert.True(await reader.ReadAsync(Ct));
            Assert.Equal(pr.Number, reader.GetInt64(0));
            Assert.Equal(pr.HtmlUrl, reader.GetString(1));
            Assert.Equal(expectedBranch, reader.GetString(2));
            Assert.Equal(commitSha, reader.GetString(3));
            Assert.Equal(pr.Title, reader.GetString(4));
            Assert.True(reader.GetInt64(5) > 0);
            Assert.False(await reader.ReadAsync(Ct));
        }

        await using (var stageCmd = connection.CreateCommand())
        {
            stageCmd.CommandText = "SELECT COUNT(*) FROM run_stages WHERE run_id = $id AND kind = 'Decision' AND completed_at IS NOT NULL;";
            stageCmd.Parameters.AddWithValue("$id", runId.ToString("D").ToUpperInvariant());
            Assert.Equal(1L, (long)(await stageCmd.ExecuteScalarAsync(Ct))!);
        }
    }

    // --- 2. Cuerpo del PR con traza completa (con y sin coste, hallazgos con/sin línea, redacción) ---

    [Fact]
    public void PullRequestBodyBuilder_includes_all_sections_cost_when_present_and_redacts_secrets()
    {
        var runId = Guid.CreateVersion7();
        var eventId = Guid.CreateVersion7();
        const string Secret = "sk-ant-supersecretvalue123456";
        var redactor = new FixedSecretRedactor(Secret);
        var spec = new Spec(
            Problem: $"Falla el login con {Secret}.",
            ProbableCause: "Falta comprobación de nulidad.",
            ProposedChange: "Validar el parámetro antes de usarlo.",
            AcceptanceCriteria: ["Devuelve 400 si el parámetro es nulo", "Añade test unitario"],
            AffectedFiles: ["src/Auth.cs"]);
        var verdicts = new[]
        {
            new RunVerdict(
                Guid.CreateVersion7(),
                Round: 1,
                ReviewerKind.Security,
                VerdictDecision.Approved,
                $"Sin vulnerabilidades; clave {Secret} omitida.",
                [
                    new ReviewFinding("low", "src/Auth.cs", 18, "Valida también espacios."),
                    new ReviewFinding("info", "src/AuthHelper.cs", null, "Buen aislamiento."),
                ],
                Model: "claude-sonnet-5-5",
                Attempts: 1,
                CreatedAt: DateTimeOffset.UtcNow),
        };
        var report = new TestReport(
            BuildSucceeded: true,
            TotalTests: 12,
            PassedTests: 12,
            FailedTests: 0,
            SkippedTests: 0,
            NewTests: ["Tests.AuthTests.Rejects_null"],
            FailedTestCases: [],
            Duration: TimeSpan.FromMilliseconds(200));

        var withCost = PullRequestBodyBuilder.BuildBody(runId, eventId, "acme", "app", 7, spec, verdicts, report, redactor, costUsd: 0.0425m);
        var withoutCost = PullRequestBodyBuilder.BuildBody(runId, eventId, "acme", "app", 7, spec, verdicts, report, redactor, costUsd: null);

        Assert.Contains("https://github.com/acme/app/issues/7", withCost, StringComparison.Ordinal);
        Assert.Contains(eventId.ToString("D"), withCost, StringComparison.Ordinal);
        Assert.Contains("Devuelve 400 si el parámetro es nulo", withCost, StringComparison.Ordinal);
        Assert.Contains("src/Auth.cs:18", withCost, StringComparison.Ordinal);
        Assert.Contains("src/AuthHelper.cs", withCost, StringComparison.Ordinal);
        Assert.Contains("12 totales, 12 pasados, 0 fallidos, 0 omitidos", withCost, StringComparison.Ordinal);
        Assert.Contains("Tests.AuthTests.Rejects_null", withCost, StringComparison.Ordinal);
        Assert.Contains("$0.0425 USD", withCost, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, withCost, StringComparison.Ordinal);

        Assert.Contains("no disponible", withoutCost, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, withoutCost, StringComparison.Ordinal);
    }

    // --- 3 & 4. Guardián Git (GitSafetyGuard): push a rama por defecto / sin prefijo y merge prohibido ---

    [Theory]
    [InlineData("main", "main")]
    [InlineData("MAIN", "main")]
    [InlineData("refs/heads/main", "main")]
    [InlineData("master", "master")]
    [InlineData("feature/fix-1", "main")]
    [InlineData("codenetix/", "main")]
    [InlineData("codenetix/run:main", "main")]
    [InlineData("codenetix/run..main", "main")]
    [InlineData("codenetix/.hidden", "main")]
    [InlineData("codenetix/sub.lock/branch", "main")]
    [InlineData("codenetix/branch.lock", "main")]
    [InlineData("", "main")]
    public void GitSafetyGuard_blocks_and_logs_push_to_default_or_non_codenetix_branch(string targetBranch, string defaultBranch)
    {
        var logger = new RecordingLogger<GitSafetyGuard>();
        var guard = new GitSafetyGuard(logger);

        Assert.Throws<GitOperationBlockedException>(() => guard.EnsurePushAllowed(targetBranch, defaultBranch));
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
    }

    [Fact]
    public void GitSafetyGuard_allows_valid_codenetix_branch()
    {
        var logger = new RecordingLogger<GitSafetyGuard>();
        var guard = new GitSafetyGuard(logger);

        guard.EnsurePushAllowed(PullRequestBranch.For(Guid.CreateVersion7()), "main");
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void GitSafetyGuard_blocks_and_logs_any_merge_attempt()
    {
        var logger = new RecordingLogger<GitSafetyGuard>();
        var guard = new GitSafetyGuard(logger);

        Assert.Throws<GitOperationBlockedException>(() => guard.EnsureMergeForbidden("git merge", "main"));
        Assert.Throws<GitOperationBlockedException>(() => guard.EnsureMergeForbidden("PUT /pulls/1/merge"));

        Assert.Equal(2, logger.Entries.Count);
        Assert.All(logger.Entries, e => Assert.Equal(LogLevel.Error, e.Level));
    }

    [Fact]
    public async Task RepositoryWorkspace_blocks_push_to_default_branch_without_touching_remote()
    {
        using var repos = new LocalGitRepos();
        var baseSha = repos.Create("acme", "guarded");
        var workspaceDir = Path.Combine(_root, "ws-guard");
        var guardLogger = new RecordingLogger<GitSafetyGuard>();
        var guard = new GitSafetyGuard(guardLogger);
        var options = Options.Create(new GitHubOptions
        {
            CloneBaseUrl = repos.BaseUrl,
            WorkspacesDirectory = workspaceDir,
            Token = "ghp_guardtoken0123456789abcdefghijkl",
        });
        var workspace = new RepositoryWorkspace(options, NullLogger<RepositoryWorkspace>.Instance, guard);
        var runId = Guid.CreateVersion7();

        var prepared = await workspace.PrepareAsync(runId, "acme", "guarded", Ct);
        await File.WriteAllTextAsync(Path.Combine(prepared.WorkspacePath, "README.md"), "# cambiado\n", Ct);

        await Assert.ThrowsAsync<GitOperationBlockedException>(() =>
            workspace.CommitAndPushBranchAsync(runId, baseSha, "main", "intento ilegal", Ct));

        Assert.Equal(baseSha, repos.BranchTip("acme", "guarded", "main"));
        Assert.Equal(["main"], repos.ListBranches("acme", "guarded"));
        Assert.Single(guardLogger.Entries, e => e.Level == LogLevel.Error);
    }

    // --- 5. Idempotencia por runId ---

    [Fact]
    public async Task PublishAsync_is_idempotent_by_runId_and_does_not_duplicate_pr_or_push()
    {
        using var repos = new LocalGitRepos();
        var baseSha = repos.Create("acme", "idem");
        var settings = new Dictionary<string, string?>(CodeNetixFactory.CompleteSettings)
        {
            [CodeNetixFactory.CloneBaseUrlKey] = repos.BaseUrl,
        };

        await using var factory = new CodeNetixFactory(_data, _webRoot, settings);
        using var client = factory.CreateAuthorizedClient();
        var run = await RunToTerminalAsync(client, "https://github.com/acme/idem/issues/9");
        var runId = run.GetProperty("runId").GetGuid();

        Assert.Equal("PullRequestCreated", run.GetProperty("state").GetString());
        Assert.Equal(1, factory.GitHub!.PullRequestPosts);
        Assert.Equal(1, factory.GitHub.LabelPosts);

        // Segunda llamada directa con el mismo runId: devuelve el PR ya persistido sin tocar git ni GitHub.
        var publisher = factory.Services.GetRequiredService<IPullRequestPublisher>();
        var second = await publisher.PublishAsync(
            new PullRequestRequest(runId, "acme", "idem", baseSha, "Otro título", "Otro cuerpo", "Otro commit"),
            Ct);

        var firstPr = run.GetProperty("pullRequest");
        Assert.Equal(firstPr.GetProperty("number").GetInt64(), second.Number);
        Assert.Equal(firstPr.GetProperty("url").GetString(), second.Url);
        Assert.Equal(firstPr.GetProperty("branchName").GetString(), second.BranchName);
        Assert.Equal(firstPr.GetProperty("commitSha").GetString(), second.CommitSha);
        Assert.Equal(1, factory.GitHub.PullRequestPosts);
        Assert.Equal(1, factory.GitHub.LabelPosts);
    }

    // --- 6. Error de GitHub API al abrir PR (422 / 500) ---

    [Theory]
    [InlineData(StatusCodes.Status422UnprocessableEntity)]
    [InlineData(StatusCodes.Status500InternalServerError)]
    public async Task GitHub_pulls_api_error_fails_run_from_Deciding_without_persisting_fake_pr(int status)
    {
        using var repos = new LocalGitRepos();
        repos.Create("acme", "prfail");
        var settings = new Dictionary<string, string?>(CodeNetixFactory.CompleteSettings)
        {
            [CodeNetixFactory.CloneBaseUrlKey] = repos.BaseUrl,
        };

        await using var factory = new CodeNetixFactory(_data, _webRoot, settings);
        using var client = factory.CreateAuthorizedClient();
        factory.GitHub!.PullRequestPostStatus = status;

        var run = await RunToTerminalAsync(client, "https://github.com/acme/prfail/issues/5");

        Assert.Equal("Failed", run.GetProperty("state").GetString());
        Assert.Equal("StageError", run.GetProperty("failureReason").GetString());
        Assert.Contains($"HTTP {status}", run.GetProperty("failureDetail").GetString(), StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Null, run.GetProperty("pullRequest").ValueKind);

        var events = run.GetProperty("events").EnumerateArray().ToList();
        Assert.Contains(events, e =>
            e.GetProperty("type").GetString() == "StateChanged"
            && e.GetProperty("from").GetString() == "Deciding"
            && e.GetProperty("to").GetString() == "Failed");
        Assert.DoesNotContain(events, e => e.GetProperty("type").GetString() == "PullRequestOpened");

        await using var connection = await OpenReadOnlyAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM gh_pull_requests;";
        Assert.Equal(0L, (long)(await cmd.ExecuteScalarAsync(Ct))!);
    }

    // --- 7. Fallo al añadir etiqueta codenetix ---

    [Theory]
    [InlineData(StatusCodes.Status403Forbidden)]
    [InlineData(StatusCodes.Status500InternalServerError)]
    public async Task Label_failure_persists_pr_and_completes_PullRequestCreated(int status)
    {
        using var repos = new LocalGitRepos();
        repos.Create("acme", "labelfail");
        var settings = new Dictionary<string, string?>(CodeNetixFactory.CompleteSettings)
        {
            [CodeNetixFactory.CloneBaseUrlKey] = repos.BaseUrl,
        };

        await using var factory = new CodeNetixFactory(_data, _webRoot, settings);
        using var client = factory.CreateAuthorizedClient();
        factory.GitHub!.LabelPostStatus = status;

        var run = await RunToTerminalAsync(client, "https://github.com/acme/labelfail/issues/11");

        Assert.Equal("PullRequestCreated", run.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Object, run.GetProperty("pullRequest").ValueKind);
        Assert.Single(factory.GitHub.PullRequestsOn("acme", "labelfail"));
        Assert.Empty(factory.GitHub.LabelsOn("acme", "labelfail", run.GetProperty("pullRequest").GetProperty("number").GetInt64()));

        await using var connection = await OpenReadOnlyAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM gh_pull_requests;";
        Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync(Ct))!);
    }

    // --- 8. Secreto en el commit bloqueado antes del push ---

    [Fact]
    public async Task Secret_in_staged_commit_is_blocked_before_push_and_fails_run()
    {
        using var repos = new LocalGitRepos();
        repos.Create("acme", "secretleak");
        var settings = new Dictionary<string, string?>(CodeNetixFactory.CompleteSettings)
        {
            [CodeNetixFactory.CloneBaseUrlKey] = repos.BaseUrl,
        };
        var secret = CodeNetixFactory.CompleteSettings["CODENETIX:Providers:Anthropic:ApiKey"]!;

        await using var factory = new CodeNetixFactory(_data, _webRoot, settings);
        factory.Sandbox.OnSessionCreated = session =>
        {
            session.ExportWorkspaceHandler = async (dest, ct) =>
            {
                await WriteTarAsync(dest,
                [
                    ("workspace/README.md", "# test\n"),
                    ("workspace/src/Leaked.cs", $"public static class Leaked {{ public const string Key = \"{secret}\"; }}\n"),
                ], ct);
            };
        };

        using var client = factory.CreateAuthorizedClient();
        var run = await RunToTerminalAsync(client, "https://github.com/acme/secretleak/issues/19");

        Assert.Equal("Failed", run.GetProperty("state").GetString());
        Assert.Equal("StageError", run.GetProperty("failureReason").GetString());
        Assert.Contains("secretos", run.GetProperty("failureDetail").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secret, run.GetProperty("failureDetail").GetString(), StringComparison.Ordinal);
        Assert.Equal(["main"], repos.ListBranches("acme", "secretleak"));
        Assert.Empty(factory.GitHub!.PullRequestsOn("acme", "secretleak"));
    }

    // --- 9. Rama por defecto distinta de main ("master") ---

    [Fact]
    public async Task Non_main_default_branch_opens_pr_against_master_and_guard_blocks_push_to_master()
    {
        using var repos = new LocalGitRepos();
        var baseSha = repos.Create("acme", "legacy", defaultBranch: "master");
        var settings = new Dictionary<string, string?>(CodeNetixFactory.CompleteSettings)
        {
            [CodeNetixFactory.CloneBaseUrlKey] = repos.BaseUrl,
        };

        await using var factory = new CodeNetixFactory(_data, _webRoot, settings);
        using var client = factory.CreateAuthorizedClient();
        var run = await RunToTerminalAsync(client, "https://github.com/acme/legacy/issues/77");

        Assert.Equal("PullRequestCreated", run.GetProperty("state").GetString());
        var pr = Assert.Single(factory.GitHub!.PullRequestsOn("acme", "legacy"));
        Assert.Equal("master", pr.Base);
        Assert.Equal(baseSha, repos.BranchTip("acme", "legacy", "master"));
        Assert.Equal(1, repos.CommitCount("acme", "legacy", baseSha, pr.Head));

        // Además, RepositoryWorkspace bloquea push directo a "master".
        var workspaceDir = Path.Combine(_root, "ws-master-guard");
        var guardLogger = new RecordingLogger<GitSafetyGuard>();
        var guard = new GitSafetyGuard(guardLogger);
        var options = Options.Create(new GitHubOptions
        {
            CloneBaseUrl = repos.BaseUrl,
            WorkspacesDirectory = workspaceDir,
            Token = "ghp_masterguard0123456789abcdefghijk",
        });
        var workspace = new RepositoryWorkspace(options, NullLogger<RepositoryWorkspace>.Instance, guard);
        var guardRunId = Guid.CreateVersion7();
        await workspace.PrepareAsync(guardRunId, "acme", "legacy", Ct);

        await Assert.ThrowsAsync<GitOperationBlockedException>(() =>
            workspace.CommitAndPushBranchAsync(guardRunId, baseSha, "master", "intento a master", Ct));
        Assert.Single(guardLogger.Entries, e => e.Level == LogLevel.Error);
    }

    // --- 10. Push HTTP con http.extraHeader efímero, redacción del token y .git/config limpio ---

    [Fact]
    public async Task CommitAndPushBranchAsync_over_http_sends_ephemeral_basic_header_redacts_token_and_keeps_git_config_clean()
    {
        using var repos = new LocalGitRepos();
        var baseSha = repos.Create("acme", "httppush");
        await using var fakeGitHub = await FakeGitHub.StartAsync(Ct);

        const string Token = "ghp_httppushtoken0123456789abcdefgh";
        var workspaceDir = Path.Combine(_root, "ws-http-push");
        var guard = new GitSafetyGuard(NullLogger<GitSafetyGuard>.Instance);
        var redactor = new FixedSecretRedactor(Token);
        var options = Options.Create(new GitHubOptions
        {
            CloneBaseUrl = repos.BaseUrl,
            WorkspacesDirectory = workspaceDir,
            Token = Token,
        });
        var workspace = new RepositoryWorkspace(options, NullLogger<RepositoryWorkspace>.Instance, guard, redactor);
        var runId = Guid.CreateVersion7();

        var prepared = await workspace.PrepareAsync(runId, "acme", "httppush", Ct);

        // Apuntamos origin al servidor HTTP local (FakeGitHub) para verificar la cabecera Authorization: Basic y la redacción del fallo.
        var httpRemote = $"{fakeGitHub.BaseUrl.TrimEnd('/')}/acme/httppush.git";
        LocalGitRepos.Git(prepared.WorkspacePath, "remote", "set-url", "origin", httpRemote);
        await File.WriteAllTextAsync(Path.Combine(prepared.WorkspacePath, "README.md"), "# cambiado\n", Ct);

        var ex = await Assert.ThrowsAsync<PullRequestPublishException>(() =>
            workspace.CommitAndPushBranchAsync(runId, baseSha, PullRequestBranch.For(runId), "fix: cambio http", Ct));

        Assert.DoesNotContain(Token, ex.Message, StringComparison.Ordinal);
        var expectedBasic = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"x-access-token:{Token}"));
        Assert.DoesNotContain(expectedBasic, ex.Message, StringComparison.Ordinal);

        Assert.Contains(fakeGitHub.Requests, r =>
            r.Path.StartsWith("/acme/httppush.git/", StringComparison.Ordinal)
            && string.Equals(r.Authorization, expectedBasic, StringComparison.Ordinal));

        var gitConfig = await File.ReadAllTextAsync(Path.Combine(prepared.WorkspacePath, ".git", "config"), Ct);
        Assert.DoesNotContain(Token, gitConfig, StringComparison.Ordinal);
        Assert.DoesNotContain("extraheader", gitConfig, StringComparison.OrdinalIgnoreCase);
    }

    // --- 11. Escaneo de secretos: eliminar secreto existente se permite; añadir secreto en binario se bloquea ---

    [Fact]
    public async Task Staged_secret_scan_allows_removing_existing_secret_and_blocks_adding_secret_in_binary_file()
    {
        const string Secret = "sk-ant-existingsecret0123456789abcd";
        using var repos = new LocalGitRepos();
        var baseSha = repos.Create("acme", "secretscan", new Dictionary<string, string>
        {
            ["src/Config.cs"] = $"public static class Config {{ public const string Key = \"{Secret}\"; }}\n",
        });

        var workspaceDir = Path.Combine(_root, "ws-secret-scan");
        var guard = new GitSafetyGuard(NullLogger<GitSafetyGuard>.Instance);
        var redactor = new FixedSecretRedactor(Secret);
        var options = Options.Create(new GitHubOptions
        {
            CloneBaseUrl = repos.BaseUrl,
            WorkspacesDirectory = workspaceDir,
            Token = "ghp_scantoken0123456789abcdefghijkl",
        });
        var workspace = new RepositoryWorkspace(options, NullLogger<RepositoryWorkspace>.Instance, guard, redactor);

        // Caso A: eliminar un secreto hardcodeado existente debe permitirse (sin falso positivo en líneas '-' del diff).
        var removeRunId = Guid.CreateVersion7();
        var preparedRemove = await workspace.PrepareAsync(removeRunId, "acme", "secretscan", Ct);
        await File.WriteAllTextAsync(
            Path.Combine(preparedRemove.WorkspacePath, "src", "Config.cs"),
            "public static class Config { public static string Key => Environment.GetEnvironmentVariable(\"API_KEY\")!; }\n",
            Ct);

        var pushed = await workspace.CommitAndPushBranchAsync(removeRunId, baseSha, PullRequestBranch.For(removeRunId), "fix: remove hardcoded secret", Ct);
        Assert.Equal(1, repos.CommitCount("acme", "secretscan", baseSha, pushed.BranchName));

        // Caso B: añadir un secreto incrustado en un archivo binario (con bytes NUL) debe bloquearse antes del push.
        var binaryRunId = Guid.CreateVersion7();
        var preparedBinary = await workspace.PrepareAsync(binaryRunId, "acme", "secretscan", Ct);
        var binaryPayload = new byte[] { 0x00, 0x01, 0x02, 0x00 }
            .Concat(Encoding.UTF8.GetBytes(Secret))
            .Concat(new byte[] { 0x00, 0xFF })
            .ToArray();
        await File.WriteAllBytesAsync(Path.Combine(preparedBinary.WorkspacePath, "payload.bin"), binaryPayload, Ct);

        var ex = await Assert.ThrowsAsync<PullRequestPublishException>(() =>
            workspace.CommitAndPushBranchAsync(binaryRunId, baseSha, PullRequestBranch.For(binaryRunId), "add binary", Ct));
        Assert.Contains("secretos", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(PullRequestBranch.For(binaryRunId), repos.ListBranches("acme", "secretscan"));
    }

    // --- 12. Guardián en fronteras HTTP (GitHubApiClient) y proceso (git merge) ---

    [Theory]
    [InlineData("repos/acme/app/pulls/1/merge")]
    [InlineData("repos/acme/app/pulls/42/merge?foo=bar")]
    [InlineData("repos/acme/app/merges")]
    public async Task GitHubApiClient_blocks_merge_endpoints_via_GitSafetyGuard_without_sending_http_request(string mergePath)
    {
        await using var fakeGitHub = await FakeGitHub.StartAsync(Ct);
        var guardLogger = new RecordingLogger<GitSafetyGuard>();
        var guard = new GitSafetyGuard(guardLogger);
        using var apiClient = new GitHubApiClient(
            Options.Create(new GitHubOptions
            {
                ApiUrl = fakeGitHub.BaseUrl,
                Token = "ghp_apitoken0123456789abcdefghijklm",
            }),
            guard);

        await Assert.ThrowsAsync<GitOperationBlockedException>(() =>
            apiClient.PostAsync(mergePath, new { merge_method = "squash" }, Ct));

        Assert.Empty(fakeGitHub.Requests);
        Assert.Single(guardLogger.Entries, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task RepositoryWorkspace_blocks_git_merge_subcommand_via_GitSafetyGuard()
    {
        var guardLogger = new RecordingLogger<GitSafetyGuard>();
        var guard = new GitSafetyGuard(guardLogger);
        var options = Options.Create(new GitHubOptions
        {
            WorkspacesDirectory = Path.Combine(_root, "ws-merge-guard"),
        });
        var workspace = new RepositoryWorkspace(options, NullLogger<RepositoryWorkspace>.Instance, guard);

        await Assert.ThrowsAsync<GitOperationBlockedException>(() =>
            workspace.RunGitAsync(null, ["-c", "user.name=test", "merge", "origin/main"], token: null, TimeSpan.FromSeconds(5), Ct));

        Assert.Single(guardLogger.Entries, e => e.Level == LogLevel.Error);
    }

    // --- Helpers ---

    private async Task<JsonElement> RunToTerminalAsync(HttpClient client, string issueUrl)
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

    private sealed class FixedSecretRedactor(string secret) : ISecretRedactor
    {
        public string Redact(string value) => value.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Entries)
            {
                Entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }
}
