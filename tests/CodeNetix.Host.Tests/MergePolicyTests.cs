using System.Formats.Tar;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CodeNetix.Contracts;
using CodeNetix.GitHub;
using CodeNetix.Pipeline.Features.DecideMerge;
using CodeNetix.Pipeline.Features.RunPipeline;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CodeNetix.Host.Tests;

/// <summary>
/// Pruebas de la Historia 3.4 (Clasificación de riesgo determinista, AD-9, FR-18, NFR-6):
/// <see cref="GlobMatcher"/>, <see cref="MergePolicy.CountDiffLines"/>, <see cref="MergePolicy.Classify"/>
/// (tests de tabla sobre las 7 filas de la matriz I/O), <see cref="PullRequestBodyBuilder"/> con
/// <see cref="PolicyResult"/> y pruebas E2E en <see cref="RunOrchestrator"/> para <see cref="RiskClass.Minor"/>
/// y <see cref="RiskClass.NeedsAttention"/>.
/// </summary>
public sealed class MergePolicyTests : IDisposable
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);
    private static readonly IReadOnlyList<string> DefaultProtected = EffectiveConfig.DefaultProtectedPaths;
    private static readonly IReadOnlyList<VerdictDecision> UnanimousApproved =
    [
        VerdictDecision.Approved,
        VerdictDecision.Approved,
        VerdictDecision.Approved,
    ];

    private readonly string _root;
    private readonly string _data;
    private readonly string _webRoot;

    public MergePolicyTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "codenetix-policy-tests-" + Guid.NewGuid().ToString("N"));
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

    // --- 1. GlobMatcher ---

    [Theory]
    [InlineData("src/App/App.csproj", "**/*.csproj", true)]
    [InlineData("App.csproj", "**/*.csproj", true)]
    [InlineData("src\\App\\App.csproj", "**/*.csproj", true)]
    [InlineData("./src/App/App.csproj", "**/*.csproj", true)]
    [InlineData("src/App/App.csproj.user", "**/*.csproj", false)]
    [InlineData("src/Data/Migrations/001.cs", "**/Migrations/**", true)]
    [InlineData("Migrations/001.cs", "**/Migrations/**", true)]
    [InlineData("src/Data/Migrations/Sub/001.cs", "**/Migrations/**", true)]
    [InlineData("src\\Data\\Migrations\\001.cs", "**/Migrations/**", true)]
    [InlineData("src/MyMigrations/001.cs", "**/Migrations/**", false)]
    [InlineData(".github/workflows/ci.yml", ".github/**", true)]
    [InlineData("/./.github/workflows/ci.yml", ".github/**", true)]
    [InlineData(".github/CODEOWNERS", ".github/**", true)]
    [InlineData("src/.github/workflows/ci.yml", ".github/**", false)]
    [InlineData("src/Security/Auth.cs", "src/Security/**", true)]
    [InlineData("src/./Security/Auth.cs", "src/Security/**", true)]
    [InlineData("src/foo/../Security/Auth.cs", "src/Security/**", true)]
    [InlineData("src/Security/Sub/Token.cs", "src/Security/**", true)]
    [InlineData("src/SecurityExtra/Auth.cs", "src/Security/**", false)]
    [InlineData("src/v1/api.cs", "src/v?/api.*", true)]
    [InlineData("src/v10/api.cs", "src/v?/api.*", false)]
    [InlineData("", "**/*.csproj", false)]
    [InlineData("src/App.cs", "", false)]
    public void GlobMatcher_matches_expected_paths(string path, string pattern, bool expected)
    {
        Assert.Equal(expected, GlobMatcher.IsMatch(path, pattern));
    }

    // --- 2. CountDiffLines ---

    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("diff --git a/a.cs b/a.cs\n--- a/a.cs\n+++ b/a.cs\n@@ -1,2 +1,3 @@\n context\n+added\n-removed\n+added2\n", 3)]
    [InlineData("diff --git a/a.cs b/a.cs\r\n--- a/a.cs\r\n+++ b/a.cs\r\n@@ -1 +1 @@\r\n+++counter;\r\n---counter;\r\n", 2)]
    [InlineData("diff --git a/query.sql b/query.sql\n--- a/query.sql\n+++ b/query.sql\n@@ -1,2 +1,2 @@\n--- comment\n+++ value\n", 2)]
    [InlineData("--- /dev/null\n+++ b/new.cs\n@@ -0,0 +1,2 @@\n+line1\n+line2\n", 2)]
    public void CountDiffLines_counts_only_added_and_removed_lines_without_headers(string? diff, int expected)
    {
        Assert.Equal(expected, MergePolicy.CountDiffLines(diff));
    }

    // --- 3. MergePolicy.Classify: tests de tabla sobre la matriz I/O ---

    public static TheoryData<string, PolicyInput, RiskClass, string[], int> ClassifyCases => new()
    {
        {
            "Cambio menor limpio",
            new PolicyInput(["src/App.cs"], 40, DefaultProtected, UnanimousApproved, Round: 1),
            RiskClass.Minor,
            [],
            0
        },
        {
            "Ruta protegida por defecto (.csproj)",
            new PolicyInput(["src/App/App.csproj"], 15, DefaultProtected, UnanimousApproved, Round: 1),
            RiskClass.NeedsAttention,
            ["src/App/App.csproj"],
            1
        },
        {
            "Ruta protegida por defecto (Migrations con separador Windows)",
            new PolicyInput(["src\\Data\\Migrations\\001.cs"], 20, DefaultProtected, UnanimousApproved, Round: 1),
            RiskClass.NeedsAttention,
            ["src/Data/Migrations/001.cs"],
            1
        },
        {
            "Ruta protegida por defecto (.github)",
            new PolicyInput([".github/workflows/ci.yml"], 10, DefaultProtected, UnanimousApproved, Round: 1),
            RiskClass.NeedsAttention,
            [".github/workflows/ci.yml"],
            1
        },
        {
            "Ruta protegida personalizada de codenetix.yml",
            new PolicyInput(["src/Security/Auth.cs"], 12, [.. DefaultProtected, "src/Security/**"], UnanimousApproved, Round: 1),
            RiskClass.NeedsAttention,
            ["src/Security/Auth.cs"],
            1
        },
        {
            "Umbral exacto del diff (400 -> Minor)",
            new PolicyInput(["src/App.cs"], 400, DefaultProtected, UnanimousApproved, Round: 1),
            RiskClass.Minor,
            [],
            0
        },
        {
            "Umbral exacto del diff superado (401 -> NeedsAttention)",
            new PolicyInput(["src/App.cs"], 401, DefaultProtected, UnanimousApproved, Round: 1),
            RiskClass.NeedsAttention,
            [],
            1
        },
        {
            "Unanimidad en Ronda 2 (no en primera ronda)",
            new PolicyInput(["src/App.cs"], 20, DefaultProtected, UnanimousApproved, Round: 2),
            RiskClass.NeedsAttention,
            [],
            1
        },
        {
            "Sin veredictos (lista vacía)",
            new PolicyInput(["src/App.cs"], 20, DefaultProtected, [], Round: 1),
            RiskClass.NeedsAttention,
            [],
            1
        },
        {
            "Veredictos no unánimes (ChangesRequested)",
            new PolicyInput(["src/App.cs"], 20, DefaultProtected, [VerdictDecision.Approved, VerdictDecision.ChangesRequested], Round: 1),
            RiskClass.NeedsAttention,
            [],
            1
        },
        {
            "Múltiples factores combinados (rutas duplicadas y desordenadas + diff > 400 + ronda 2)",
            new PolicyInput(
                ["src/Security/Auth.cs", "src/App/App.csproj", "src\\App\\App.csproj"],
                500,
                [.. DefaultProtected, "src/Security/**"],
                UnanimousApproved,
                Round: 2),
            RiskClass.NeedsAttention,
            ["src/App/App.csproj", "src/Security/Auth.cs"],
            3
        },
    };

    [Theory]
    [MemberData(nameof(ClassifyCases))]
    public void Classify_evaluates_matrix_deterministically(
        string _,
        PolicyInput input,
        RiskClass expectedRisk,
        string[] expectedProtectedPaths,
        int expectedReasonCount)
    {
        var result = MergePolicy.Classify(input);

        Assert.Equal(expectedRisk, result.RiskClass);
        Assert.Equal(PolicyAction.CreatePullRequest, result.Action);
        Assert.Equal(expectedProtectedPaths, result.MatchedProtectedPaths);
        Assert.Equal(expectedReasonCount, result.Reasons.Count);
    }

    [Fact]
    public void Classify_is_deterministic_across_repeated_invocations_and_throws_on_null()
    {
        Assert.Throws<ArgumentNullException>(() => MergePolicy.Classify(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => MergePolicy.Classify(new PolicyInput(["src/App.cs"], -1, DefaultProtected, UnanimousApproved, Round: 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => MergePolicy.Classify(new PolicyInput(["src/App.cs"], 10, DefaultProtected, UnanimousApproved, Round: 1, MaxMinorDiffLines: -1)));

        var customThresholdMinor = MergePolicy.Classify(new PolicyInput(["src/App.cs"], 50, DefaultProtected, UnanimousApproved, Round: 1, MaxMinorDiffLines: 50));
        Assert.Equal(RiskClass.Minor, customThresholdMinor.RiskClass);

        var customThresholdRisky = MergePolicy.Classify(new PolicyInput(["src/App.cs"], 51, DefaultProtected, UnanimousApproved, Round: 1, MaxMinorDiffLines: 50));
        Assert.Equal(RiskClass.NeedsAttention, customThresholdRisky.RiskClass);
        Assert.Single(customThresholdRisky.Reasons);

        var round2WithoutVerdicts = MergePolicy.Classify(new PolicyInput(["src/App.cs"], 10, DefaultProtected, [], Round: 2));
        Assert.Equal(RiskClass.NeedsAttention, round2WithoutVerdicts.RiskClass);
        Assert.Equal(2, round2WithoutVerdicts.Reasons.Count);

        var input = new PolicyInput(
            ["src/Security/Auth.cs", "src/App/App.csproj"],
            450,
            [.. DefaultProtected, "src/Security/**"],
            UnanimousApproved,
            Round: 2);

        var first = MergePolicy.Classify(input);
        for (var i = 0; i < 10; i++)
        {
            var next = MergePolicy.Classify(input);
            Assert.Equal(first.RiskClass, next.RiskClass);
            Assert.Equal(first.Action, next.Action);
            Assert.Equal(first.MatchedProtectedPaths, next.MatchedProtectedPaths);
            Assert.Equal(first.Reasons, next.Reasons);
        }
    }

    // --- 4. PullRequestBodyBuilder con PolicyResult ---

    [Fact]
    public void PullRequestBodyBuilder_formats_minor_and_needs_attention_titles_and_bodies()
    {
        var redactor = new NoOpRedactor();
        var spec = new Spec(
            Problem: "Actualiza configuración de seguridad.",
            ProbableCause: "Falta validación de cabecera.",
            ProposedChange: "Añadir validación.",
            AcceptanceCriteria: ["Test pasa"],
            AffectedFiles: ["src/Security/Auth.cs"]);
        var report = new TestReport(true, 1, 1, 0, 0, ["Tests.Auth"], [], TimeSpan.FromMilliseconds(50));

        var minorPolicy = new PolicyResult(RiskClass.Minor, PolicyAction.CreatePullRequest, [], []);
        var minorTitle = PullRequestBodyBuilder.BuildTitle(10, spec.Problem, redactor, minorPolicy);
        var minorBody = PullRequestBodyBuilder.BuildBody(Guid.CreateVersion7(), Guid.CreateVersion7(), "acme", "app", 10, spec, [], report, redactor, policy: minorPolicy);

        Assert.DoesNotContain(PullRequestBodyBuilder.NeedsAttentionTitlePrefix, minorTitle, StringComparison.Ordinal);
        Assert.Contains("## Clasificación de riesgo", minorBody, StringComparison.Ordinal);
        Assert.Contains("`Minor`", minorBody, StringComparison.Ordinal);

        var attentionPolicy = new PolicyResult(
            RiskClass.NeedsAttention,
            PolicyAction.CreatePullRequest,
            ["src/Security/Auth.cs"],
            ["Toca rutas protegidas (1): src/Security/Auth.cs", "El diff supera el umbral de cambios menores (450 > 400 líneas)"]);
        var attentionTitle = PullRequestBodyBuilder.BuildTitle(10, spec.Problem, redactor, attentionPolicy);
        var attentionBody = PullRequestBodyBuilder.BuildBody(Guid.CreateVersion7(), Guid.CreateVersion7(), "acme", "app", 10, spec, [], report, redactor, policy: attentionPolicy);

        Assert.StartsWith(PullRequestBodyBuilder.NeedsAttentionTitlePrefix, attentionTitle, StringComparison.Ordinal);
        Assert.Contains("## Clasificación de riesgo", attentionBody, StringComparison.Ordinal);
        Assert.Contains("`NeedsAttention`", attentionBody, StringComparison.Ordinal);
        Assert.Contains("450 > 400", attentionBody, StringComparison.Ordinal);
        Assert.Contains("`src/Security/Auth.cs`", attentionBody, StringComparison.Ordinal);
    }

    // --- 5. E2E en RunOrchestrator: Minor vs NeedsAttention (rutas protegidas + ronda 2) ---

    [Fact]
    public async Task End_to_end_minor_change_emits_RiskClassified_and_opens_standard_pr()
    {
        using var repos = new LocalGitRepos();
        repos.Create("acme", "minorrepo", new Dictionary<string, string>
        {
            ["README.md"] = "# Minor\n",
            ["src/App.cs"] = "public class App { public int Value => 1; }\n",
        });

        var settings = new Dictionary<string, string?>(CodeNetixFactory.CompleteSettings)
        {
            [CodeNetixFactory.CloneBaseUrlKey] = repos.BaseUrl,
        };

        await using var factory = new CodeNetixFactory(_data, _webRoot, settings);
        factory.Sandbox.OnSessionCreated = session =>
        {
            session.ExportWorkspaceHandler = async (dest, ct) =>
            {
                await WriteTarAsync(dest,
                [
                    ("workspace/README.md", "# Minor\n"),
                    ("workspace/src/App.cs", "public class App { public int Value => 2; }\n"),
                ], ct);
            };
        };

        using var client = factory.CreateAuthorizedClient();
        var run = await RunToTerminalAsync(client, "https://github.com/acme/minorrepo/issues/1");

        Assert.Equal("PullRequestCreated", run.GetProperty("state").GetString());
        var events = run.GetProperty("events").EnumerateArray().ToList();
        var riskEvent = Assert.Single(events, e => e.GetProperty("type").GetString() == "RiskClassified");
        Assert.Equal("Minor", riskEvent.GetProperty("riskClass").GetString());
        Assert.Equal(2, riskEvent.GetProperty("diffLines").GetInt32());
        Assert.Empty(riskEvent.GetProperty("matchedProtectedPaths").EnumerateArray());
        Assert.Equal(JsonValueKind.String, riskEvent.GetProperty("stageId").ValueKind);

        var pr = Assert.Single(factory.GitHub!.PullRequestsOn("acme", "minorrepo"));
        Assert.DoesNotContain(PullRequestBodyBuilder.NeedsAttentionTitlePrefix, pr.Title, StringComparison.Ordinal);
        Assert.Contains("## Clasificación de riesgo", pr.Body, StringComparison.Ordinal);
        Assert.Contains("`Minor`", pr.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task End_to_end_protected_paths_change_marks_pr_NeedsAttention_and_lists_paths()
    {
        using var repos = new LocalGitRepos();
        repos.Create("acme", "riskyrepo", new Dictionary<string, string>
        {
            ["codenetix.yml"] = "version: 1\nprotected_paths:\n  - \"src/Security/**\"\n",
            ["src/App/App.csproj"] = "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>\n",
            ["src/Security/Auth.cs"] = "public class Auth { public bool Allow() => false; }\n",
        });

        var settings = new Dictionary<string, string?>(CodeNetixFactory.CompleteSettings)
        {
            [CodeNetixFactory.CloneBaseUrlKey] = repos.BaseUrl,
        };

        await using var factory = new CodeNetixFactory(_data, _webRoot, settings);
        factory.Sandbox.OnSessionCreated = session =>
        {
            session.ExportWorkspaceHandler = async (dest, ct) =>
            {
                await WriteTarAsync(dest,
                [
                    ("workspace/codenetix.yml", "version: 1\nprotected_paths:\n  - \"src/Security/**\"\n"),
                    ("workspace/src/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup /></Project>\n"),
                    ("workspace/src/Security/Auth.cs", "public class Auth { public bool Allow() => true; }\n"),
                ], ct);
            };
        };

        using var client = factory.CreateAuthorizedClient();
        var run = await RunToTerminalAsync(client, "https://github.com/acme/riskyrepo/issues/2");

        Assert.Equal("PullRequestCreated", run.GetProperty("state").GetString());
        var events = run.GetProperty("events").EnumerateArray().ToList();
        var riskEvent = Assert.Single(events, e => e.GetProperty("type").GetString() == "RiskClassified");
        Assert.Equal("NeedsAttention", riskEvent.GetProperty("riskClass").GetString());
        var matched = riskEvent.GetProperty("matchedProtectedPaths").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(["src/App/App.csproj", "src/Security/Auth.cs"], matched);

        var pr = Assert.Single(factory.GitHub!.PullRequestsOn("acme", "riskyrepo"));
        Assert.StartsWith(PullRequestBodyBuilder.NeedsAttentionTitlePrefix, pr.Title, StringComparison.Ordinal);
        Assert.Contains("## Clasificación de riesgo", pr.Body, StringComparison.Ordinal);
        Assert.Contains("`NeedsAttention`", pr.Body, StringComparison.Ordinal);
        Assert.Contains("`src/App/App.csproj`", pr.Body, StringComparison.Ordinal);
        Assert.Contains("`src/Security/Auth.cs`", pr.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task End_to_end_round_2_approval_marks_pr_NeedsAttention()
    {
        using var repos = new LocalGitRepos();
        repos.Create("acme", "round2repo", new Dictionary<string, string>
        {
            ["src/App.cs"] = "public class App { public int Value => 1; }\n",
        });

        var settings = new Dictionary<string, string?>(CodeNetixFactory.CompleteSettings)
        {
            [CodeNetixFactory.CloneBaseUrlKey] = repos.BaseUrl,
        };

        await using var factory = new CodeNetixFactory(_data, _webRoot, settings);
        factory.Sandbox.OnSessionCreated = session =>
        {
            session.ExportWorkspaceHandler = async (dest, ct) =>
            {
                await WriteTarAsync(dest,
                [
                    ("workspace/src/App.cs", "public class App { public int Value => 2; }\n"),
                ], ct);
            };
        };

        var securityCalls = 0;
        factory.ChatClient.Handler = (call, _) =>
        {
            var system = call.Messages.First(m => m.Role == ChatRole.System).Text;
            if (!system.Contains("Reviewer of CodeNetix", StringComparison.Ordinal))
            {
                return Task.FromResult(FakeChatClient.ValidAnalysisJson);
            }

            if (system.Contains("Security Reviewer", StringComparison.Ordinal) && Interlocked.Increment(ref securityCalls) == 1)
            {
                return Task.FromResult(
                    """
                    {
                      "decision": "changes_requested",
                      "summary": "Añade validación extra.",
                      "findings": [
                        {
                          "severity": "medium",
                          "file": "src/App.cs",
                          "line": 1,
                          "message": "Validar entrada."
                        }
                      ]
                    }
                    """);
            }

            return Task.FromResult(FakeChatClient.ValidVerdictJson);
        };

        using var client = factory.CreateAuthorizedClient();
        var run = await RunToTerminalAsync(client, "https://github.com/acme/round2repo/issues/3");

        Assert.Equal("PullRequestCreated", run.GetProperty("state").GetString());
        var events = run.GetProperty("events").EnumerateArray().ToList();
        var riskEvent = Assert.Single(events, e => e.GetProperty("type").GetString() == "RiskClassified");
        Assert.Equal("NeedsAttention", riskEvent.GetProperty("riskClass").GetString());
        Assert.Empty(riskEvent.GetProperty("matchedProtectedPaths").EnumerateArray());

        var pr = Assert.Single(factory.GitHub!.PullRequestsOn("acme", "round2repo"));
        Assert.StartsWith(PullRequestBodyBuilder.NeedsAttentionTitlePrefix, pr.Title, StringComparison.Ordinal);
        Assert.Contains("`NeedsAttention`", pr.Body, StringComparison.Ordinal);
        Assert.Contains("ronda 2", pr.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task End_to_end_protected_paths_use_frozen_BaseSha_config_even_if_sandbox_removes_codenetix_yml()
    {
        using var repos = new LocalGitRepos();
        repos.Create("acme", "ad10repo", new Dictionary<string, string>
        {
            ["codenetix.yml"] = "version: 1\nprotected_paths:\n  - \"src/Security/**\"\n",
            ["src/Security/Auth.cs"] = "public class Auth { public bool Allow() => false; }\n",
        });

        var settings = new Dictionary<string, string?>(CodeNetixFactory.CompleteSettings)
        {
            [CodeNetixFactory.CloneBaseUrlKey] = repos.BaseUrl,
        };

        await using var factory = new CodeNetixFactory(_data, _webRoot, settings);
        factory.Sandbox.OnSessionCreated = session =>
        {
            session.ExportWorkspaceHandler = async (dest, ct) =>
            {
                // El Sandbox borra/sobrescribe codenetix.yml quitando src/Security/**; AD-10 obliga a usar la copia congelada de BaseSha.
                await WriteTarAsync(dest,
                [
                    ("workspace/codenetix.yml", "version: 1\n"),
                    ("workspace/src/Security/Auth.cs", "public class Auth { public bool Allow() => true; }\n"),
                ], ct);
            };
        };

        using var client = factory.CreateAuthorizedClient();
        var run = await RunToTerminalAsync(client, "https://github.com/acme/ad10repo/issues/4");

        Assert.Equal("PullRequestCreated", run.GetProperty("state").GetString());
        var events = run.GetProperty("events").EnumerateArray().ToList();
        var riskEvent = Assert.Single(events, e => e.GetProperty("type").GetString() == "RiskClassified");
        Assert.Equal("NeedsAttention", riskEvent.GetProperty("riskClass").GetString());
        var matched = riskEvent.GetProperty("matchedProtectedPaths").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(["src/Security/Auth.cs"], matched);

        var pr = Assert.Single(factory.GitHub!.PullRequestsOn("acme", "ad10repo"));
        Assert.StartsWith(PullRequestBodyBuilder.NeedsAttentionTitlePrefix, pr.Title, StringComparison.Ordinal);
        Assert.Contains("`src/Security/Auth.cs`", pr.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ComputeDiffAsync_reports_old_protected_path_when_protected_file_is_renamed()
    {
        using var repos = new LocalGitRepos();
        var baseSha = repos.Create("acme", "renamerepo", new Dictionary<string, string>
        {
            ["src/Security/Auth.cs"] = "public class Auth { public bool Allow(string token) => !string.IsNullOrEmpty(token); }\n",
        });

        var workspaceDir = Path.Combine(_root, "ws-rename");
        var guard = new GitSafetyGuard(NullLogger<GitSafetyGuard>.Instance);
        var options = Options.Create(new GitHubOptions
        {
            CloneBaseUrl = repos.BaseUrl,
            WorkspacesDirectory = workspaceDir,
            Token = "ghp_renametoken0123456789abcdefghij",
        });
        var workspace = new RepositoryWorkspace(options, NullLogger<RepositoryWorkspace>.Instance, guard);
        var runId = Guid.CreateVersion7();

        await workspace.PrepareAsync(runId, "acme", "renamerepo", Ct);

        using var tar = new MemoryStream();
        await WriteTarAsync(tar,
        [
            ("workspace/src/App/Auth.cs", "public class Auth { public bool Allow(string token) => !string.IsNullOrEmpty(token); }\n"),
        ], Ct);
        tar.Position = 0;

        var diff = await workspace.ComputeDiffAsync(runId, tar, baseSha, Ct);

        Assert.Contains("src/Security/Auth.cs", diff.ChangedFiles);
        Assert.Contains("src/App/Auth.cs", diff.ChangedFiles);

        var policy = MergePolicy.Classify(new PolicyInput(
            diff.ChangedFiles,
            MergePolicy.CountDiffLines(diff.UnifiedDiff),
            [.. DefaultProtected, "src/Security/**"],
            UnanimousApproved,
            Round: 1));
        Assert.Equal(RiskClass.NeedsAttention, policy.RiskClass);
        Assert.Equal(["src/Security/Auth.cs"], policy.MatchedProtectedPaths);
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

    private sealed class NoOpRedactor : ISecretRedactor
    {
        public string Redact(string value) => value;
    }
}
