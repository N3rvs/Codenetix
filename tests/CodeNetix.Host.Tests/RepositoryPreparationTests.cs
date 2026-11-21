using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CodeNetix.Contracts;
using CodeNetix.GitHub;
using CodeNetix.Persistence;
using CodeNetix.Pipeline.Features.RunPipeline;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CodeNetix.Host.Tests;

/// <summary>
/// Matriz de la historia 1.7: clonado con credenciales efímeras (<see cref="RepositoryWorkspace"/>) y paso 1 del
/// orquestador de punta a punta, con repositorios git locales (<see cref="LocalGitRepos"/>).
/// </summary>
public sealed class RepositoryPreparationTests : IDisposable
{
    private const string Token = "ghp_testtoken0123456789abcdefghijklmn"; // el de CompleteSettings
    private const string Owner = "acme";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "codenetix-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _dataDirectory;
    private readonly string _webRoot;
    private readonly LocalGitRepos _repos = new();

    public RepositoryPreparationTests()
    {
        _dataDirectory = Path.Combine(_root, "data");
        _webRoot = Path.Combine(_root, "wwwroot");
        Directory.CreateDirectory(_webRoot);
        File.WriteAllText(Path.Combine(_webRoot, "index.html"), "<!doctype html><html><body>Panel</body></html>");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string WorkspacesDirectory => Path.Combine(_dataDirectory, "workspaces");

    private static string Basic(string token) => Convert.ToBase64String(Encoding.UTF8.GetBytes($"x-access-token:{token}"));

    // --- RepositoryWorkspace ---------------------------------------------------------------

    private RepositoryWorkspace CreateWorkspace(string? cloneBaseUrl = null, string? token = Token) =>
        new(
            Options.Create(new GitHubOptions
            {
                Token = token,
                CloneBaseUrl = cloneBaseUrl ?? _repos.BaseUrl,
                WorkspacesDirectory = WorkspacesDirectory,
            }),
            NullLogger<RepositoryWorkspace>.Instance);

    [Fact]
    public async Task Clone_without_codenetix_yml_pins_the_base_sha_and_returns_no_config()
    {
        var sha = _repos.Create(Owner, "plain");
        var workspace = CreateWorkspace();
        var runId = Guid.CreateVersion7();

        var prepared = await workspace.PrepareAsync(runId, Owner, "plain", Ct);

        Assert.Equal(sha, prepared.BaseSha);
        Assert.Null(prepared.RepoConfigYaml);
        Assert.Equal(Path.Combine(Path.GetFullPath(WorkspacesDirectory), RunShortId.Of(runId)), prepared.WorkspacePath);
        Assert.True(File.Exists(Path.Combine(prepared.WorkspacePath, "README.md")));

        // Clon superficial de la rama por defecto, sin etiquetas.
        Assert.Equal("1", LocalGitRepos.Git(prepared.WorkspacePath, "rev-list", "--count", "HEAD").Trim());
        Assert.Equal(string.Empty, LocalGitRepos.Git(prepared.WorkspacePath, "tag", "--list").Trim());

        await workspace.DeleteAsync(runId, Ct);
        Assert.False(Directory.Exists(prepared.WorkspacePath));
    }

    [Fact]
    public async Task Clone_reads_codenetix_yml_and_follows_the_default_branch_head()
    {
        _repos.Create(Owner, "configured", new Dictionary<string, string> { ["codenetix.yml"] = "version: 1\nreviewers: [tests]\n" });
        var head = _repos.Commit(Owner, "configured", "codenetix.yml", "version: 1\nreviewers: [security]\n");
        var workspace = CreateWorkspace();
        var runId = Guid.CreateVersion7();

        var prepared = await workspace.PrepareAsync(runId, Owner, "configured", Ct);

        Assert.Equal(head, prepared.BaseSha);
        Assert.Equal("version: 1\nreviewers: [security]\n", prepared.RepoConfigYaml);
        await workspace.DeleteAsync(runId, Ct);
    }

    [Fact]
    public async Task Credentials_never_reach_the_clone_configuration()
    {
        _repos.Create(Owner, "secret");
        var workspace = CreateWorkspace();
        var runId = Guid.CreateVersion7();

        var prepared = await workspace.PrepareAsync(runId, Owner, "secret", Ct);

        var gitConfig = await File.ReadAllTextAsync(Path.Combine(prepared.WorkspacePath, ".git", "config"), Ct);
        var localConfig = LocalGitRepos.Git(prepared.WorkspacePath, "config", "--list", "--local");
        foreach (var text in new[] { gitConfig, localConfig })
        {
            Assert.DoesNotContain("extraheader", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Token, text, StringComparison.Ordinal);
            Assert.DoesNotContain(Basic(Token), text, StringComparison.Ordinal);
        }

        var origin = LocalGitRepos.Git(prepared.WorkspacePath, "remote", "get-url", "origin").Trim();
        Assert.Equal(workspace.CloneUrlOf(Owner, "secret"), origin);
        Assert.Empty(new Uri(origin).UserInfo);
        await workspace.DeleteAsync(runId, Ct);
    }

    [Fact]
    public async Task Leftover_credentials_in_the_clone_are_detected()
    {
        _repos.Create(Owner, "leaky");
        var workspace = CreateWorkspace();
        var runId = Guid.CreateVersion7();
        var prepared = await workspace.PrepareAsync(runId, Owner, "leaky", Ct);

        LocalGitRepos.Git(prepared.WorkspacePath, "config", "--local", "http.extraHeader", "Authorization: Basic " + Basic(Token));
        var header = await Assert.ThrowsAsync<RepositoryPreparationException>(() => workspace.VerifyNoCredentialsAsync(prepared.WorkspacePath, Token, Ct));

        LocalGitRepos.Git(prepared.WorkspacePath, "config", "--local", "--unset-all", "http.extraHeader");
        LocalGitRepos.Git(prepared.WorkspacePath, "remote", "set-url", "origin", $"https://x-access-token:{Token}@github.com/{Owner}/leaky.git");
        var userInfo = await Assert.ThrowsAsync<RepositoryPreparationException>(() => workspace.VerifyNoCredentialsAsync(prepared.WorkspacePath, Token, Ct));

        foreach (var ex in new[] { header, userInfo })
        {
            Assert.Contains("credenciales", ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(Token, ex.Message, StringComparison.Ordinal);
        }

        await workspace.DeleteAsync(runId, Ct);
        Assert.False(Directory.Exists(prepared.WorkspacePath));
    }

    [Fact]
    public async Task Config_is_read_from_the_base_sha_not_from_the_working_tree()
    {
        _repos.Create(Owner, "tampered", new Dictionary<string, string> { ["codenetix.yml"] = "version: 1\nmax_review_rounds: 1\n" });
        var workspace = CreateWorkspace();
        var runId = Guid.CreateVersion7();
        var prepared = await workspace.PrepareAsync(runId, Owner, "tampered", Ct);

        // Se modifica (y se borra después) el archivo en el árbol de trabajo tras el clon.
        var file = Path.Combine(prepared.WorkspacePath, "codenetix.yml");
        await File.WriteAllTextAsync(file, "version: 1\nmax_review_rounds: 5\n", Ct);
        var afterEdit = await workspace.ReadRepoConfigAsync(prepared.WorkspacePath, prepared.BaseSha, Token, Ct);
        File.Delete(file);
        var afterDelete = await workspace.ReadRepoConfigAsync(prepared.WorkspacePath, prepared.BaseSha, Token, Ct);

        Assert.Equal("version: 1\nmax_review_rounds: 1\n", afterEdit);
        Assert.Equal(afterEdit, afterDelete);
        await workspace.DeleteAsync(runId, Ct);
    }

    [Fact]
    public async Task A_directory_named_codenetix_yml_is_not_a_config()
    {
        _repos.Create(Owner, "dir", new Dictionary<string, string> { ["codenetix.yml/readme.txt"] = "x" });
        var workspace = CreateWorkspace();
        var runId = Guid.CreateVersion7();

        var prepared = await workspace.PrepareAsync(runId, Owner, "dir", Ct);

        Assert.Null(prepared.RepoConfigYaml);
        await workspace.DeleteAsync(runId, Ct);
    }

    [Fact]
    public async Task Failed_clone_deletes_the_tree_and_never_reports_the_token()
    {
        var workspace = CreateWorkspace();
        var runId = Guid.CreateVersion7();

        var ex = await Assert.ThrowsAsync<RepositoryPreparationException>(() => workspace.PrepareAsync(runId, Owner, "missing", Ct));

        Assert.StartsWith($"no se ha podido clonar {Owner}/missing: ", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Basic(Token), ex.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(workspace.WorkspacePathOf(runId)));
    }

    [Fact]
    public async Task Clone_over_http_sends_the_token_only_in_the_header_and_reports_the_failure()
    {
        // Un servidor HTTP que rechaza todo: el clon falla, pero se ve qué ha enviado git.
        await using var server = await FakeGitHub.StartAsync(Ct);
        var workspace = CreateWorkspace(cloneBaseUrl: server.BaseUrl);
        var runId = Guid.CreateVersion7();

        var ex = await Assert.ThrowsAsync<RepositoryPreparationException>(() => workspace.PrepareAsync(runId, Owner, "remote", Ct));

        Assert.DoesNotContain(Token, ex.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(workspace.WorkspacePathOf(runId)));
        var requests = server.Requests;
        Assert.NotEmpty(requests);
        Assert.All(requests, r =>
        {
            Assert.Equal("Basic " + Basic(Token), r.Authorization);
            Assert.DoesNotContain(Token, r.Path, StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData("https://user:pass@github.com")]
    [InlineData("ssh://github.com")]
    [InlineData("no es una url")]
    [InlineData("https://github.com?x=1")]
    public void Clone_base_url_with_credentials_or_unsupported_scheme_is_rejected(string baseUrl)
    {
        var workspace = CreateWorkspace(cloneBaseUrl: baseUrl);

        var ex = Assert.Throws<RepositoryPreparationException>(() => workspace.CloneUrlOf(Owner, "r"));

        Assert.Contains("CloneBaseUrl", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("pass", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("..", "r")]
    [InlineData(".", "r")]
    [InlineData("", "r")]
    [InlineData("a/b", "r")]
    [InlineData("acme", "r#1")]
    [InlineData("acme", "my repo")]
    [InlineData("acme", "%2e%2e")]
    [InlineData("acme", "..")]
    [InlineData("acme", "")]
    [InlineData("acme", "r?x=1")]
    [InlineData("acme", "r\\..")]
    [InlineData("-acme", "r")]
    [InlineData("acme", "-r")]
    [InlineData("acme", "--upload-pack=x")]
    public void Clone_url_rejects_unsafe_owner_or_repo(string owner, string repo)
    {
        var ex = Assert.Throws<RepositoryPreparationException>(() => CreateWorkspace(cloneBaseUrl: "https://github.com").CloneUrlOf(owner, repo));

        Assert.Equal("el nombre del repositorio no es válido", ex.Message);
    }

    [Theory]
    [InlineData("github:acme/app#7", true, "acme", "app", 7L)]
    [InlineData("github:acme-corp/my.repo_x#123", true, "acme-corp", "my.repo_x", 123L)]
    [InlineData("github:../r#1", false, "", "", 0L)]
    [InlineData("github:./r#1", false, "", "", 0L)]
    [InlineData("github:o/..#1", false, "", "", 0L)]
    [InlineData("github:/r#1", false, "", "", 0L)]
    [InlineData("github:o/#1", false, "", "", 0L)]
    [InlineData("github:o/r/x#1", false, "", "", 0L)]
    [InlineData("github:o/r#", false, "", "", 0L)]
    [InlineData("github:o/r", false, "", "", 0L)]
    [InlineData("github:o/r#x", false, "", "", 0L)]
    [InlineData("github:o/r#-1", false, "", "", 0L)]
    [InlineData("github:o/r##1", false, "", "", 0L)]
    [InlineData("github:o r/r#1", false, "", "", 0L)]
    [InlineData("github:o/%2e%2e#1", false, "", "", 0L)]
    [InlineData("github:o\\x/r#1", false, "", "", 0L)]
    [InlineData("github:-o/r#1", false, "", "", 0L)]
    [InlineData("github:o/-r#1", false, "", "", 0L)]
    [InlineData("sentry:acme/123", false, "", "", 0L)]
    [InlineData("", false, "", "", 0L)]
    [InlineData(null, false, "", "", 0L)]
    public void Object_key_parsing_accepts_only_safe_github_issues(string? key, bool valid, string owner, string repo, long number)
    {
        Assert.Equal(valid, GitHubObjectKey.TryParse(key, out var o, out var r, out var n));
        Assert.Equal((owner, repo, number), (o, r, n));
        if (valid)
        {
            Assert.Equal(key, GitHubObjectKey.Format(o, r, n));
        }
    }

    [Theory]
    [InlineData("https://github.com", "https://github.com/acme/r.git")]
    [InlineData("https://github.com/", "https://github.com/acme/r.git")]
    [InlineData("https://ghe.example.com/git", "https://ghe.example.com/git/acme/r.git")]
    public void Clone_url_is_built_from_the_base(string baseUrl, string expected) =>
        Assert.Equal(expected, CreateWorkspace(cloneBaseUrl: baseUrl).CloneUrlOf(Owner, "r"));

    [Fact]
    public async Task Clone_timeout_is_a_preparation_error()
    {
        _repos.Create(Owner, "slow");
        var workspace = new RepositoryWorkspace(
            Options.Create(new GitHubOptions { Token = Token, CloneBaseUrl = _repos.BaseUrl, WorkspacesDirectory = WorkspacesDirectory }),
            NullLogger<RepositoryWorkspace>.Instance)
        {
            CloneTimeout = TimeSpan.FromMilliseconds(1),
        };
        var runId = Guid.CreateVersion7();

        var ex = await Assert.ThrowsAsync<RepositoryPreparationException>(() => workspace.PrepareAsync(runId, Owner, "slow", Ct));

        Assert.Contains("tiempo máximo", ex.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(workspace.WorkspacePathOf(runId)));
    }

    // --- Orquestador de punta a punta ------------------------------------------------------

    private CodeNetixFactory CreateFactory(Dictionary<string, string?>? extra = null)
    {
        var settings = new Dictionary<string, string?>(CodeNetixFactory.CompleteSettings)
        {
            [CodeNetixFactory.CloneBaseUrlKey] = _repos.BaseUrl,
        };
        foreach (var (key, value) in extra ?? [])
        {
            settings[key] = value;
        }

        return new CodeNetixFactory(_dataDirectory, _webRoot, settings);
    }

    [Fact]
    public async Task Repository_without_config_reaches_Analyzing_with_the_default_snapshot_and_then_fails_StageError()
    {
        var sha = _repos.Create(Owner, "nocfg");
        await using var factory = CreateFactory();
        using var client = factory.CreateAuthorizedClient();

        var run = await RunToTerminalAsync(client, "nocfg");

        Assert.Equal("Failed", run.State);
        Assert.Equal("StageError", run.FailureReason);
        Assert.Equal(RunOrchestrator.Detail, run.FailureDetail);

        var events = await GetEventsAsync(client, run.RunId);
        Assert.Equal(
            [
                "RunCreated", "StateChanged", "RepositoryPrepared", "RepoConfigResolved", "StateChanged",
                "SandboxCreated", "SpecGenerated", "StateChanged", "OutputAppended", "StateChanged",
                "VerdictRecorded", "VerdictRecorded", "VerdictRecorded", "StateChanged", "SandboxReleased",
            ],
            events.Select(e => e.GetProperty("type").GetString()));
        Assert.Equal(sha, events[2].GetProperty("baseSha").GetString());
        Assert.Equal("defaults", events[3].GetProperty("source").GetString());
        Assert.Equal(("Received", "Analyzing"), (events[4].GetProperty("from").GetString(), events[4].GetProperty("to").GetString()));
        Assert.Equal(("Analyzing", "Coding"), (events[7].GetProperty("from").GetString(), events[7].GetProperty("to").GetString()));
        Assert.Equal(("Coding", "Reviewing"), (events[9].GetProperty("from").GetString(), events[9].GetProperty("to").GetString()));
        Assert.Equal(("Reviewing", "Failed"), (events[13].GetProperty("from").GetString(), events[13].GetProperty("to").GetString()));

        var (baseSha, config) = await ReadSnapshotAsync(run.RunId);
        Assert.Equal(sha, baseSha);
        Assert.NotNull(config);
        var root = config!.RootElement;
        Assert.Equal("defaults", root.GetProperty("source").GetString());
        Assert.Equal(["security", "quality", "tests"], Strings(root.GetProperty("reviewers")));
        Assert.Equal(["**/Migrations/**", "**/*.csproj", ".github/**"], Strings(root.GetProperty("protectedPaths")));
        Assert.Equal(2, root.GetProperty("maxReviewRounds").GetInt32());
        Assert.Equal(3, root.GetProperty("maxCodingAttempts").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("budgetUsd").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("models").ValueKind);

        await AssertWorkspaceDeletedAsync(run.ShortId);
    }

    [Fact]
    public async Task Valid_config_restricts_reviewers_and_rounds()
    {
        _repos.Create(Owner, "valid", new Dictionary<string, string> { ["codenetix.yml"] = "version: 1\nreviewers: [security]\nmax_review_rounds: 1\n" });
        await using var factory = CreateFactory();
        using var client = factory.CreateAuthorizedClient();

        var run = await RunToTerminalAsync(client, "valid");

        Assert.Equal("StageError", run.FailureReason);
        var root = (await ReadSnapshotAsync(run.RunId)).Config!.RootElement;
        Assert.Equal("file", root.GetProperty("source").GetString());
        Assert.Equal(["security"], Strings(root.GetProperty("reviewers")));
        Assert.Equal(1, root.GetProperty("maxReviewRounds").GetInt32());
        var events = await GetEventsAsync(client, run.RunId);
        Assert.Equal("file", events.Single(e => e.GetProperty("type").GetString() == "RepoConfigResolved").GetProperty("source").GetString());
    }

    [Fact]
    public async Task Repository_cannot_raise_an_instance_limit()
    {
        _repos.Create(Owner, "greedy", new Dictionary<string, string> { ["codenetix.yml"] = "version: 1\nmax_review_rounds: 5\nmax_coding_attempts: 5\nbudget_usd: 100\n" });
        await using var factory = CreateFactory(new()
        {
            ["CODENETIX:Limits:MaxReviewRounds"] = "2",
            ["CODENETIX:Limits:MaxCodingAttempts"] = "1",
            ["CODENETIX:Limits:BudgetUsd"] = "7.5",
        });
        using var client = factory.CreateAuthorizedClient();

        var run = await RunToTerminalAsync(client, "greedy");

        var root = (await ReadSnapshotAsync(run.RunId)).Config!.RootElement;
        Assert.Equal(2, root.GetProperty("maxReviewRounds").GetInt32());
        Assert.Equal(1, root.GetProperty("maxCodingAttempts").GetInt32());
        Assert.Equal(7.5m, root.GetProperty("budgetUsd").GetDecimal());
    }

    [Fact]
    public async Task Repository_paths_are_added_to_the_defaults()
    {
        _repos.Create(Owner, "paths", new Dictionary<string, string> { ["codenetix.yml"] = "version: 1\nprotected_paths: [\"src/Auth/**\"]\n" });
        await using var factory = CreateFactory();
        using var client = factory.CreateAuthorizedClient();

        var run = await RunToTerminalAsync(client, "paths");

        var root = (await ReadSnapshotAsync(run.RunId)).Config!.RootElement;
        Assert.Equal(["**/Migrations/**", "**/*.csproj", ".github/**", "src/Auth/**"], Strings(root.GetProperty("protectedPaths")));
    }

    [Theory]
    [InlineData("malformed", "version: 1\nreviewers:\n\t- security\n", "codenetix.yml: YAML no válido en la línea 3, columna 1")]
    [InlineData("hacker", "version: 1\nreviewers: [hacker]\n", "codenetix.yml: $.reviewers[0]: valor no admitido")]
    [InlineData("unknown", "version: 1\nextra: true\n", "codenetix.yml: $.extra: clave no admitida")]
    [InlineData("noversion", "reviewers: [security]\n", "codenetix.yml: $: falta la clave obligatoria 'version'")]
    [InlineData("version2", "version: 2\n", "codenetix.yml: $.version: la versión debe ser 1")]
    public async Task Invalid_config_fails_from_Received_with_InvalidRepoConfig_and_no_snapshot(string repo, string yaml, string expected)
    {
        var sha = _repos.Create(Owner, repo, new Dictionary<string, string> { ["codenetix.yml"] = yaml });
        await using var factory = CreateFactory();
        using var client = factory.CreateAuthorizedClient();

        var run = await RunToTerminalAsync(client, repo);

        Assert.Equal("Failed", run.State);
        Assert.Equal("InvalidRepoConfig", run.FailureReason);
        Assert.StartsWith(expected, run.FailureDetail, StringComparison.Ordinal);

        // Ni Analyzing ni configuración: RepositoryPrepared y el fallo, sin pasar por ningún agente.
        var events = await GetEventsAsync(client, run.RunId);
        Assert.Equal(["RunCreated", "StateChanged", "RepositoryPrepared", "StateChanged"], events.Select(e => e.GetProperty("type").GetString()));
        Assert.Equal("Received", events[3].GetProperty("from").GetString());
        Assert.Equal("InvalidRepoConfig", events[3].GetProperty("failureReason").GetString());
        Assert.DoesNotContain(events, e => e.TryGetProperty("to", out var to) && to.GetString() == "Analyzing");

        var (baseSha, config) = await ReadSnapshotAsync(run.RunId);
        Assert.Equal(sha, baseSha);
        Assert.Null(config);
        await AssertWorkspaceDeletedAsync(run.ShortId);
    }

    [Fact]
    public async Task Clone_failure_fails_with_CloneFailed_without_the_token_and_deletes_the_tree()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateAuthorizedClient();

        var run = await RunToTerminalAsync(client, "doesnotexist");

        Assert.Equal("Failed", run.State);
        Assert.Equal("CloneFailed", run.FailureReason);
        Assert.StartsWith($"no se ha podido clonar {Owner}/doesnotexist: ", run.FailureDetail, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, run.FailureDetail, StringComparison.Ordinal);

        var events = await GetEventsAsync(client, run.RunId);
        Assert.Equal(["RunCreated", "StateChanged", "StateChanged"], events.Select(e => e.GetProperty("type").GetString()));
        Assert.Equal("CloneFailed", events[2].GetProperty("failureReason").GetString());
        Assert.Null((await ReadSnapshotAsync(run.RunId)).BaseSha);
        await AssertWorkspaceDeletedAsync(run.ShortId);
    }

    [Fact]
    public async Task The_token_appears_in_no_RunEvent_nor_run_row()
    {
        _repos.Create(Owner, "tokencheck", new Dictionary<string, string> { ["codenetix.yml"] = "version: 1\n" });
        await using var factory = CreateFactory();
        using var client = factory.CreateAuthorizedClient();

        var run = await RunToTerminalAsync(client, "tokencheck");

        await using var connection = await OpenReadOnlyAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload_json FROM run_events UNION ALL SELECT COALESCE(effective_config_json, '') || COALESCE(failure_detail, '') || COALESCE(base_sha, '') FROM run_runs;";
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = 0;
        while (await reader.ReadAsync(Ct))
        {
            rows++;
            var text = reader.GetString(0);
            Assert.DoesNotContain(Token, text, StringComparison.Ordinal);
            Assert.DoesNotContain(Basic(Token), text, StringComparison.Ordinal);
            Assert.DoesNotContain("extraheader", text, StringComparison.OrdinalIgnoreCase);
        }

        Assert.True(rows >= 6, $"Solo {rows} filas.");
        Assert.Equal("StageError", run.FailureReason);
    }

    // --- Utilidades ------------------------------------------------------------------------

    private static List<string?> Strings(JsonElement array) => [.. array.EnumerateArray().Select(e => e.GetString())];

    private async Task<RunBody> RunToTerminalAsync(HttpClient client, string repo)
    {
        using var submit = await client.PostAsJsonAsync(
            "/api/events/manual",
            new { url = $"https://github.com/{Owner}/{repo}/issues/7", requestId = Guid.NewGuid().ToString() },
            Ct);
        Assert.Equal(HttpStatusCode.Created, submit.StatusCode);
        var eventId = (await submit.Content.ReadFromJsonAsync<EventBody>(Ct))!.EventId;

        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            using var eventResponse = await client.GetAsync($"/api/events/{eventId}", Ct);
            var info = (await eventResponse.Content.ReadFromJsonAsync<EventBody>(Ct))!;
            if (info.RunId is { } runId)
            {
                using var runResponse = await client.GetAsync($"/api/runs/{runId}", Ct);
                var run = (await runResponse.Content.ReadFromJsonAsync<RunBody>(Ct))!;
                if (run.State is "Failed" or "Cancelled" or "PullRequestCreated")
                {
                    // Si se creó el Sandbox, se espera a su liberación (el último paso del orquestador).
                    var types = (await GetEventsAsync(client, runId)).Select(e => e.GetProperty("type").GetString()).ToList();
                    if (!types.Contains("SandboxCreated") || types.Contains("SandboxReleased"))
                    {
                        return run;
                    }
                }
            }

            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30), $"La Ejecución del Evento {eventId} no ha terminado.");
            await Task.Delay(50, Ct);
        }
    }

    private static async Task<List<JsonElement>> GetEventsAsync(HttpClient client, Guid runId)
    {
        using var response = await client.GetAsync($"/api/runs/{runId}/events", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return [.. json.RootElement.EnumerateArray().Select(e => e.Clone())];
    }

    private async Task<(string? BaseSha, JsonDocument? Config)> ReadSnapshotAsync(Guid runId)
    {
        await using var connection = await OpenReadOnlyAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT base_sha, effective_config_json FROM run_runs WHERE id = $id;";
        command.Parameters.AddWithValue("$id", runId.ToString().ToUpperInvariant());
        await using var reader = await command.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));
        var baseSha = reader.IsDBNull(0) ? null : reader.GetString(0);
        var config = reader.IsDBNull(1) ? null : JsonDocument.Parse(reader.GetString(1));
        return (baseSha, config);
    }

    /// <summary>El árbol se borra en el <c>finally</c> del orquestador, justo después de confirmar el Estado terminal.</summary>
    private async Task AssertWorkspaceDeletedAsync(string shortId)
    {
        var path = Path.Combine(WorkspacesDirectory, shortId);
        var stopwatch = Stopwatch.StartNew();
        while (Directory.Exists(path) && stopwatch.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(50, Ct);
        }

        Assert.False(Directory.Exists(path), $"El árbol de trabajo '{path}' sigue existiendo.");
    }

    private async Task<SqliteConnection> OpenReadOnlyAsync()
    {
        var connection = new SqliteConnection($"Data Source={Path.Combine(_dataDirectory, PersistenceOptions.DatabaseFileName)};Mode=ReadOnly");
        await connection.OpenAsync(Ct);
        return connection;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        LocalGitRepos.DeleteDirectory(_root);
        _repos.Dispose();
    }

    private sealed record EventBody(Guid EventId, string? Status, Guid? RunId);

    private sealed record RunBody(Guid RunId, string ShortId, string State, string? FailureReason, string? FailureDetail);
}
