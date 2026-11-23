using System.Diagnostics;
using System.Formats.Tar;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CodeNetix.Contracts;
using CodeNetix.GitHub.Features.NotifyIssue;
using CodeNetix.Persistence;
using CodeNetix.Pipeline.Data;
using CodeNetix.Pipeline.Domain;
using CodeNetix.Pipeline.Events;
using CodeNetix.Pipeline.Features.RunPipeline;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace CodeNetix.Host.Tests;

/// <summary>
/// Matriz de la Historia 3.5 (FR-19, AD-8, AD-16 paso 7): aviso terminal idempotente en la issue de GitHub
/// para <c>PullRequestCreated</c>, <c>Failed</c> (sin duplicar <c>NotAutomatable</c>), <c>Cancelled</c>,
/// idempotencia por <c>(runId, kind)</c>, orígenes no GitHub y tolerancia a errores de GitHub.
/// </summary>
public sealed class TerminalNotificationTests : IDisposable
{
    private const string GitHubToken = "ghp_testtoken0123456789abcdefghijklmn";
    private const string AnthropicKey = "sk-ant-testkey0123456789abcdefghijklmn";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "codenetix-term-notify-" + Guid.NewGuid().ToString("N"));
    private readonly string _dataDirectory;
    private readonly string _webRoot;
    private int _issueNumber = 700;

    public TerminalNotificationTests()
    {
        _dataDirectory = Path.Combine(_root, "data");
        _webRoot = Path.Combine(_root, "wwwroot");
        Directory.CreateDirectory(_webRoot);
        File.WriteAllText(Path.Combine(_webRoot, "index.html"), "<!doctype html><html><body>Panel</body></html>");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private CodeNetixFactory CreateFactory(Dictionary<string, string?>? extra = null)
    {
        var settings = new Dictionary<string, string?>(CodeNetixFactory.CompleteSettings);
        foreach (var (key, value) in extra ?? [])
        {
            settings[key] = value;
        }

        return new CodeNetixFactory(_dataDirectory, _webRoot, settings);
    }

    // --- 1. Cierre en PullRequestCreated ----------------------------------------------------

    [Fact]
    public async Task PullRequestCreated_posts_one_comment_with_pr_link_shortId_and_risk_class()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateAuthorizedClient();

        var run = await RunToTerminalAsync(client);

        Assert.Equal("PullRequestCreated", run.State);
        var pr = Assert.Single(factory.GitHub!.PullRequestsOn("o", "r"));
        var comment = Assert.Single(factory.GitHub.CommentsOn("o", "r", run.Number));
        Assert.Equal(1, factory.GitHub.CommentPosts);

        Assert.Contains(TerminalNotificationComment.PullRequestCreatedTitle, comment.Body, StringComparison.Ordinal);
        Assert.Contains($"[#{pr.Number}]({pr.HtmlUrl})", comment.Body, StringComparison.Ordinal);
        Assert.Contains(pr.HtmlUrl, comment.Body, StringComparison.Ordinal);
        Assert.Contains($"#{pr.Number}", comment.Body, StringComparison.Ordinal);
        Assert.Contains(RunShortId.Of(run.RunId), comment.Body, StringComparison.Ordinal);
        Assert.Contains("Minor", comment.Body, StringComparison.Ordinal);
        Assert.EndsWith(IssueNotifier.Marker(run.RunId, NotificationKind.PullRequestCreated), comment.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(GitHubToken, comment.Body, StringComparison.Ordinal);

        var events = Events(run);
        var commented = Assert.Single(events, e => e.GetProperty("type").GetString() == "IssueCommented");
        Assert.Equal("PullRequestCreated", commented.GetProperty("kind").GetString());
        Assert.Equal(comment.HtmlUrl, commented.GetProperty("commentUrl").GetString());
        Assert.DoesNotContain(events, e => e.GetProperty("type").GetString() == "NotificationFailed");

        // gh_notifications: exactamente 1 fila Posted con kind = PullRequestCreated.
        var (kind, status, commentId, commentUrl, error) = await ReadNotificationAsync(run.RunId);
        Assert.Equal(("PullRequestCreated", "Posted", comment.Id, comment.HtmlUrl), (kind, status, commentId, commentUrl));
        Assert.Null(error);
    }

    [Fact]
    public async Task PullRequestCreated_with_NeedsAttention_includes_risk_class_and_protected_paths()
    {
        using var repos = new LocalGitRepos();
        repos.Create("acme", "risky", new Dictionary<string, string>
        {
            ["README.md"] = "# Risky\n",
            ["src/App/App.csproj"] = "<Project Sdk=\"Microsoft.NET.Sdk\" />\n",
        });

        await using var factory = CreateFactory(new() { [CodeNetixFactory.CloneBaseUrlKey] = repos.BaseUrl });
        factory.Sandbox.OnSessionCreated = session =>
        {
            session.ExportWorkspaceHandler = async (dest, ct) =>
            {
                await WriteTarAsync(dest,
                [
                    ("workspace/README.md", "# Risky\n"),
                    ("workspace/src/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><Nullable>enable</Nullable></PropertyGroup></Project>\n"),
                ], ct);
            };
        };

        using var client = factory.CreateAuthorizedClient();
        var run = await RunToTerminalAsync(client, "acme", "risky");

        Assert.Equal("PullRequestCreated", run.State);
        var pr = Assert.Single(factory.GitHub!.PullRequestsOn("acme", "risky"));
        var comment = Assert.Single(factory.GitHub.CommentsOn("acme", "risky", run.Number));
        Assert.Contains($"[#{pr.Number}]({pr.HtmlUrl})", comment.Body, StringComparison.Ordinal);
        Assert.Contains("NeedsAttention", comment.Body, StringComparison.Ordinal);
        Assert.Contains("src/App/App.csproj", comment.Body, StringComparison.Ordinal);
    }

    // --- 2. Cierre en Failed (no NotAutomatable): TestsFailed, ReviewRejected, ReviewRoundsExhausted, InvalidRepoConfig ---

    [Fact]
    public async Task Failed_TestsFailed_posts_one_RunFailed_comment_with_reason_and_redacted_detail()
    {
        await using var factory = CreateFactory(new() { ["CODENETIX:Limits:MaxCodingAttempts"] = "1" });
        factory.Sandbox.OnSessionCreated = session =>
        {
            session.ExecHandler = cmd =>
            {
                if (cmd.Executable == "claude")
                {
                    return new SandboxExecResult(1, string.Empty, $"Error CS0001 con clave {AnthropicKey}", TimedOut: false, Truncated: false);
                }

                return new SandboxExecResult(0, string.Empty, string.Empty, TimedOut: false, Truncated: false);
            };
        };

        using var client = factory.CreateAuthorizedClient();
        var run = await RunToTerminalAsync(client);

        Assert.Equal(("Failed", "TestsFailed"), (run.State, run.FailureReason));
        var comment = Assert.Single(factory.GitHub!.CommentsOn("o", "r", run.Number));
        Assert.Equal(1, factory.GitHub.CommentPosts);
        Assert.Contains(TerminalNotificationComment.RunFailedTitle, comment.Body, StringComparison.Ordinal);
        Assert.Contains("TestsFailed", comment.Body, StringComparison.Ordinal);
        Assert.Contains(RunShortId.Of(run.RunId), comment.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(AnthropicKey, comment.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(GitHubToken, comment.Body, StringComparison.Ordinal);
        Assert.EndsWith(IssueNotifier.Marker(run.RunId, NotificationKind.RunFailed), comment.Body, StringComparison.Ordinal);

        var commented = Assert.Single(Events(run), e => e.GetProperty("type").GetString() == "IssueCommented");
        Assert.Equal("RunFailed", commented.GetProperty("kind").GetString());
        Assert.Equal(comment.HtmlUrl, commented.GetProperty("commentUrl").GetString());

        var (kind, status, _, _, error) = await ReadNotificationAsync(run.RunId);
        Assert.Equal(("RunFailed", "Posted"), (kind, status));
        Assert.Null(error);
    }

    [Fact]
    public async Task Failed_ReviewRejected_posts_one_RunFailed_comment_with_reason_and_detail()
    {
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
                      "decision": "rejected",
                      "summary": "Inyección SQL detectada (secreto {{AnthropicKey}} no debe filtrarse).",
                      "findings": []
                    }
                    """);
            }

            return Task.FromResult(FakeChatClient.ValidVerdictJson);
        };

        using var client = factory.CreateAuthorizedClient();
        var run = await RunToTerminalAsync(client);

        Assert.Equal(("Failed", "ReviewRejected"), (run.State, run.FailureReason));
        var comment = Assert.Single(factory.GitHub!.CommentsOn("o", "r", run.Number));
        Assert.Contains("ReviewRejected", comment.Body, StringComparison.Ordinal);
        Assert.Contains("Inyección SQL detectada", comment.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(AnthropicKey, comment.Body, StringComparison.Ordinal);
        Assert.Contains(RunShortId.Of(run.RunId), comment.Body, StringComparison.Ordinal);

        var commented = Assert.Single(Events(run), e => e.GetProperty("type").GetString() == "IssueCommented");
        Assert.Equal("RunFailed", commented.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Failed_ReviewRoundsExhausted_posts_one_RunFailed_comment()
    {
        await using var factory = CreateFactory(new() { ["CODENETIX:Limits:MaxReviewRounds"] = "1" });
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
                    """
                    {
                      "decision": "changes_requested",
                      "summary": "Extrae el método auxiliar para reducir complejidad.",
                      "findings": []
                    }
                    """);
            }

            return Task.FromResult(FakeChatClient.ValidVerdictJson);
        };

        using var client = factory.CreateAuthorizedClient();
        var run = await RunToTerminalAsync(client);

        Assert.Equal(("Failed", "ReviewRoundsExhausted"), (run.State, run.FailureReason));
        var comment = Assert.Single(factory.GitHub!.CommentsOn("o", "r", run.Number));
        Assert.Contains("ReviewRoundsExhausted", comment.Body, StringComparison.Ordinal);
        Assert.Contains("Extrae el método auxiliar", comment.Body, StringComparison.Ordinal);
        Assert.Contains(RunShortId.Of(run.RunId), comment.Body, StringComparison.Ordinal);

        var (kind, status, _, _, _) = await ReadNotificationAsync(run.RunId);
        Assert.Equal(("RunFailed", "Posted"), (kind, status));
    }

    [Fact]
    public async Task Failed_InvalidRepoConfig_posts_one_RunFailed_comment()
    {
        using var repos = new LocalGitRepos();
        repos.Create("acme", "badcfg", new Dictionary<string, string>
        {
            ["codenetix.yml"] = "version: 2\n",
        });

        await using var factory = CreateFactory(new() { [CodeNetixFactory.CloneBaseUrlKey] = repos.BaseUrl });
        using var client = factory.CreateAuthorizedClient();

        var run = await RunToTerminalAsync(client, "acme", "badcfg");

        Assert.Equal(("Failed", "InvalidRepoConfig"), (run.State, run.FailureReason));
        var comment = Assert.Single(factory.GitHub!.CommentsOn("acme", "badcfg", run.Number));
        Assert.Contains("InvalidRepoConfig", comment.Body, StringComparison.Ordinal);
        Assert.Contains("codenetix.yml", comment.Body, StringComparison.Ordinal);
        Assert.Contains(RunShortId.Of(run.RunId), comment.Body, StringComparison.Ordinal);

        var (kind, status, _, _, _) = await ReadNotificationAsync(run.RunId);
        Assert.Equal(("RunFailed", "Posted"), (kind, status));
    }

    // --- 3. Cierre en Failed(NotAutomatable): nunca duplica con RunFailed -------------------

    [Fact]
    public async Task Failed_NotAutomatable_posts_only_NotAutomatable_and_never_RunFailed()
    {
        await using var factory = CreateFactory();
        factory.ChatClient.Respond(FakeChatClient.NotAutomatableAnswer("missing_information", "Faltan pasos de reproducción."));
        using var client = factory.CreateAuthorizedClient();

        var run = await RunToTerminalAsync(client);

        Assert.Equal(("Failed", "NotAutomatable"), (run.State, run.FailureReason));
        var comment = Assert.Single(factory.GitHub!.CommentsOn("o", "r", run.Number));
        Assert.Equal(1, factory.GitHub.CommentPosts);
        Assert.EndsWith(IssueNotifier.Marker(run.RunId, NotificationKind.NotAutomatable), comment.Body, StringComparison.Ordinal);

        var commentedEvents = Events(run).Where(e => e.GetProperty("type").GetString() == "IssueCommented").ToList();
        var commented = Assert.Single(commentedEvents);
        Assert.Equal("NotAutomatable", commented.GetProperty("kind").GetString());

        Assert.Equal(1L, await ScalarAsync("SELECT COUNT(*) FROM gh_notifications;"));
        Assert.Equal(0L, await ScalarAsync("SELECT COUNT(*) FROM gh_notifications WHERE kind = 'RunFailed';"));
    }

    // --- 4. Cierre en Cancelled con token ya cancelado (CTS independiente de 15 s) ----------

    [Fact]
    public async Task Cancelled_run_posts_RunCancelled_even_when_run_token_is_already_cancelled()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateAuthorizedClient();
        var (run, orchestrator) = await CreateCancelledRunAsync(factory, "github:o/r#777");

        using var alreadyCancelled = new CancellationTokenSource();
        alreadyCancelled.Cancel();

        await orchestrator.NotifyTerminalAsync(run, cancellationToken: alreadyCancelled.Token);

        var comment = Assert.Single(factory.GitHub!.CommentsOn("o", "r", 777));
        Assert.Equal(1, factory.GitHub.CommentPosts);
        Assert.Contains(TerminalNotificationComment.RunCancelledTitle, comment.Body, StringComparison.Ordinal);
        Assert.Contains(run.ShortId, comment.Body, StringComparison.Ordinal);
        Assert.EndsWith(IssueNotifier.Marker(run.Id, NotificationKind.RunCancelled), comment.Body, StringComparison.Ordinal);

        var finalRun = await GetRunAsync(client, run.Id);
        Assert.Equal("Cancelled", finalRun.GetProperty("state").GetString());
        var commented = Assert.Single(
            finalRun.GetProperty("events").EnumerateArray(),
            e => e.GetProperty("type").GetString() == "IssueCommented");
        Assert.Equal("RunCancelled", commented.GetProperty("kind").GetString());
        Assert.Equal(comment.HtmlUrl, commented.GetProperty("commentUrl").GetString());

        var (kind, status, _, _, error) = await ReadNotificationAsync(run.Id);
        Assert.Equal(("RunCancelled", "Posted"), (kind, status));
        Assert.Null(error);
    }

    // --- 5. Idempotencia por (runId, kind) --------------------------------------------------

    [Theory]
    [InlineData(NotificationKind.PullRequestCreated)]
    [InlineData(NotificationKind.RunFailed)]
    [InlineData(NotificationKind.RunCancelled)]
    public async Task NotifyAsync_is_idempotent_for_all_terminal_notification_kinds(NotificationKind kind)
    {
        await using var factory = CreateFactory();
        using var _ = factory.CreateClient();
        var notifier = factory.Services.GetRequiredService<IIssueNotifier>();
        var runId = Guid.CreateVersion7();

        var first = await notifier.NotifyAsync(runId, "o", "r", 88, kind, "Aviso terminal", Ct);
        var second = await notifier.NotifyAsync(runId, "o", "r", 88, kind, "Aviso terminal duplicado", Ct);

        Assert.Equal(NotificationStatus.Posted, first.Status);
        Assert.Equal(first, second);
        Assert.Equal(1, factory.GitHub!.CommentPosts);
        Assert.Single(factory.GitHub.CommentsOn("o", "r", 88));
        Assert.Equal(1L, await ScalarAsync("SELECT COUNT(*) FROM gh_notifications WHERE status = 'Posted';"));
    }

    // --- 6. Origen no GitHub ----------------------------------------------------------------

    [Fact]
    public async Task Non_GitHub_object_key_skips_notification_and_emits_no_notification_events()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateAuthorizedClient();
        var (run, orchestrator) = await CreateFailedRunAsync(factory, "sentry:acme/123", FailureReason.StageError, "error externo");

        await orchestrator.NotifyTerminalAsync(run, cancellationToken: Ct);

        Assert.Equal(0, factory.GitHub!.CommentPosts);
        Assert.Equal(0L, await ScalarAsync("SELECT COUNT(*) FROM gh_notifications;"));

        var finalRun = await GetRunAsync(client, run.Id);
        Assert.Equal("Failed", finalRun.GetProperty("state").GetString());
        Assert.DoesNotContain(
            finalRun.GetProperty("events").EnumerateArray(),
            e => e.GetProperty("type").GetString() is "IssueCommented" or "NotificationFailed");
    }

    // --- 7. Error de GitHub al comentar (500 / 403) -----------------------------------------

    [Theory]
    [InlineData(StatusCodes.Status500InternalServerError, "GitHub no responde (HTTP 500)")]
    [InlineData(StatusCodes.Status403Forbidden, "el token de GitHub no es válido o no tiene permisos (HTTP 403)")]
    public async Task GitHub_comment_error_on_PullRequestCreated_records_NotificationFailed_and_keeps_state(int status, string expectedError)
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateAuthorizedClient();
        factory.GitHub!.CommentPostStatus = status;

        var run = await RunToTerminalAsync(client);

        Assert.Equal("PullRequestCreated", run.State);
        Assert.Null(run.FailureReason);
        Assert.Equal(1, factory.GitHub.CommentPosts);

        var failed = Assert.Single(Events(run), e => e.GetProperty("type").GetString() == "NotificationFailed");
        Assert.Equal("PullRequestCreated", failed.GetProperty("kind").GetString());
        Assert.Equal(expectedError, failed.GetProperty("error").GetString());
        Assert.DoesNotContain(Events(run), e => e.GetProperty("type").GetString() == "IssueCommented");

        var (kind, notifStatus, _, _, error) = await ReadNotificationAsync(run.RunId);
        Assert.Equal(("PullRequestCreated", "Failed"), (kind, notifStatus));
        Assert.Equal(expectedError, error);

        // El token nunca aparece en run_events ni en gh_notifications.
        Assert.DoesNotContain(GitHubToken, (string)(await ScalarAsync("SELECT group_concat(payload_json, '') FROM run_events;"))!, StringComparison.Ordinal);
        Assert.DoesNotContain(GitHubToken, (string)(await ScalarAsync("SELECT group_concat(coalesce(error, '') || coalesce(comment_url, ''), '') FROM gh_notifications;"))!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GitHub_comment_error_on_Cancelled_records_NotificationFailed_and_keeps_Cancelled_state()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateAuthorizedClient();
        factory.GitHub!.CommentPostStatus = StatusCodes.Status500InternalServerError;
        var (run, orchestrator) = await CreateCancelledRunAsync(factory, "github:o/r#778");

        await orchestrator.NotifyTerminalAsync(run, cancellationToken: Ct);

        var finalRun = await GetRunAsync(client, run.Id);
        Assert.Equal("Cancelled", finalRun.GetProperty("state").GetString());
        var failed = Assert.Single(
            finalRun.GetProperty("events").EnumerateArray(),
            e => e.GetProperty("type").GetString() == "NotificationFailed");
        Assert.Equal("RunCancelled", failed.GetProperty("kind").GetString());
        Assert.Equal("GitHub no responde (HTTP 500)", failed.GetProperty("error").GetString());

        var (kind, notifStatus, _, _, error) = await ReadNotificationAsync(run.Id);
        Assert.Equal(("RunCancelled", "Failed"), (kind, notifStatus));
        Assert.Equal("GitHub no responde (HTTP 500)", error);
    }

    // --- Pruebas unitarias de TerminalNotificationComment y SanitizeAllowingPullRequestLinks ---

    [Fact]
    public async Task Stub_notifier_throwing_OperationCanceledException_or_secret_exception_emits_redacted_NotificationFailed_without_throwing()
    {
        await using var baseFactory = CreateFactory();
        var stubNotifier = new ThrowingIssueNotifier();
        await using var factory = baseFactory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddSingleton<IIssueNotifier>(stubNotifier)));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", CodeNetixFactory.TestAccessToken);

        // 1) OperationCanceledException → NotificationFailed con mensaje de timeout, sin lanzar, estado intacto.
        var (cancelledRun, orchestrator) = await CreateCancelledRunAsync(factory, "github:o/r#790");
        stubNotifier.NextException = new OperationCanceledException("simulación de timeout");

        await orchestrator.NotifyTerminalAsync(cancelledRun, cancellationToken: Ct);

        var runAfterTimeout = await GetRunAsync(client, cancelledRun.Id);
        Assert.Equal("Cancelled", runAfterTimeout.GetProperty("state").GetString());
        var timeoutEvent = Assert.Single(
            runAfterTimeout.GetProperty("events").EnumerateArray(),
            e => e.GetProperty("type").GetString() == "NotificationFailed");
        Assert.Equal("RunCancelled", timeoutEvent.GetProperty("kind").GetString());
        Assert.Contains("tiempo agotado", timeoutEvent.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);

        // 2) Excepción inesperada con secreto configurado → NotificationFailed redactado, sin lanzar, estado intacto.
        var (failedRun, _) = await CreateFailedRunAsync(factory, "github:o/r#791", FailureReason.StageError, "fallo previo");
        stubNotifier.NextException = new InvalidOperationException($"fallo inesperado con {GitHubToken} y {AnthropicKey}");

        await orchestrator.NotifyTerminalAsync(failedRun, cancellationToken: Ct);

        var runAfterError = await GetRunAsync(client, failedRun.Id);
        Assert.Equal("Failed", runAfterError.GetProperty("state").GetString());
        var errorEvent = Assert.Single(
            runAfterError.GetProperty("events").EnumerateArray(),
            e => e.GetProperty("type").GetString() == "NotificationFailed");
        Assert.Equal("RunFailed", errorEvent.GetProperty("kind").GetString());
        var recordedError = errorEvent.GetProperty("error").GetString()!;
        Assert.DoesNotContain(GitHubToken, recordedError, StringComparison.Ordinal);
        Assert.DoesNotContain(AnthropicKey, recordedError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NotifyTerminalAsync_throws_when_PullRequestCreated_has_null_pullRequest()
    {
        await using var factory = CreateFactory();
        using var _ = factory.CreateClient();
        var contexts = factory.Services.GetRequiredService<IDbContextFactory<PipelineDbContext>>();
        var log = factory.Services.GetRequiredService<RunEventLog>();
        var orchestrator = (RunOrchestrator)factory.Services.GetRequiredService<IRunOrchestrator>();

        await using var context = await contexts.CreateDbContextAsync(Ct);
        var (run, created, initial) = PipelineRun.Create(Guid.CreateVersion7(), "github:o/r#792", DateTimeOffset.UtcNow);
        context.Runs.Add(run);
        await log.AddAsync(context, run.Id, created, Ct);
        await log.AddAsync(context, run.Id, initial, Ct);
        await log.AddTransitionAsync(context, run, run.StartAnalysis(), Ct);
        await log.AddTransitionAsync(context, run, run.StartCoding(), Ct);
        await log.AddTransitionAsync(context, run, run.StartReview(), Ct);
        await log.AddTransitionAsync(context, run, run.StartDeciding(), Ct);
        await log.AddTransitionAsync(context, run, run.CompletePullRequest(), Ct);
        await context.SaveChangesAsync(Ct);

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            orchestrator.NotifyTerminalAsync(run, pullRequest: null, policy: null, cancellationToken: Ct));
    }

    [Fact]
    public void SanitizeAllowingPullRequestLinks_preserves_same_repo_pr_link_rejects_spoofed_hosts_and_truncates_cleanly()
    {
        var input = "PR abierto: [#12](https://github.com/acme/shop/pull/12) — ignora "
            + "[#12](https://evil.example/acme/shop/pull/12), "
            + "[#12](https://github.com@evil.example/acme/shop/pull/12), "
            + "[#12](http://github.com/acme/shop/pull/12), "
            + "[mal](https://evil.example/x), <script>x</script>, @admin y [#99](https://github.com/other/repo/pull/99)";

        var clean = IssueCommentSanitizer.SanitizeAllowingPullRequestLinks(input, 1000, "acme", "shop");

        Assert.Contains("[#12](https://github.com/acme/shop/pull/12)", clean, StringComparison.Ordinal);
        Assert.DoesNotContain("https://evil.example", clean, StringComparison.Ordinal);
        Assert.DoesNotContain("github.com@evil.example", clean, StringComparison.Ordinal);
        Assert.DoesNotContain("http://github.com", clean, StringComparison.Ordinal);
        Assert.DoesNotContain("https://github.com/other/repo/pull/99", clean, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", clean, StringComparison.Ordinal);
        Assert.DoesNotContain("@admin", clean, StringComparison.Ordinal);
        Assert.Contains("@\u200Badmin", clean, StringComparison.Ordinal);

        // Truncamiento cerca de maxLength: nunca filtra un token placeholder a medias.
        var nearLimit = new string('a', 40) + " [#12](https://github.com/acme/shop/pull/12) " + new string('b', 40);
        var truncated = IssueCommentSanitizer.SanitizeAllowingPullRequestLinks(nearLimit, 60, "acme", "shop");
        Assert.True(truncated.Length <= 60);
        Assert.DoesNotContain("CODENETIXPRLINK", truncated, StringComparison.Ordinal);
    }

    [Fact]
    public void TerminalNotificationComment_lists_remaining_protected_paths_count_when_over_ten()
    {
        var paths = Enumerable.Range(1, 13).Select(i => $"src/Auth/File{i}.cs").ToArray();
        var pr = new PullRequestInfo(15, "https://github.com/acme/shop/pull/15", "codenetix/run", "abc", "fix", DateTimeOffset.UtcNow);
        var policy = new PolicyResult(RiskClass.NeedsAttention, PolicyAction.CreatePullRequest, paths, ["toca rutas protegidas"]);
        var redactor = new FixedSecretRedactor("unused");

        var body = TerminalNotificationComment.BuildPullRequestCreated("abcd1234", pr, policy, redactor);

        Assert.Contains("`src/Auth/File1.cs`", body, StringComparison.Ordinal);
        Assert.Contains("`src/Auth/File10.cs`", body, StringComparison.Ordinal);
        Assert.DoesNotContain("`src/Auth/File11.cs`", body, StringComparison.Ordinal);
        Assert.Contains("(+3 más)", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TerminalNotificationComment_redacts_secrets_and_normalizes_newlines()
    {
        const string Secret = "sk-ant-supersecret0123456789abcdef";
        var redactor = new FixedSecretRedactor(Secret);

        var failed = TerminalNotificationComment.BuildRunFailed("abcd1234", FailureReason.TestsFailed, $"Línea 1\r\nFallo con {Secret}\rLínea 3", redactor);
        var cancelled = TerminalNotificationComment.BuildRunCancelled("abcd1234", redactor);

        Assert.Contains("TestsFailed", failed, StringComparison.Ordinal);
        Assert.Contains("`abcd1234`", failed, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, failed, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", failed, StringComparison.Ordinal);
        Assert.Contains("> Línea 1\n> Fallo con [REDACTED]\n> Línea 3", failed, StringComparison.Ordinal);

        Assert.Contains("`abcd1234`", cancelled, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", cancelled, StringComparison.Ordinal);
    }

    // --- Utilidades ------------------------------------------------------------------------

    private static async Task<(PipelineRun Run, RunOrchestrator Orchestrator)> CreateCancelledRunAsync(WebApplicationFactory<Program> factory, string objectKey)
    {
        var contexts = factory.Services.GetRequiredService<IDbContextFactory<PipelineDbContext>>();
        var log = factory.Services.GetRequiredService<RunEventLog>();
        var orchestrator = (RunOrchestrator)factory.Services.GetRequiredService<IRunOrchestrator>();

        await using var context = await contexts.CreateDbContextAsync(Ct);
        var (run, created, initial) = PipelineRun.Create(Guid.CreateVersion7(), objectKey, DateTimeOffset.UtcNow);
        context.Runs.Add(run);
        await log.AddAsync(context, run.Id, created, Ct);
        await log.AddAsync(context, run.Id, initial, Ct);
        await log.AddTransitionAsync(context, run, run.Cancel(), Ct);
        await context.SaveChangesAsync(Ct);
        return (run, orchestrator);
    }

    private static async Task<(PipelineRun Run, RunOrchestrator Orchestrator)> CreateFailedRunAsync(
        WebApplicationFactory<Program> factory,
        string objectKey,
        FailureReason reason,
        string detail)
    {
        var contexts = factory.Services.GetRequiredService<IDbContextFactory<PipelineDbContext>>();
        var log = factory.Services.GetRequiredService<RunEventLog>();
        var orchestrator = (RunOrchestrator)factory.Services.GetRequiredService<IRunOrchestrator>();

        await using var context = await contexts.CreateDbContextAsync(Ct);
        var (run, created, initial) = PipelineRun.Create(Guid.CreateVersion7(), objectKey, DateTimeOffset.UtcNow);
        context.Runs.Add(run);
        await log.AddAsync(context, run.Id, created, Ct);
        await log.AddAsync(context, run.Id, initial, Ct);
        await log.AddTransitionAsync(context, run, run.Fail(reason, detail), Ct);
        await context.SaveChangesAsync(Ct);
        return (run, orchestrator);
    }

    private async Task<RunResult> RunToTerminalAsync(HttpClient client, string owner = "o", string repo = "r")
    {
        var number = Interlocked.Increment(ref _issueNumber);
        using var submit = await client.PostAsJsonAsync(
            "/api/events/manual",
            new { url = $"https://github.com/{owner}/{repo}/issues/{number}", requestId = Guid.NewGuid().ToString() },
            Ct);
        Assert.Equal(HttpStatusCode.Created, submit.StatusCode);
        var eventId = (await submit.Content.ReadFromJsonAsync<EventBody>(Ct))!.EventId;

        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            using var eventResponse = await client.GetAsync($"/api/events/{eventId}", Ct);
            var info = (await eventResponse.Content.ReadFromJsonAsync<EventBody>(Ct))!;
            Assert.NotEqual("Ignored", info.Status);
            if (info.RunId is { } runId)
            {
                var json = await GetRunAsync(client, runId);
                var state = json.GetProperty("state").GetString();
                var events = json.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("type").GetString()).ToList();

                var notified = events.Contains("IssueCommented") || events.Contains("NotificationFailed");
                var sandboxDone = !events.Contains("SandboxCreated") || events.Contains("SandboxReleased");
                if (state is "Failed" or "Cancelled" or "PullRequestCreated" && notified && sandboxDone)
                {
                    return new RunResult(runId, number, state, json.GetProperty("failureReason").GetString(), json.GetProperty("failureDetail").GetString(), json);
                }
            }

            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30), $"La Ejecución del Evento {eventId} no ha terminado.");
            await Task.Delay(50, Ct);
        }
    }

    private static async Task<JsonElement> GetRunAsync(HttpClient client, Guid runId)
    {
        using var response = await client.GetAsync($"/api/runs/{runId}", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();
    }

    private static List<JsonElement> Events(RunResult run) => [.. run.Json.GetProperty("events").EnumerateArray()];

    private async Task<(string Kind, string Status, long? CommentId, string? CommentUrl, string? Error)> ReadNotificationAsync(Guid runId)
    {
        await using var connection = await OpenReadOnlyAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT kind, status, comment_id, comment_url, error FROM gh_notifications WHERE run_id = $id;";
        command.Parameters.AddWithValue("$id", runId.ToString().ToUpperInvariant());
        await using var reader = await command.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));
        var result = (
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? (long?)null : reader.GetInt64(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4));
        Assert.False(await reader.ReadAsync(Ct));
        return result;
    }

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var connection = await OpenReadOnlyAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(Ct);
        return value is DBNull ? null : value;
    }

    private async Task<SqliteConnection> OpenReadOnlyAsync()
    {
        var connection = new SqliteConnection($"Data Source={Path.Combine(_dataDirectory, PersistenceOptions.DatabaseFileName)};Mode=ReadOnly");
        await connection.OpenAsync(Ct);
        return connection;
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

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        LocalGitRepos.DeleteDirectory(_root);
    }

    private sealed class FixedSecretRedactor(string secret) : ISecretRedactor
    {
        public string Redact(string value) => value.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
    }

    private sealed class ThrowingIssueNotifier : IIssueNotifier
    {
        public Exception? NextException { get; set; }

        public Task<IssueNotification> NotifyAsync(
            Guid runId,
            string owner,
            string repo,
            long number,
            NotificationKind kind,
            string body,
            CancellationToken cancellationToken) =>
            Task.FromException<IssueNotification>(NextException ?? new InvalidOperationException("stub error"));
    }

    private sealed record EventBody(Guid EventId, string? Status, Guid? RunId);

    private sealed record RunResult(Guid RunId, long Number, string? State, string? FailureReason, string? FailureDetail, JsonElement Json);
}
