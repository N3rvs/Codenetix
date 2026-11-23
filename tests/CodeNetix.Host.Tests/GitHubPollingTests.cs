using System.Collections.Concurrent;
using System.Net.Http.Json;
using CodeNetix.Contracts;
using CodeNetix.GitHub;
using CodeNetix.GitHub.Features.ListLabeledIssues;
using CodeNetix.Host.Configuration;
using CodeNetix.Host.Features.SubmitManualEvent;
using CodeNetix.Ingestion.Features.PollGitHub;
using CodeNetix.Ingestion.Features.SubmitManualEvent;
using CodeNetix.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeNetix.Host.Tests;

/// <summary>
/// Historia 7.1 (FR-6, FR-26, AD-4, AD-18): sondeo de issues etiquetadas en <c>serve</c> contra un GitHub falso. El primer
/// ciclo lo lanza el servicio al estar <c>Ready</c> (el intervalo por defecto, 5 min, no vence durante la prueba); los
/// siguientes se lanzan a mano con <see cref="GitHubPoller.RunCycleAsync"/>. Una prueba por fila de la matriz de la spec.
/// </summary>
public sealed class GitHubPollingTests : IAsyncLifetime
{
    private const string Alice = "alice";
    private static readonly string Token = CodeNetixFactory.CompleteSettings["CODENETIX:GitHub:Token"]!;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "codenetix-polling-" + Guid.NewGuid().ToString("N"));
    private readonly string _dataDirectory;
    private readonly string _webRoot;
    private readonly ConcurrentQueue<string> _logs = new();
    private FakeGitHub _gitHub = null!;

    public GitHubPollingTests()
    {
        _dataDirectory = Path.Combine(_root, "data");
        _webRoot = Path.Combine(_root, "wwwroot");
        Directory.CreateDirectory(_webRoot);
        File.WriteAllText(Path.Combine(_webRoot, "index.html"), "<!doctype html><html><body>Panel</body></html>");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string DatabasePath => Path.Combine(_dataDirectory, PersistenceOptions.DatabaseFileName);

    public async ValueTask InitializeAsync() => _gitHub = await FakeGitHub.StartAsync(Ct);

    // --- Fila 1: issue nueva etiquetada → Evento Accepted con la clave AD-4 y Ejecución del Dispatcher ---

    [Fact]
    public async Task New_labeled_issue_creates_an_Accepted_GitHub_event_and_the_Dispatcher_creates_its_run()
    {
        SeedLabeledIssue(12, labelEventId: 900, actor: Alice);
        await using var host = await StartAsync(Settings("O/R"));

        var row = Assert.Single(await ReadEventsAsync());
        Assert.Equal("GitHub", row.Source);
        Assert.Equal("gh:o/r#12:labeled:900", row.DedupKey);
        Assert.Equal("github:o/r#12", row.ObjectKey);
        Assert.Equal((Alice, "Accepted", (string?)null), (row.Actor, row.Status, row.IgnoredReason));
        Assert.Contains("\"labelEventId\":900", row.Payload, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, row.Payload, StringComparison.Ordinal);

        // FR-26 sobre quien puso la etiqueta, no sobre el token; con las credenciales de la instancia.
        Assert.Contains(_gitHub.Requests, r => r.Kind == $"permission:o/r:{Alice}");
        Assert.All(_gitHub.Requests.Where(r => r.Kind.StartsWith("issues-list:", StringComparison.Ordinal)), r => Assert.Equal("Bearer " + Token, r.Authorization));

        // Solo el Dispatcher crea la Ejecución.
        var dispatched = await WaitForAsync(async () => (await ReadEventsAsync()).Single().RunId is not null);
        Assert.True(dispatched, "El Dispatcher debe convertir el Evento en una Ejecución.");
    }

    [Fact]
    public async Task Interval_elapsing_runs_new_cycles_on_its_own()
    {
        SeedLabeledIssue(12, labelEventId: 900, actor: Alice);
        await using var host = await StartAsync(Settings("o/r"), interval: TimeSpan.FromMilliseconds(150));

        Assert.True(await WaitForAsync(() => Task.FromResult(host.Poller.CompletedCycles >= 3)), "El sondeo debe repetirse cada intervalo.");
        Assert.True(_gitHub.IssueListRequests("o", "r").Count >= 3);
        Assert.Single(await ReadEventsAsync());
    }

    [Fact]
    public async Task Default_configuration_maps_to_five_minutes_and_the_codenetix_label()
    {
        await using var host = await StartAsync(Settings("O/R, o/r ; acme/Web"));

        var options = host.Factory.Services.GetRequiredService<IOptions<GitHubPollingOptions>>().Value;
        Assert.Equal(TimeSpan.FromMinutes(5), options.Interval);
        Assert.Equal("codenetix", options.Label);
        Assert.Equal([new PolledRepository("o", "r"), new PolledRepository("acme", "web")], options.Repositories);
    }

    [Fact]
    public async Task Indexed_repositories_and_custom_label_and_interval_are_read()
    {
        var settings = Settings(null);
        settings["CODENETIX:Polling:Repositories:0"] = "o/r";
        settings["CODENETIX:Polling:Repositories:1"] = "o/s";
        settings["CODENETIX:Polling:Label"] = "auto-fix";
        settings["CODENETIX:Polling:IntervalSeconds"] = "60";
        await using var host = await StartAsync(settings);

        var options = host.Factory.Services.GetRequiredService<IOptions<GitHubPollingOptions>>().Value;
        Assert.Equal([new PolledRepository("o", "r"), new PolledRepository("o", "s")], options.Repositories);
        Assert.Equal("auto-fix", options.Label);
        Assert.Equal(TimeSpan.FromSeconds(60), options.Interval);
        Assert.EndsWith(":labels=auto-fix", _gitHub.IssueListRequests("o", "r")[0].Kind, StringComparison.Ordinal);
    }

    // --- Fila 2: ciclos repetidos → 304 con If-None-Match, sin eventos ni permiso ---

    [Fact]
    public async Task Repeated_cycles_send_the_ETag_get_304_and_create_nothing_else()
    {
        SeedLabeledIssue(12, labelEventId: 900, actor: Alice);
        await using var host = await StartAsync(Settings("o/r"));
        var permissionCalls = _gitHub.PermissionCalls;
        var eventCalls = _gitHub.IssueEventCalls("o", "r", 12);

        await host.Poller.RunCycleAsync(Ct);
        await host.Poller.RunCycleAsync(Ct);

        var lists = _gitHub.IssueListRequests("o", "r");
        Assert.Equal(3, lists.Count);
        Assert.Equal(string.Empty, lists[0].IfNoneMatch);
        Assert.False(string.IsNullOrEmpty(lists[1].IfNoneMatch));
        Assert.Equal(lists[1].IfNoneMatch, lists[2].IfNoneMatch);
        Assert.Equal(permissionCalls, _gitHub.PermissionCalls);
        Assert.Equal(eventCalls, _gitHub.IssueEventCalls("o", "r", 12));
        Assert.Single(await ReadEventsAsync());
    }

    // --- Fila 3: Ejecución en curso → misma clave, ningún Evento ni permiso nuevo ---

    [Fact]
    public async Task Issue_with_a_running_execution_gets_no_other_event_when_polled_again()
    {
        var coder = new BlockingCodingAgent();
        SeedLabeledIssue(12, labelEventId: 900, actor: Alice);
        await using var host = await StartAsync(Settings("o/r"), services: s => s.AddSingleton<ICodingAgent>(coder));
        await coder.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        var permissionCalls = _gitHub.PermissionCalls;

        // La issue cambia (p. ej. un comentario): GitHub ya no responde 304 y se vuelve a mirar su evento labeled.
        _gitHub.SetListedIssue("o", "r", 12, "2026-11-22T10:05:00Z");
        await host.Poller.RunCycleAsync(Ct);

        Assert.Equal(2, _gitHub.IssueEventCalls("o", "r", 12));
        Assert.Equal(permissionCalls, _gitHub.PermissionCalls);
        var row = Assert.Single(await ReadEventsAsync());
        Assert.NotNull(row.RunId);
    }

    // --- Fila 4: lanzada por otra Fuente después de etiquetarla → Ignored sin permiso ni segunda Ejecución ---

    [Fact]
    public async Task Issue_launched_by_another_source_after_labeling_is_ignored_without_asking_permission()
    {
        await using var host = await StartAsync(Settings("o/r"));
        using var client = host.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", CodeNetixFactory.TestAccessToken);
        using (var response = await client.PostAsJsonAsync(SubmitManualEventEndpoint.Route, new { url = "https://github.com/o/r/issues/12", requestId = Guid.NewGuid() }, Ct))
        {
            response.EnsureSuccessStatusCode();
        }

        SeedLabeledIssue(12, labelEventId: 900, actor: Alice, labeledAt: DateTimeOffset.UtcNow.AddHours(-1));
        await host.Poller.RunCycleAsync(Ct);

        var polled = Assert.Single(await ReadEventsAsync(), r => r.Source == "GitHub");
        Assert.Equal(("Ignored", GitHubPoller.AlreadyLaunchedReason), (polled.Status, polled.IgnoredReason));
        Assert.DoesNotContain(_gitHub.Requests, r => r.Kind == $"permission:o/r:{Alice}");
        Assert.Null(polled.RunId);
    }

    // --- Fila 5: reetiquetado → Evento nuevo con el nuevo id ---

    [Fact]
    public async Task Relabeling_creates_a_new_event_with_the_new_label_event_id()
    {
        SeedLabeledIssue(12, labelEventId: 900, actor: Alice);
        await using var host = await StartAsync(Settings("o/r"));

        _gitHub.AddIssueEvent("o", "r", 12, 940, "unlabeled", "codenetix", Alice);
        _gitHub.AddIssueEvent("o", "r", 12, 950, "labeled", "codenetix", Alice);
        _gitHub.SetListedIssue("o", "r", 12, "2026-11-22T11:00:00Z");
        await host.Poller.RunCycleAsync(Ct);

        var rows = await ReadEventsAsync();
        Assert.Equal(["gh:o/r#12:labeled:900", "gh:o/r#12:labeled:950"], rows.Select(r => r.DedupKey).Order(StringComparer.Ordinal));
        Assert.Equal("Accepted", rows.Single(r => r.DedupKey.EndsWith(":950", StringComparison.Ordinal)).Status);
    }

    // --- Fila 6: sin permiso o sin actor → Ignored "no autorizado" ---

    [Fact]
    public async Task Labeler_without_write_access_or_without_actor_is_ignored_as_not_authorized()
    {
        _gitHub.PermissionsByLogin["mallory"] = "read";
        SeedLabeledIssue(12, labelEventId: 900, actor: "mallory");
        SeedLabeledIssue(13, labelEventId: 901, actor: null);
        await using var host = await StartAsync(Settings("o/r"));

        var rows = await ReadEventsAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(("Ignored", ManualEventIngestion.UnauthorizedReason), (r.Status, r.IgnoredReason)));
        Assert.Equal("mallory", rows.Single(r => r.ObjectKey == "github:o/r#12").Actor);
        Assert.Null(rows.Single(r => r.ObjectKey == "github:o/r#13").Actor);
        Assert.Equal(1, _gitHub.PermissionCalls);
        Assert.All(rows, r => Assert.Null(r.RunId));
    }

    // --- Fila 7: GitHub no responde → nada persistido ni ETag; los demás siguen; el siguiente ciclo lo crea ---

    [Theory]
    [InlineData("list")]
    [InlineData("events")]
    [InlineData("permission")]
    public async Task GitHub_unavailable_persists_nothing_and_the_next_cycle_creates_the_event(string stage)
    {
        SeedLabeledIssue(12, labelEventId: 900, actor: Alice);
        switch (stage)
        {
            case "list":
                _gitHub.IssueListStatuses["o/r"] = 502;
                break;
            case "events":
                _gitHub.IssueEventsStatus = 502;
                break;
            default:
                _gitHub.PermissionStatus = 502;
                break;
        }

        await using var host = await StartAsync(Settings("o/r"), captureLogs: true);
        Assert.Empty(await ReadEventsAsync());

        _gitHub.IssueListStatuses.Clear();
        _gitHub.IssueEventsStatus = 200;
        _gitHub.PermissionStatus = 200;
        await host.Poller.RunCycleAsync(Ct);

        // Sin ETag guardado: la segunda consulta no es condicional y el Evento se crea.
        Assert.Equal(string.Empty, _gitHub.IssueListRequests("o", "r")[^1].IfNoneMatch);
        var row = Assert.Single(await ReadEventsAsync());
        Assert.Equal("Accepted", row.Status);
        Assert.Contains(_logs, l => l.Contains("Sondeo:", StringComparison.Ordinal) && l.Contains("GitHub no responde", StringComparison.Ordinal));
        Assert.DoesNotContain(_logs, l => l.Contains(Token, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failing_repository_does_not_stop_the_others()
    {
        _gitHub.IssueListStatuses["o/r"] = 500;
        SeedLabeledIssue(7, labelEventId: 700, actor: Alice, repo: "s");
        await using var host = await StartAsync(Settings("o/r,o/s"));

        var row = Assert.Single(await ReadEventsAsync());
        Assert.Equal("gh:o/s#7:labeled:700", row.DedupKey);
    }

    // --- Fila 8: rate limit → nada persistido, ciclo cortado, espera hasta el reset ---

    [Fact]
    public async Task Rate_limit_stops_the_cycle_persists_nothing_and_backs_off_until_the_reset()
    {
        SeedLabeledIssue(12, labelEventId: 900, actor: Alice);
        SeedLabeledIssue(7, labelEventId: 700, actor: Alice, repo: "s");
        await using var host = await StartAsync(Settings("o/r,o/s"));
        var before = _gitHub.IssueListRequests("o", "s").Count;
        _gitHub.RateLimitExhausted = true;
        _gitHub.RateLimitReset = DateTimeOffset.UtcNow.AddMinutes(20).ToUnixTimeSeconds();
        _gitHub.IssueListStatuses["o/r"] = 403;
        var result = await host.Poller.RunCycleAsync(Ct);

        Assert.True(result.RateLimited);
        Assert.InRange(result.RetryAfter!.Value, TimeSpan.FromMinutes(18), TimeSpan.FromMinutes(21));
        Assert.Equal(before, _gitHub.IssueListRequests("o", "s").Count); // el ciclo se corta: o/s no se pide
        Assert.Equal(2, (await ReadEventsAsync()).Count); // los del primer ciclo, nada nuevo
        Assert.Equal(TimeSpan.FromMinutes(20), GitHubPollingService.Backoff(TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(5)));
        Assert.Equal(TimeSpan.FromMinutes(5), GitHubPollingService.Backoff(TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(5)));
        Assert.Equal(TimeSpan.FromMinutes(5), GitHubPollingService.Backoff(null, TimeSpan.FromMinutes(5)));
        Assert.Equal(GitHubPollingOptions.MaxBackoff, GitHubPollingService.Backoff(TimeSpan.FromHours(5), TimeSpan.FromMinutes(5)));
    }

    // --- Fila 9: PR etiquetado → descartado ---

    [Fact]
    public async Task Labeled_pull_requests_are_skipped()
    {
        _gitHub.SetListedIssue("o", "r", 14, "2026-11-22T10:00:00Z", pullRequest: true);
        _gitHub.AddIssueEvent("o", "r", 14, 1400, "labeled", "codenetix", Alice);
        await using var host = await StartAsync(Settings("o/r"));

        Assert.Empty(await ReadEventsAsync());
        Assert.Equal(0, _gitHub.IssueEventCalls("o", "r", 14));
    }

    // --- Fila 10: sin evento labeled → nada, y no se vuelve a pedir hasta que cambie la issue ---

    [Fact]
    public async Task Issue_without_a_labeled_event_creates_nothing_and_is_not_asked_again_until_it_changes()
    {
        _gitHub.SetListedIssue("o", "r", 12, "2026-11-22T10:00:00Z");
        _gitHub.AddIssueEvent("o", "r", 12, 900, "labeled", "bug", Alice);
        await using var host = await StartAsync(Settings("o/r"));
        Assert.Equal(1, _gitHub.IssueEventCalls("o", "r", 12));

        _gitHub.SetListedIssue("o", "r", 13, "2026-11-22T10:01:00Z"); // otra issue cambia: ya no hay 304
        await host.Poller.RunCycleAsync(Ct);
        Assert.Equal(1, _gitHub.IssueEventCalls("o", "r", 12));

        _gitHub.SetListedIssue("o", "r", 12, "2026-11-22T10:02:00Z");
        await host.Poller.RunCycleAsync(Ct);
        Assert.Equal(2, _gitHub.IssueEventCalls("o", "r", 12));
        Assert.Empty(await ReadEventsAsync());
    }

    // --- Fila 11: modo Diagnostic → ningún ciclo ---

    [Fact]
    public async Task Diagnostic_instance_never_polls()
    {
        SeedLabeledIssue(12, labelEventId: 900, actor: Alice);
        var settings = new Dictionary<string, string?>
        {
            ["CODENETIX:GitHub:Token"] = Token, // falta la key del Proveedor
            ["CODENETIX:Polling:Repositories"] = "o/r",
        };
        await using var host = await StartAsync(settings, interval: TimeSpan.FromMilliseconds(50), waitForFirstCycle: false);

        var health = host.Factory.Services.GetRequiredService<IInstanceHealth>();
        Assert.True(await WaitForAsync(() => Task.FromResult(health.Status == InstanceHealthStatus.Diagnostic)));
        await Task.Delay(500, Ct);

        Assert.Equal(0, host.Poller.CompletedCycles);
        Assert.Equal(0, _gitHub.AllIssueListCalls);
        Assert.Empty(await ReadEventsAsync());
    }

    // --- Fila 12: sin repositorios (por defecto) → desactivado ---

    [Fact]
    public async Task Without_repositories_polling_is_disabled_and_GitHub_is_never_listed()
    {
        SeedLabeledIssue(12, labelEventId: 900, actor: Alice);
        await using var host = await StartAsync(Settings(null), interval: TimeSpan.FromMilliseconds(50), waitForFirstCycle: false);

        var health = host.Factory.Services.GetRequiredService<IInstanceHealth>();
        Assert.True(await WaitForAsync(() => Task.FromResult(health.Status == InstanceHealthStatus.Ready)));
        await Task.Delay(500, Ct);

        Assert.Contains(host.Factory.Services.GetServices<IHostedService>(), s => s is GitHubPollingService);
        Assert.False(host.Factory.Services.GetRequiredService<IOptions<GitHubPollingOptions>>().Value.Enabled);
        Assert.Equal(0, host.Poller.CompletedCycles);
        Assert.Equal(0, _gitHub.AllIssueListCalls);
    }

    // --- Fila 13: configuración inválida → Diagnostic con el motivo ---

    [Theory]
    [InlineData("CODENETIX:Polling:IntervalSeconds", "10", InstanceOptionsValidator.InvalidPollingInterval)]
    [InlineData("CODENETIX:Polling:IntervalSeconds", "90000", InstanceOptionsValidator.InvalidPollingInterval)]
    [InlineData("CODENETIX:Polling:Repositories", "foo", InstanceOptionsValidator.InvalidPollingRepository)]
    [InlineData("CODENETIX:Polling:Repositories", "o/r,https://github.com/o/s", InstanceOptionsValidator.InvalidPollingRepository)]
    [InlineData("CODENETIX:Polling:Repositories:0", "o/r/x", InstanceOptionsValidator.InvalidPollingRepository)]
    [InlineData("CODENETIX:Polling:Label", "a,b", InstanceOptionsValidator.InvalidPollingLabel)]
    public async Task Invalid_polling_configuration_puts_the_instance_in_Diagnostic(string key, string value, string reason)
    {
        var settings = Settings(key.StartsWith("CODENETIX:Polling:Repositories", StringComparison.Ordinal) ? null : "o/r");
        settings[key] = value;
        await using var host = await StartAsync(settings, waitForFirstCycle: false);

        var health = host.Factory.Services.GetRequiredService<IInstanceHealth>();
        Assert.True(await WaitForAsync(() => Task.FromResult(health.Status == InstanceHealthStatus.Diagnostic)));
        Assert.Contains(reason, health.Reasons);
        Assert.Equal(0, _gitHub.AllIssueListCalls);
    }

    [Fact]
    public void More_than_100_repositories_or_a_long_label_is_invalid()
    {
        var options = new InstanceOptions();
        options.Polling.Repositories = [.. Enumerable.Range(0, 101).Select(i => $"o/r{i}")];
        options.Polling.Label = new string('x', 51);

        var reasons = InstanceOptionsValidator.Validate(options);

        Assert.Contains(InstanceOptionsValidator.TooManyPollingRepositories, reasons);
        Assert.Contains(InstanceOptionsValidator.InvalidPollingLabel, reasons);
        Assert.DoesNotContain(InstanceOptionsValidator.InvalidPollingRepository, reasons);
    }

    // --- Adaptador GitHub: paginación, labeled más reciente, 304, rate limit ---

    [Fact]
    public async Task Adapter_pages_the_list_and_finds_the_most_recent_labeled_event_across_pages()
    {
        for (var n = 1; n <= 150; n++)
        {
            _gitHub.SetListedIssue("o", "r", n, $"2026-11-22T10:{n / 60:00}:{n % 60:00}Z");
        }

        _gitHub.SetListedIssue("o", "r", 151, "2026-11-22T12:00:00Z", pullRequest: true);
        _gitHub.SetListedIssue("o", "r", 152, "2026-11-22T12:00:00Z", label: "bug");
        _gitHub.AddIssueEvent("o", "r", 5, 10, "labeled", "Codenetix", "early");
        for (var i = 0; i < 230; i++)
        {
            _gitHub.AddIssueEvent("o", "r", 5, 100 + i, i % 2 == 0 ? "commented" : "unlabeled", "codenetix", "someone");
        }

        _gitHub.AddIssueEvent("o", "r", 5, 500, "labeled", "CODENETIX", "latest");
        _gitHub.AddIssueEvent("o", "r", 5, 501, "labeled", "bug", "other");
        using var client = ApiClient();
        var source = new LabeledIssueSource(client);

        var list = await source.ListOpenAsync("o", "r", "codenetix", null, Ct);
        var found = await source.FindLatestLabelEventAsync("o", "r", 5, "codenetix", Ct);

        Assert.Equal(LabeledIssueListStatus.Ok, list.Status);
        Assert.Equal(150, list.Issues.Count);
        Assert.DoesNotContain(list.Issues, i => i.Number is 151 or 152);
        Assert.False(list.Truncated);
        Assert.Null(list.ETag); // dos páginas: el ETag de la primera no describe la lista entera
        Assert.Equal(LabelEventLookupStatus.Found, found.Status);
        Assert.Equal((500L, "latest"), (found.Event!.Id, found.Event.Actor));
        Assert.Equal(["page=3"], _gitHub.Requests.Where(r => r.Kind.StartsWith("issue-events:o/r#5:", StringComparison.Ordinal)).Skip(1).Select(r => r.Kind.Split(':')[^1]));

        _gitHub.SetListedIssue("o", "q", 1, "2026-11-22T10:00:00Z");
        var single = await source.ListOpenAsync("o", "q", "codenetix", null, Ct);
        Assert.NotNull(single.ETag);
        var again = await source.ListOpenAsync("o", "q", "codenetix", single.ETag, Ct);
        Assert.Equal(LabeledIssueListStatus.NotModified, again.Status);
        Assert.Equal(single.ETag, _gitHub.IssueListRequests("o", "q")[^1].IfNoneMatch);
    }

    [Fact]
    public async Task Adapter_reports_rate_limit_and_unavailability_without_throwing()
    {
        _gitHub.RateLimitExhausted = true;
        _gitHub.RateLimitReset = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds();
        _gitHub.IssueListStatuses["o/r"] = 403;
        _gitHub.IssueEventsStatus = 503;
        using var client = ApiClient();
        var source = new LabeledIssueSource(client);

        var limited = await source.ListOpenAsync("o", "r", "codenetix", null, Ct);
        var failed = await source.FindLatestLabelEventAsync("o", "r", 1, "codenetix", Ct);

        Assert.Equal(LabeledIssueListStatus.Unavailable, limited.Status);
        Assert.True(limited.Failure!.RateLimited);
        Assert.InRange(limited.Failure.RetryAfter!.Value, TimeSpan.FromMinutes(8), TimeSpan.FromMinutes(11));
        Assert.StartsWith(GitHubApiClient.RateLimitedReason, limited.Failure.Reason, StringComparison.Ordinal);
        Assert.Equal(LabelEventLookupStatus.Unavailable, failed.Status);
        Assert.False(failed.Failure!.RateLimited);
        Assert.DoesNotContain(Token, limited.Failure.Reason + failed.Failure.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Shared_dedup_key_is_lowercase_and_follows_AD_4()
    {
        Assert.Equal("gh:acme/web#12:labeled:900", GitHubLabelEventKey.Format("Acme", "Web", 12, 900));
    }

    // --- Revisión 1 ----------------------------------------------------------------

    [Fact]
    public async Task A_failing_issue_does_not_block_older_issues_and_keeps_the_repository_pending()
    {
        SeedLabeledIssue(20, labelEventId: 2000, actor: Alice, updatedAt: "2026-11-22T12:00:00Z"); // la más reciente: va primero
        SeedLabeledIssue(12, labelEventId: 900, actor: Alice);
        _gitHub.IssueEventsStatuses["o/r#20"] = 502;
        await using var host = await StartAsync(Settings("o/r"));

        var row = Assert.Single(await ReadEventsAsync());
        Assert.Equal("gh:o/r#12:labeled:900", row.DedupKey);

        await host.Poller.RunCycleAsync(Ct);

        Assert.Equal(string.Empty, _gitHub.IssueListRequests("o", "r")[^1].IfNoneMatch); // pendiente: sin ETag
        Assert.Equal(2, _gitHub.IssueEventCalls("o", "r", 20)); // se reintenta la que falló
        Assert.Equal(1, _gitHub.IssueEventCalls("o", "r", 12)); // la resuelta no se vuelve a pedir
        Assert.Single(await ReadEventsAsync());
    }

    [Fact]
    public async Task Manual_event_older_than_the_label_or_a_label_without_date_does_not_suppress_the_polled_event()
    {
        await using var host = await StartAsync(Settings("o/r"));
        using var client = host.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", CodeNetixFactory.TestAccessToken);
        foreach (var number in new[] { 12, 13 })
        {
            using var response = await client.PostAsJsonAsync(SubmitManualEventEndpoint.Route, new { url = $"https://github.com/o/r/issues/{number}", requestId = Guid.NewGuid() }, Ct);
            response.EnsureSuccessStatusCode();
        }

        SeedLabeledIssue(12, labelEventId: 900, actor: Alice, labeledAt: DateTimeOffset.UtcNow.AddHours(1)); // etiquetada después del fix
        SeedLabeledIssue(13, labelEventId: 901, actor: Alice, omitCreatedAt: true); // GitHub sin created_at
        await host.Poller.RunCycleAsync(Ct);

        var polled = (await ReadEventsAsync()).Where(r => r.Source == "GitHub").ToList();
        Assert.Equal(2, polled.Count);
        Assert.All(polled, r => Assert.Equal(("Accepted", (string?)null), (r.Status, r.IgnoredReason)));
        Assert.Equal(2, _gitHub.Requests.Count(r => r.Kind == $"permission:o/r:{Alice}"));
    }

    [Fact]
    public async Task Deleted_user_ghost_without_permission_is_ignored_as_not_authorized()
    {
        _gitHub.PermissionStatusByLogin["ghost"] = 404; // GitHub muestra a los usuarios borrados como "ghost"
        SeedLabeledIssue(12, labelEventId: 900, actor: "ghost");
        await using var host = await StartAsync(Settings("o/r"));

        var row = Assert.Single(await ReadEventsAsync());
        Assert.Equal(("ghost", "Ignored", ManualEventIngestion.UnauthorizedReason), (row.Actor, row.Status, row.IgnoredReason));
        Assert.Contains(_gitHub.Requests, r => r.Kind == "permission:o/r:ghost");
    }

    [Fact]
    public async Task Service_waits_for_the_clamped_reset_after_a_rate_limit_before_the_next_cycle()
    {
        var clock = new ManualTimeProvider();
        var cycle = new ScriptedCycle(
            new PollCycleResult(0, 0, 1, true, TimeSpan.FromMinutes(20)),
            new PollCycleResult(0, 0, 0, false, null),
            new PollCycleResult(0, 0, 1, true, TimeSpan.FromHours(5)),
            new PollCycleResult(0, 0, 0, false, null));
        var options = new GitHubPollingOptions { Interval = TimeSpan.FromMinutes(5) };
        options.Repositories.Add(new PolledRepository("o", "r"));
        using var service = new GitHubPollingService(
            Options.Create(options),
            new ReadyHealth(),
            cycle,
            clock,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<GitHubPollingService>.Instance);
        await service.StartAsync(Ct);
        try
        {
            await WaitForCycleAndTimerAsync(1);

            // Rate limit pidiendo 20 min: ni al intervalo (5 min) ni justo antes del reset hay ciclo.
            await AdvanceAndExpectAsync(TimeSpan.FromMinutes(5), 1);
            await AdvanceAndExpectAsync(TimeSpan.FromMinutes(15) - TimeSpan.FromSeconds(1), 1);
            clock.Advance(TimeSpan.FromSeconds(1));
            await WaitForCycleAndTimerAsync(2);

            // Ciclo normal: el siguiente, al intervalo.
            clock.Advance(TimeSpan.FromMinutes(5));
            await WaitForCycleAndTimerAsync(3);

            // Rate limit pidiendo 5 h: se acota a 1 h.
            await AdvanceAndExpectAsync(TimeSpan.FromHours(1) - TimeSpan.FromSeconds(1), 3);
            clock.Advance(TimeSpan.FromSeconds(1));
            await WaitForCycleAndTimerAsync(4);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        async Task WaitForCycleAndTimerAsync(int cycles) =>
            Assert.True(await WaitForAsync(() => Task.FromResult(cycle.Calls == cycles && clock.PendingTimers == 1)), $"Se esperaban {cycles} ciclo(s) y un temporizador.");

        async Task AdvanceAndExpectAsync(TimeSpan by, int cycles)
        {
            clock.Advance(by);
            await Task.Delay(150, Ct);
            Assert.Equal(cycles, cycle.Calls);
        }
    }

    [Fact]
    public async Task Adapter_marks_more_than_ten_pages_as_truncated_without_ETag()
    {
        for (var n = 1; n <= 1001; n++)
        {
            _gitHub.SetListedIssue("o", "r", n, $"2026-11-22T{n / 3600:00}:{n / 60 % 60:00}:{n % 60:00}Z");
        }

        using var client = ApiClient();
        var list = await new LabeledIssueSource(client).ListOpenAsync("o", "r", "codenetix", null, Ct);

        Assert.Equal(LabeledIssueListStatus.Ok, list.Status);
        Assert.True(list.Truncated);
        Assert.Null(list.ETag);
        Assert.Equal(LabeledIssueSource.MaxListPages * LabeledIssueSource.PageSize, list.Issues.Count);
        Assert.Equal(LabeledIssueSource.MaxListPages, _gitHub.IssueListRequests("o", "r").Count);
    }

    [Fact]
    public async Task Poller_logs_a_truncated_list_and_warns_once_per_issue_and_updated_at_without_labeled_event()
    {
        var source = new ScriptedSource();
        var logger = new ListLogger<GitHubPoller>();
        var options = new GitHubPollingOptions();
        options.Repositories.Add(new PolledRepository("o", "r"));
        using var poller = new GitHubPoller(Options.Create(options), source, null!, logger);

        source.Issues = [new LabeledIssue(12, "a")];
        source.Truncated = true;
        await poller.RunCycleAsync(Ct);
        await poller.RunCycleAsync(Ct);
        source.Issues = [new LabeledIssue(12, "b")];
        await poller.RunCycleAsync(Ct);

        Assert.Equal(3, logger.Entries.Count(e => e.Level == LogLevel.Warning && e.Message.Contains("más issues etiquetadas", StringComparison.Ordinal)));
        Assert.Equal(2, logger.Entries.Count(e => e.Level == LogLevel.Warning && e.Message.Contains("no se encuentra su evento labeled", StringComparison.Ordinal)));
        Assert.Equal(2, source.LookupCalls); // con el mismo updated_at no se vuelve a pedir

        poller.Dispose();
        poller.Dispose(); // idempotente
        await Assert.ThrowsAsync<ObjectDisposedException>(() => poller.RunCycleAsync(Ct));
    }

    [Fact]
    public async Task Adapter_reads_the_events_of_at_most_five_pages_from_the_end()
    {
        _gitHub.AddIssueEvent("o", "r", 5, 1, "labeled", "codenetix", Alice); // solo en la página 1
        for (var i = 0; i < 650; i++)
        {
            _gitHub.AddIssueEvent("o", "r", 5, 10 + i, "commented", null, "someone");
        }

        using var client = ApiClient();
        var found = await new LabeledIssueSource(client).FindLatestLabelEventAsync("o", "r", 5, "codenetix", Ct);

        Assert.Equal(LabelEventLookupStatus.NotFound, found.Status);
        Assert.Equal(
            ["page=1", "page=7", "page=6", "page=5", "page=4", "page=3"],
            _gitHub.Requests.Where(r => r.Kind.StartsWith("issue-events:o/r#5:", StringComparison.Ordinal)).Select(r => r.Kind.Split(':')[^1]));
    }

    [Theory]
    [InlineData(404)]
    [InlineData(410)]
    public async Task Adapter_treats_missing_issue_events_as_not_found(int status)
    {
        _gitHub.IssueEventsStatus = status;
        using var client = ApiClient();

        var found = await new LabeledIssueSource(client).FindLatestLabelEventAsync("o", "r", 5, "codenetix", Ct);

        Assert.Equal(LabelEventLookupStatus.NotFound, found.Status);
    }

    [Theory]
    [InlineData("429")]
    [InlineData("retry-after-seconds")]
    [InlineData("retry-after-date")]
    [InlineData("secondary-message")]
    [InlineData("reset-in-the-past")]
    public async Task Adapter_recognizes_rate_limits_and_their_wait(string kind)
    {
        TimeSpan min, max;
        switch (kind)
        {
            case "429":
                _gitHub.IssueListStatuses["o/r"] = 429;
                _gitHub.RetryAfterHeader = "30";
                (min, max) = (TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
                break;
            case "retry-after-seconds":
                _gitHub.IssueListStatuses["o/r"] = 403;
                _gitHub.RetryAfterHeader = "120";
                (min, max) = (TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(120));
                break;
            case "retry-after-date":
                _gitHub.IssueListStatuses["o/r"] = 403;
                _gitHub.RetryAfterHeader = DateTimeOffset.UtcNow.AddSeconds(300).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                (min, max) = (TimeSpan.FromSeconds(290), TimeSpan.FromSeconds(301));
                break;
            case "secondary-message":
                _gitHub.IssueListStatuses["o/r"] = 403;
                _gitHub.IssueListErrorMessage = "You have exceeded a secondary rate limit. Please wait a few minutes before you try again.";
                (min, max) = (GitHubApiClient.SecondaryRateLimitMinimumWait, GitHubApiClient.SecondaryRateLimitMinimumWait);
                break;
            default:
                _gitHub.IssueListStatuses["o/r"] = 403;
                _gitHub.RateLimitExhausted = true;
                _gitHub.RateLimitReset = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds();
                (min, max) = (TimeSpan.Zero, TimeSpan.Zero);
                break;
        }

        using var client = ApiClient();
        var list = await new LabeledIssueSource(client).ListOpenAsync("o", "r", "codenetix", null, Ct);

        Assert.Equal(LabeledIssueListStatus.Unavailable, list.Status);
        Assert.True(list.Failure!.RateLimited);
        Assert.InRange(list.Failure.RetryAfter!.Value, min, max);
    }

    [Fact]
    public async Task A_plain_403_without_rate_limit_signals_is_not_a_rate_limit()
    {
        _gitHub.IssueListStatuses["o/r"] = 403;
        _gitHub.IssueListErrorMessage = "Resource not accessible by integration";
        using var client = ApiClient();

        var list = await new LabeledIssueSource(client).ListOpenAsync("o", "r", "codenetix", null, Ct);

        Assert.False(list.Failure!.RateLimited);
        Assert.Null(list.Failure.RetryAfter);
    }

    // --- Utilidades ----------------------------------------------------------------

    private sealed class ReadyHealth : IInstanceHealth
    {
        public InstanceHealthStatus Status => InstanceHealthStatus.Ready;

        public IReadOnlyList<string> Reasons => [];

        public Task WhenReadyAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>Ciclo falso que devuelve los resultados en orden (el último se repite).</summary>
    private sealed class ScriptedCycle(params PollCycleResult[] results) : IGitHubPollCycle
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<PollCycleResult> RunCycleAsync(CancellationToken cancellationToken)
        {
            var index = Interlocked.Increment(ref _calls) - 1;
            return Task.FromResult(results[Math.Min(index, results.Length - 1)]);
        }
    }

    /// <summary>Fuente falsa: lista fija (siempre <c>Ok</c>) y ningún evento <c>labeled</c>.</summary>
    private sealed class ScriptedSource : ILabeledIssueSource
    {
        private int _lookupCalls;

        public IReadOnlyList<LabeledIssue> Issues { get; set; } = [];

        public bool Truncated { get; set; }

        public int LookupCalls => Volatile.Read(ref _lookupCalls);

        public Task<LabeledIssueList> ListOpenAsync(string owner, string repo, string label, string? etag, CancellationToken cancellationToken) =>
            Task.FromResult(new LabeledIssueList(LabeledIssueListStatus.Ok, Issues, null, Truncated, null));

        public Task<LabelEventLookup> FindLatestLabelEventAsync(string owner, string repo, long issueNumber, string label, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _lookupCalls);
            return Task.FromResult(LabelEventLookup.NotFound);
        }
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue((logLevel, formatter(state, exception)));
    }

    /// <summary>Reloj manual: los temporizadores solo vencen con <see cref="Advance"/>.</summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object _lock = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _now = new(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);

        public int PendingTimers
        {
            get
            {
                lock (_lock)
                {
                    return _timers.Count(t => t.Due is not null);
                }
            }
        }

        public override DateTimeOffset GetUtcNow()
        {
            lock (_lock)
            {
                return _now;
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            return timer;
        }

        public void Advance(TimeSpan by)
        {
            List<ManualTimer> due;
            lock (_lock)
            {
                _now += by;
                due = [.. _timers.Where(t => t.Due is { } at && at <= _now)];
                foreach (var timer in due)
                {
                    timer.Due = null;
                }
            }

            foreach (var timer in due)
            {
                timer.Fire();
            }
        }

        private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
        {
            public DateTimeOffset? Due { get; set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._lock)
                {
                    Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
                    if (!owner._timers.Contains(this))
                    {
                        owner._timers.Add(this);
                    }
                }

                return true;
            }

            public void Fire() => callback(state);

            public void Dispose()
            {
                lock (owner._lock)
                {
                    owner._timers.Remove(this);
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    // --- Utilidades (comunes) --------------------------------------------------------

    private void SeedLabeledIssue(long number, long labelEventId, string? actor, string repo = "r", DateTimeOffset? labeledAt = null, string updatedAt = "2026-11-22T10:00:00Z", bool omitCreatedAt = false)
    {
        _gitHub.SetListedIssue("o", repo, number, updatedAt);
        _gitHub.AddIssueEvent("o", repo, number, labelEventId - 1, "labeled", "bug", actor);
        _gitHub.AddIssueEvent("o", repo, number, labelEventId, "labeled", "codenetix", actor, labeledAt, omitCreatedAt);
    }

    private static Dictionary<string, string?> Settings(string? repositories)
    {
        var settings = new Dictionary<string, string?>(CodeNetixFactory.CompleteSettings);
        if (repositories is not null)
        {
            settings["CODENETIX:Polling:Repositories"] = repositories;
        }

        return settings;
    }

    private GitHubApiClient ApiClient() =>
        new(Options.Create(new GitHubOptions { Token = Token, ApiUrl = _gitHub.BaseUrl }));

    private async Task<PollingHost> StartAsync(
        Dictionary<string, string?> settings,
        TimeSpan? interval = null,
        Action<IServiceCollection>? services = null,
        bool waitForFirstCycle = true,
        bool captureLogs = false)
    {
        var baseFactory = new CodeNetixFactory(_dataDirectory, _webRoot, settings, gitHub: _gitHub);
        var factory = baseFactory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            if (interval is { } value)
            {
                s.PostConfigure<GitHubPollingOptions>(o => o.Interval = value);
            }

            if (captureLogs)
            {
                s.AddSingleton<ILoggerProvider>(new RawLoggerProvider(_logs));
            }

            services?.Invoke(s);
        }));

        _ = factory.Services; // arranca el host
        var host = new PollingHost(baseFactory, factory);
        if (waitForFirstCycle)
        {
            Assert.True(await WaitForAsync(() => Task.FromResult(host.Poller.CompletedCycles >= 1)), "El primer ciclo debe lanzarse al estar Ready.");
        }

        return host;
    }

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

    private async Task<List<EventRow>> ReadEventsAsync()
    {
        if (!File.Exists(DatabasePath))
        {
            return [];
        }

        await using var connection = new SqliteConnection($"Data Source={DatabasePath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT source, object_key, dedup_key, actor, status, ignored_reason, payload_json, run_id FROM ing_events ORDER BY received_at, id;";
        var rows = new List<EventRow>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(new EventRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7)));
        }

        return rows;
    }

    public async ValueTask DisposeAsync()
    {
        await _gitHub.DisposeAsync();
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

    private sealed record EventRow(string Source, string ObjectKey, string DedupKey, string? Actor, string Status, string? IgnoredReason, string Payload, string? RunId);

    private sealed class PollingHost(CodeNetixFactory baseFactory, WebApplicationFactory<Program> factory) : IAsyncDisposable
    {
        public WebApplicationFactory<Program> Factory => factory;

        public GitHubPoller Poller => factory.Services.GetRequiredService<GitHubPoller>();

        public async ValueTask DisposeAsync()
        {
            await factory.DisposeAsync();
            await baseFactory.DisposeAsync();
        }
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

    /// <summary>Logs sin redactar: prueba que el sondeo no escribe el token, ni siquiera antes del redactor.</summary>
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
