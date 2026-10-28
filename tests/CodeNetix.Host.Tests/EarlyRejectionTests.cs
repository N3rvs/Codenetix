using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CodeNetix.Agents.Features.AnalyzeIssue;
using CodeNetix.Contracts;
using CodeNetix.GitHub.Data;
using CodeNetix.GitHub.Features.NotifyIssue;
using CodeNetix.Persistence;
using CodeNetix.Pipeline.Features.RunPipeline;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CodeNetix.Host.Tests;

/// <summary>Matriz de la historia 1.9: rechazo temprano con un único comentario en la issue.</summary>
public sealed class EarlyRejectionTests : IDisposable
{
    private const string GitHubToken = "ghp_testtoken0123456789abcdefghijklmn"; // el de CompleteSettings
    private const string Explanation = "El cambio afecta a toda la capa de datos y necesita decisiones de arquitectura.";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "codenetix-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _dataDirectory;
    private readonly string _webRoot;
    private int _issueNumber = 500;

    public EarlyRejectionTests()
    {
        _dataDirectory = Path.Combine(_root, "data");
        _webRoot = Path.Combine(_root, "wwwroot");
        Directory.CreateDirectory(_webRoot);
        File.WriteAllText(Path.Combine(_webRoot, "index.html"), "<!doctype html><html><body>Panel</body></html>");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private CodeNetixFactory CreateFactory() => new(_dataDirectory, _webRoot, CodeNetixFactory.CompleteSettings);

    // --- Matriz: No automatizable ----------------------------------------------------------

    [Fact]
    public async Task Not_automatable_fails_without_spec_and_posts_exactly_one_comment()
    {
        await using var factory = CreateFactory();
        factory.ChatClient.Respond(FakeChatClient.NotAutomatableAnswer("too_large", Explanation));
        using var client = factory.CreateAuthorizedClient();

        var run = await RunToTerminalAsync(client);

        Assert.Equal(("Failed", "NotAutomatable"), (run.State, run.FailureReason));
        Assert.Equal("too_large: " + Explanation, run.FailureDetail);
        Assert.Equal(JsonValueKind.Null, run.Json.GetProperty("spec").ValueKind);
        Assert.Equal(0L, await ScalarAsync("SELECT COUNT(*) FROM run_specs;"));

        var events = Events(run);
        Assert.Equal(
            ["RunCreated", "StateChanged", "RepositoryPrepared", "RepoConfigResolved", "StateChanged", "SandboxCreated", "AnalysisRejected", "StateChanged", "IssueCommented", "SandboxReleased"],
            events.Select(e => e.GetProperty("type").GetString()));
        var rejected = events[6];
        Assert.Equal("too_large", rejected.GetProperty("category").GetString());
        Assert.Equal(1, rejected.GetProperty("attempts").GetInt32());
        Assert.False(string.IsNullOrEmpty(rejected.GetProperty("model").GetString()));
        Assert.Equal(("Analyzing", "Failed", "NotAutomatable"), (events[7].GetProperty("from").GetString(), events[7].GetProperty("to").GetString(), events[7].GetProperty("failureReason").GetString()));
        Assert.DoesNotContain(events, e => e.GetProperty("type").GetString() is "SpecGenerated" or "NotificationFailed");

        // Un único comentario, en español, con el shortId y el marcador; sin el texto de la issue.
        var comment = Assert.Single(factory.GitHub!.CommentsOn("o", "r", run.Number));
        Assert.Equal(1, factory.GitHub.CommentPosts);
        Assert.Equal(comment.HtmlUrl, events[8].GetProperty("commentUrl").GetString());
        Assert.Equal("NotAutomatable", events[8].GetProperty("kind").GetString());
        Assert.Contains(NotAutomatableComment.Title, comment.Body, StringComparison.Ordinal);
        Assert.Contains("cambio demasiado grande", comment.Body, StringComparison.Ordinal);
        Assert.Contains(Explanation, comment.Body, StringComparison.Ordinal);
        Assert.Contains(RunShortId.Of(run.RunId), comment.Body, StringComparison.Ordinal);
        Assert.EndsWith(IssueNotifier.Marker(run.RunId, NotificationKind.NotAutomatable), comment.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeGitHub.DefaultIssueBody, comment.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeGitHub.DefaultIssueTitle, comment.Body, StringComparison.Ordinal);

        // gh_notifications: una fila Posted con el comentario.
        await using var connection = await OpenReadOnlyAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT kind, status, comment_id, comment_url, error, created_at, updated_at FROM gh_notifications WHERE run_id = $id;";
        command.Parameters.AddWithValue("$id", run.RunId.ToString().ToUpperInvariant());
        await using var reader = await command.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));
        Assert.Equal(("NotAutomatable", "Posted", comment.Id, comment.HtmlUrl), (reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetString(3)));
        Assert.True(reader.IsDBNull(4));
        Assert.True(reader.GetInt64(6) >= reader.GetInt64(5));
        Assert.False(await reader.ReadAsync(Ct));
    }

    // --- Matriz: Automatizable -------------------------------------------------------------

    [Fact]
    public async Task Automatable_issue_behaves_as_in_1_8_without_comment()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateAuthorizedClient();

        var run = await RunToTerminalAsync(client);

        Assert.Equal(("StageError", RunOrchestrator.Detail), (run.FailureReason, run.FailureDetail));
        Assert.Equal(JsonValueKind.Object, run.Json.GetProperty("spec").ValueKind);
        Assert.Equal(0, factory.GitHub!.CommentPosts);
        Assert.Empty(factory.GitHub.CommentsOn("o", "r", run.Number));
        Assert.Equal(0L, await ScalarAsync("SELECT COUNT(*) FROM gh_notifications;"));
        Assert.DoesNotContain(Events(run), e => e.GetProperty("type").GetString() is "AnalysisRejected" or "IssueCommented");
    }

    // --- Matriz: GitHub falla --------------------------------------------------------------

    [Theory]
    [InlineData(StatusCodes.Status403Forbidden, "el token de GitHub no es válido o no tiene permisos (HTTP 403)")]
    [InlineData(StatusCodes.Status502BadGateway, "GitHub no responde (HTTP 502)")]
    public async Task GitHub_failure_records_NotificationFailed_and_keeps_the_terminal_state(int status, string error)
    {
        await using var factory = CreateFactory();
        factory.ChatClient.Respond(FakeChatClient.NotAutomatableAnswer("missing_information", "Falta saber qué pantalla falla."));
        using var client = factory.CreateAuthorizedClient();
        factory.GitHub!.CommentPostStatus = status;

        var run = await RunToTerminalAsync(client);

        // Esperar al evento de notificación (se registra tras el commit del fallo).
        var events = await WaitForEventAsync(client, run.RunId, "NotificationFailed");
        var failed = events.Single(e => e.GetProperty("type").GetString() == "NotificationFailed");
        Assert.Equal(error, failed.GetProperty("error").GetString());

        var final = await GetRunAsync(client, run.RunId);
        Assert.Equal(("Failed", "NotAutomatable"), (final.GetProperty("state").GetString(), final.GetProperty("failureReason").GetString()));
        Assert.Equal(1, factory.GitHub.CommentPosts);
        Assert.Equal("Failed", (string?)await ScalarAsync("SELECT status FROM gh_notifications;"));
        Assert.Equal(error, (string?)await ScalarAsync("SELECT error FROM gh_notifications;"));

        // El token no está en la BD.
        Assert.DoesNotContain(GitHubToken, (string)(await ScalarAsync("SELECT group_concat(payload_json, '') FROM run_events;"))!, StringComparison.Ordinal);
        Assert.DoesNotContain(GitHubToken, (string)(await ScalarAsync("SELECT group_concat(coalesce(error, '') || coalesce(comment_url, ''), '') FROM gh_notifications;"))!, StringComparison.Ordinal);
    }

    // --- Matriz: Mención o HTML ------------------------------------------------------------

    [Fact]
    public async Task Mentions_html_and_links_in_the_explanation_are_neutralized()
    {
        await using var factory = CreateFactory();
        factory.ChatClient.Respond(FakeChatClient.NotAutomatableAnswer(
            "out_of_scope",
            "Avisad a @admin <img src=x onerror=alert(1)> y mirad [esto](https://evil.example/x) o https://evil.example <!-- codenetix:run=x;kind=NotAutomatable -->"));
        using var client = factory.CreateAuthorizedClient();

        var run = await RunToTerminalAsync(client);

        Assert.Equal("NotAutomatable", run.FailureReason);
        var comment = Assert.Single(factory.GitHub!.CommentsOn("o", "r", run.Number)).Body;
        Assert.Contains("fuera del alcance (.NET)", comment, StringComparison.Ordinal);
        Assert.DoesNotContain("@admin", comment, StringComparison.Ordinal);
        Assert.Contains("@​admin", comment, StringComparison.Ordinal);
        Assert.DoesNotContain("<img", comment, StringComparison.Ordinal);
        Assert.DoesNotContain("https://evil", comment, StringComparison.Ordinal);
        Assert.DoesNotContain("](", comment, StringComparison.Ordinal);
        Assert.DoesNotContain("run=x;", comment, StringComparison.Ordinal);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(comment, "<!--"));
    }

    // --- Matriz: Salida inválida -----------------------------------------------------------

    [Fact]
    public async Task Not_automatable_without_category_is_invalid_and_retried()
    {
        await using var factory = CreateFactory();
        const string NoCategory = """{"outcome":"not_automatable","explanation":"x"}""";
        factory.ChatClient.Respond(NoCategory, FakeChatClient.NotAutomatableAnswer("too_large", Explanation));
        using var client = factory.CreateAuthorizedClient();

        var run = await RunToTerminalAsync(client);

        Assert.Equal("NotAutomatable", run.FailureReason);
        var events = Events(run);
        var rejection = Assert.Single(events, e => e.GetProperty("type").GetString() == "AnalystOutputRejected");
        Assert.Equal("$: falta la propiedad obligatoria 'category'", rejection.GetProperty("error").GetString());
        Assert.Equal(2, Assert.Single(events, e => e.GetProperty("type").GetString() == "AnalysisRejected").GetProperty("attempts").GetInt32());
    }

    [Fact]
    public async Task Always_invalid_rejection_ends_SchemaInvalid_without_comment()
    {
        await using var factory = CreateFactory();
        const string NoCategory = """{"outcome":"not_automatable","explanation":"x"}""";
        factory.ChatClient.Respond(NoCategory, NoCategory, NoCategory);
        using var client = factory.CreateAuthorizedClient();

        var run = await RunToTerminalAsync(client);

        Assert.Equal("SchemaInvalid", run.FailureReason);
        Assert.Contains("'category'", run.FailureDetail, StringComparison.Ordinal);
        Assert.Equal(0, factory.GitHub!.CommentPosts);
    }

    // --- Matriz: Doble publicación y caída tras publicar ----------------------------------

    [Fact]
    public async Task Notifying_twice_in_series_posts_once()
    {
        await using var factory = CreateFactory();
        var (notifier, gitHub) = Start(factory);
        var runId = Guid.CreateVersion7();

        var first = await notifier.NotifyAsync(runId, "o", "r", 7, NotificationKind.NotAutomatable, "cuerpo", Ct);
        var second = await notifier.NotifyAsync(runId, "o", "r", 7, NotificationKind.NotAutomatable, "cuerpo", Ct);

        Assert.Equal(NotificationStatus.Posted, first.Status);
        Assert.Equal(first, second);
        Assert.Equal(1, gitHub.CommentPosts);
        Assert.Single(gitHub.CommentsOn("o", "r", 7));
        Assert.Equal(1L, await ScalarAsync("SELECT COUNT(*) FROM gh_notifications WHERE status = 'Posted';"));
    }

    [Fact]
    public async Task Notifying_in_parallel_posts_once()
    {
        await using var factory = CreateFactory();
        var (notifier, gitHub) = Start(factory);
        gitHub.CommentPostDelay = TimeSpan.FromMilliseconds(300);
        var runId = Guid.CreateVersion7();

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ =>
            Task.Run(() => notifier.NotifyAsync(runId, "o", "r", 8, NotificationKind.NotAutomatable, "cuerpo", Ct), Ct)));

        Assert.All(results, r => Assert.Equal(NotificationStatus.Posted, r.Status));
        Assert.Single(results.Select(r => r.CommentUrl).Distinct());
        Assert.Equal(1, gitHub.CommentPosts);
        Assert.Single(gitHub.CommentsOn("o", "r", 8));
        Assert.Equal(1L, await ScalarAsync("SELECT COUNT(*) FROM gh_notifications;"));
    }

    [Fact]
    public async Task Crash_after_posting_adopts_the_marked_comment_without_posting_again()
    {
        await using var factory = CreateFactory();
        var (notifier, gitHub) = Start(factory);
        var runId = Guid.CreateVersion7();

        // Estado tras la caída: fila Pending y el comentario ya publicado, tras 150 comentarios de otros.
        await InsertPendingAsync(factory, runId);
        for (var i = 0; i < 150; i++)
        {
            gitHub.AddComment("o", "r", "9", $"comentario {i}", "someone");
        }

        var existing = gitHub.AddComment("o", "r", "9", "texto\n\n" + IssueNotifier.Marker(runId, NotificationKind.NotAutomatable), FakeGitHub.DefaultLogin);
        gitHub.AddComment("o", "r", "9", "otro posterior", "someone");

        var result = await notifier.NotifyAsync(runId, "o", "r", 9, NotificationKind.NotAutomatable, "cuerpo", Ct);

        Assert.Equal(IssueNotification.Posted(existing.HtmlUrl), result);
        Assert.Equal(0, gitHub.CommentPosts);
        Assert.Equal("Posted", (string?)await ScalarAsync("SELECT status FROM gh_notifications;"));
        Assert.Equal(existing.Id, (long)(await ScalarAsync("SELECT comment_id FROM gh_notifications;"))!);
    }

    [Fact]
    public async Task Pending_row_without_marker_posts_once()
    {
        await using var factory = CreateFactory();
        var (notifier, gitHub) = Start(factory);
        var runId = Guid.CreateVersion7();
        await InsertPendingAsync(factory, runId);

        var result = await notifier.NotifyAsync(runId, "o", "r", 10, NotificationKind.NotAutomatable, "cuerpo", Ct);

        Assert.Equal(NotificationStatus.Posted, result.Status);
        Assert.Equal(1, gitHub.CommentPosts);
    }

    [Fact]
    public async Task Explicit_call_after_a_failure_tries_again()
    {
        await using var factory = CreateFactory();
        var (notifier, gitHub) = Start(factory);
        var runId = Guid.CreateVersion7();
        gitHub.CommentPostStatus = StatusCodes.Status500InternalServerError;

        var failed = await notifier.NotifyAsync(runId, "o", "r", 11, NotificationKind.NotAutomatable, "cuerpo", Ct);
        gitHub.CommentPostStatus = StatusCodes.Status201Created;
        var posted = await notifier.NotifyAsync(runId, "o", "r", 11, NotificationKind.NotAutomatable, "cuerpo", Ct);

        Assert.Equal(IssueNotification.Failed("GitHub no responde (HTTP 500)"), failed);
        Assert.Equal(NotificationStatus.Posted, posted.Status);
        Assert.Equal(2, gitHub.CommentPosts);
        Assert.Single(gitHub.CommentsOn("o", "r", 11));
    }

    [Fact]
    public async Task Comment_listing_failure_is_Failed_without_posting()
    {
        await using var factory = CreateFactory();
        var (notifier, gitHub) = Start(factory);
        gitHub.AddComment("o", "r", "12", "previo", "someone");
        gitHub.CommentListStatus = StatusCodes.Status503ServiceUnavailable;

        var result = await notifier.NotifyAsync(Guid.CreateVersion7(), "o", "r", 12, NotificationKind.NotAutomatable, "cuerpo", Ct);

        Assert.Equal(IssueNotification.Failed("GitHub no responde (HTTP 503)"), result);
        Assert.Equal(0, gitHub.CommentPosts);
    }

    [Fact]
    public async Task Marker_near_the_end_of_450_comments_is_found_via_the_Link_header_without_posting()
    {
        await using var factory = CreateFactory();
        var (notifier, gitHub) = Start(factory);
        var runId = Guid.CreateVersion7();
        await InsertPendingAsync(factory, runId);
        var marked = SeedComments(gitHub, 13, total: 450, markerAt: 430, runId, FakeGitHub.DefaultLogin);

        var result = await notifier.NotifyAsync(runId, "o", "r", 13, NotificationKind.NotAutomatable, "cuerpo", Ct);

        Assert.Equal(IssueNotification.Posted(marked.HtmlUrl), result);
        Assert.Equal(0, gitHub.CommentPosts);
        Assert.Equal(["1", "5"], gitHub.CommentPagesListed("o", "r", 13));
    }

    [Fact]
    public async Task Marker_older_than_the_last_three_pages_is_not_searched()
    {
        await using var factory = CreateFactory();
        var (notifier, gitHub) = Start(factory);
        var runId = Guid.CreateVersion7();
        SeedComments(gitHub, 14, total: 450, markerAt: 50, runId, FakeGitHub.DefaultLogin);

        var result = await notifier.NotifyAsync(runId, "o", "r", 14, NotificationKind.NotAutomatable, "cuerpo", Ct);

        Assert.Equal(NotificationStatus.Posted, result.Status);
        Assert.Equal(1, gitHub.CommentPosts);
        Assert.Equal(["1", "5", "4", "3"], gitHub.CommentPagesListed("o", "r", 14));
    }

    [Fact]
    public async Task Marker_spoofed_by_another_user_is_ignored_and_the_real_comment_is_posted()
    {
        await using var factory = CreateFactory();
        var (notifier, gitHub) = Start(factory);
        var runId = Guid.CreateVersion7();
        await InsertPendingAsync(factory, runId);
        var spoofed = SeedComments(gitHub, 15, total: 3, markerAt: 2, runId, "Mallory");

        var result = await notifier.NotifyAsync(runId, "o", "r", 15, NotificationKind.NotAutomatable, "cuerpo", Ct);

        Assert.Equal(NotificationStatus.Posted, result.Status);
        Assert.NotEqual(spoofed.HtmlUrl, result.CommentUrl);
        Assert.Equal(1, gitHub.CommentPosts);
        Assert.Equal(FakeGitHub.DefaultLogin, gitHub.CommentsOn("o", "r", 15)[^1].Login);
    }

    [Fact]
    public async Task Marker_from_the_bot_matches_its_login_case_insensitively()
    {
        await using var factory = CreateFactory();
        var (notifier, gitHub) = Start(factory);
        var runId = Guid.CreateVersion7();
        await InsertPendingAsync(factory, runId);
        var marked = SeedComments(gitHub, 16, total: 1, markerAt: 1, runId, FakeGitHub.DefaultLogin.ToUpperInvariant());

        var result = await notifier.NotifyAsync(runId, "o", "r", 16, NotificationKind.NotAutomatable, "cuerpo", Ct);

        Assert.Equal(IssueNotification.Posted(marked.HtmlUrl), result);
        Assert.Equal(0, gitHub.CommentPosts);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("<https://api.github.com/x/comments?per_page=100&page=2>; rel=\"next\", <https://api.github.com/x/comments?per_page=100&page=5>; rel=\"last\"", 5L)]
    [InlineData("<https://api.github.com/x/comments?page=7&per_page=100>; rel=\"last\"", 7L)]
    [InlineData("<https://api.github.com/x/comments?per_page=100&page=1>; rel=\"prev\"", null)]
    [InlineData("<https://api.github.com/x/comments?per_page=100>; rel=\"last\"", null)]
    public void Last_page_comes_from_the_Link_header(string? link, long? expected) =>
        Assert.Equal(expected, IssueNotifier.LastPage(link));

    // --- Cuerpo del comentario -------------------------------------------------------------

    [Fact]
    public void Comment_body_keeps_every_line_quoted_with_lone_carriage_returns()
    {
        var body = NotAutomatableComment.Build("abcd1234", NotAutomatableCategory.MissingInformation, "uno\rdos\r\ntres\ncuatro");

        var quoted = body.Split('\n').SkipWhile(l => !l.StartsWith("> ", StringComparison.Ordinal)).TakeWhile(l => l.Length > 0).ToList();
        Assert.Equal(["> uno", "> dos", "> tres", "> cuatro"], quoted);
        Assert.DoesNotContain("\r", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Truncation_never_splits_a_surrogate_pair()
    {
        var text = new string('a', 998) + "😀😀"; // el primer emoji ocupa las posiciones 998-999

        var cut = TextLimits.Truncate(text, 1000);

        Assert.True(cut.Length <= 1000);
        Assert.EndsWith("a…", cut, StringComparison.Ordinal);
        Assert.False(char.IsHighSurrogate(cut[^2]));
        Assert.Equal(new string('a', 998) + "…", cut);
    }

    [Theory]
    [InlineData(NotAutomatableCategory.MissingInformation, "falta información")]
    [InlineData(NotAutomatableCategory.TooLarge, "cambio demasiado grande")]
    [InlineData(NotAutomatableCategory.OutOfScope, "fuera del alcance (.NET)")]
    public void Comment_body_has_title_translated_category_explanation_and_short_id(NotAutomatableCategory category, string label)
    {
        var body = NotAutomatableComment.Build("abcd1234", category, "Línea uno.\nLínea dos.");

        Assert.StartsWith("### " + NotAutomatableComment.Title + "\n", body, StringComparison.Ordinal);
        Assert.Contains("**Motivo:** " + label + "\n", body, StringComparison.Ordinal);
        Assert.Contains("> Línea uno.\n> Línea dos.\n", body, StringComparison.Ordinal);
        Assert.Contains("`abcd1234`", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", body, StringComparison.Ordinal);

        // La plantilla sobrevive intacta al saneado del módulo GitHub.
        Assert.Equal(body, IssueCommentSanitizer.Sanitize(body, IssueNotifier.MaxBodyLength));
    }

    [Fact]
    public void Comment_body_caps_the_explanation_at_1000_characters()
    {
        var body = NotAutomatableComment.Build("abcd1234", NotAutomatableCategory.TooLarge, new string('x', 5000));

        var quoted = body.Split('\n').Single(l => l.StartsWith("> ", StringComparison.Ordinal))[2..];
        Assert.Equal(AnalysisSchema.MaxExplanationLength, quoted.Length);
        Assert.EndsWith("…", quoted, StringComparison.Ordinal);
    }

    public static TheoryData<string, string> SanitizerCases => new()
    {
        { "texto normal", "texto normal" },
        { "hola @admin y @org/team", "hola @\u200Badmin y @\u200Borg/team" },
        { "a <img src=x onerror=alert(1)> b", "a  b" },
        { "<script>alert(1)</script>fin", "alert(1)fin" },
        { "<details><summary>x</summary>y</details>", "xy" },
        { "a <!-- oculto --> b", "a  b" },
        { "a <!-- sin cerrar", "a" },
        { "2 < 3 y 5 > 4", "2 &lt; 3 y 5 > 4" },
        { "<img src=x", "&lt;img src=x" },
        { "mira [esto](https://evil.example/x)", "mira esto" },
        { "![logo](https://evil.example/a.png)", "logo" },
        { "ver [doc][1]\n[1]: https://evil.example", "ver doc" },
        { "visita https://evil.example ya", "visita https:/\u200B/evil.example ya" },
        { "o www.evil.example", "o www\u200B.evil.example" },
        { "<https://evil.example>", "https:/\u200B/evil.example" },
        { "ctl\u0000\u0007 ok\r\nsig", "ctl ok\nsig" },
        { "&#64;admin y &#x40;root", "&amp;#​64;admin y &amp;#x40;root" },
        { "&commat;admin", "&amp;commat;admin" },
        { "https&#58;//evil.example", "https&amp;#\u200B58;/\u200B/evil.example" },
        { "[[a]](//evil.example)", @"\[\[a\]\](/" + "\u200B" + "/evil.example)" },
        { "![[a]](//evil.example/p.png)", @"\!\[\[a\]\](/" + "\u200B" + "/evil.example/p.png)" },
        { "[haz\nclic](//evil.example)", "\\[haz\nclic\\](/\u200B/evil.example)" },
        { "ver #123", "ver #\u200B123" },
        { "org/repo#5", "org/repo#\u200B5" },
        { "GH-7 y gh-8", "GH-\u200B7 y gh-\u200B8" },
        { "owner/repo@abc123", "owner/repo@\u200Babc123" },
        { "### título y # nota", "### título y # nota" },
        { "¡Hola!", "¡Hola\\!" },
    };

    [Theory]
    [MemberData(nameof(SanitizerCases))]
    public void Sanitizer_removes_html_mentions_and_links(string input, string expected) =>
        Assert.Equal(expected, IssueCommentSanitizer.Sanitize(input, 1000));

    [Fact]
    public void Sanitizer_caps_the_length()
    {
        var clean = IssueCommentSanitizer.Sanitize(new string('a', 3000), 1000);

        Assert.Equal(1000, clean.Length);
        Assert.EndsWith("…", clean, StringComparison.Ordinal);
    }

    // --- Esquema analysis.v1.json ----------------------------------------------------------

    [Fact]
    public void The_public_analysis_schema_file_is_the_embedded_one_and_the_request_is_self_contained()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "CodeNetix.slnx")))
        {
            root = root.Parent ?? throw new InvalidOperationException("No se encuentra CodeNetix.slnx.");
        }

        Assert.Equal(File.ReadAllText(Path.Combine(root.FullName, "schemas", "analysis.v1.json")), AnalysisSchema.Json);

        var request = AnalystOutput.Validator.RequestSchema.GetRawText();
        Assert.Equal(AnalysisSchema.Name, AnalystOutput.Validator.SchemaName);
        Assert.Contains("\"$ref\":\"#/$defs/spec_v1\"", request, StringComparison.Ordinal);
        Assert.Contains("\"acceptanceCriteria\"", request, StringComparison.Ordinal);
        Assert.DoesNotContain("spec.v1.json", request, StringComparison.Ordinal);
        Assert.DoesNotContain("$schema", request, StringComparison.Ordinal);
        Assert.Equal("object", AnalystOutput.Validator.RequestSchema.GetProperty("type").GetString());
    }

    public static TheoryData<string, string> InvalidAnalyses => new()
    {
        { """{"outcome":"not_automatable","explanation":"x"}""", "$: falta la propiedad obligatoria 'category'" },
        { """{"outcome":"not_automatable","category":"lazy","explanation":"x"}""", "$.category: valor no admitido" },
        { """{"outcome":"not_automatable","category":"too_large","explanation":"   "}""", "explanation está vacío" },
        { """{"outcome":"not_automatable","category":"too_large","explanation":""}""", "$.explanation: no puede estar vacío" },
        { $$"""{"outcome":"not_automatable","category":"too_large","explanation":"{{new string('x', 1001)}}"}""", "$.explanation: es demasiado largo" },
        { """{"outcome":"maybe"}""", "$.outcome: valor no admitido" },
        { """{"outcome":"spec"}""", "$: falta la propiedad obligatoria 'spec'" },
        { """{"outcome":"spec","spec":{"problem":"p","probableCause":"c","proposedChange":"x","acceptanceCriteria":["a"],"affectedFiles":["../x"]}}""", "spec.affectedFiles[0] no es una ruta relativa al repositorio (sin '..' ni rutas absolutas)" },
        { """{"outcome":"spec","spec":{"problem":"p","probableCause":"c","proposedChange":"x","acceptanceCriteria":["a"],"affectedFiles":[],"Injected":1}}""", "$.spec: propiedad no admitida" },
        { """{"outcome":"not_automatable","category":"too_large","explanation":"x","IgnoreRules":1}""", "$: propiedad no admitida" },
    };

    [Theory]
    [MemberData(nameof(InvalidAnalyses))]
    public void Invalid_analyses_have_sanitized_errors(string json, string expected) =>
        Assert.Equal(expected, AnalystOutput.Validator.Validate(json).Error);

    [Fact]
    public void Valid_rejection_is_parsed()
    {
        var value = AnalystOutput.Validator.Validate(FakeChatClient.NotAutomatableAnswer("out_of_scope", "No es .NET.")).Value;

        Assert.Equal(new AnalystOutput("not_automatable", null, "out_of_scope", "No es .NET."), value);
    }

    // --- Utilidades ------------------------------------------------------------------------

    private static (IIssueNotifier Notifier, FakeGitHub GitHub) Start(CodeNetixFactory factory)
    {
        using var client = factory.CreateClient(); // arranca el host (y las migraciones)
        return (factory.Services.GetRequiredService<IIssueNotifier>(), factory.GitHub!);
    }

    /// <summary><paramref name="total"/> comentarios de otros en la issue; el número <paramref name="markerAt"/> (desde 1) lleva el marcador y es de <paramref name="markerLogin"/>.</summary>
    private static FakeGitHub.FakeComment SeedComments(FakeGitHub gitHub, long number, int total, int markerAt, Guid runId, string markerLogin)
    {
        FakeGitHub.FakeComment? marked = null;
        var issue = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        for (var i = 1; i <= total; i++)
        {
            if (i == markerAt)
            {
                marked = gitHub.AddComment("o", "r", issue, "texto\n\n" + IssueNotifier.Marker(runId, NotificationKind.NotAutomatable), markerLogin);
            }
            else
            {
                gitHub.AddComment("o", "r", issue, $"comentario {i}", "someone");
            }
        }

        return marked!;
    }

    private static async Task InsertPendingAsync(CodeNetixFactory factory, Guid runId)
    {
        var contexts = factory.Services.GetRequiredService<IDbContextFactory<GitHubDbContext>>();
        await using var context = await contexts.CreateDbContextAsync(Ct);
        context.Notifications.Add(new NotificationRecord
        {
            Id = Guid.CreateVersion7(),
            RunId = runId,
            Kind = NotificationKind.NotAutomatable,
            Status = NotificationStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await context.SaveChangesAsync(Ct);
    }

    private async Task<RunResult> RunToTerminalAsync(HttpClient client)
    {
        var number = Interlocked.Increment(ref _issueNumber);
        using var submit = await client.PostAsJsonAsync(
            "/api/events/manual",
            new { url = $"https://github.com/o/r/issues/{number}", requestId = Guid.NewGuid().ToString() },
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

                // Un rechazo termina cuando se ha registrado el resultado del aviso (después del commit del fallo).
                var settled = json.GetProperty("failureReason").GetString() != "NotAutomatable"
                    || events.Contains("IssueCommented") || events.Contains("NotificationFailed");

                // ...y, si se creó el Sandbox, cuando se ha registrado su liberación (el último paso del orquestador).
                settled &= !events.Contains("SandboxCreated") || events.Contains("SandboxReleased");
                if (state is "Failed" or "Cancelled" or "PullRequestCreated" && settled)
                {
                    return new RunResult(runId, number, state, json.GetProperty("failureReason").GetString(), json.GetProperty("failureDetail").GetString(), json);
                }
            }

            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30), $"La Ejecución del Evento {eventId} no ha terminado.");
            await Task.Delay(50, Ct);
        }
    }

    private static async Task<List<JsonElement>> WaitForEventAsync(HttpClient client, Guid runId, string type)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            var events = (await GetRunAsync(client, runId)).GetProperty("events").EnumerateArray().ToList();
            if (events.Any(e => e.GetProperty("type").GetString() == type))
            {
                return events;
            }

            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30), $"No llega {type}.");
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

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        LocalGitRepos.DeleteDirectory(_root);
    }

    private sealed record EventBody(Guid EventId, string? Status, Guid? RunId);

    private sealed record RunResult(Guid RunId, long Number, string? State, string? FailureReason, string? FailureDetail, JsonElement Json);
}
