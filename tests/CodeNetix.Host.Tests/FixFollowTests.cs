using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CodeNetix.Host.Tests;

/// <summary><c>codenetix fix --follow</c> contra un servidor falso y contra el host real (historia 1.6).</summary>
public sealed class FixFollowTests : IDisposable
{
    private const string IssueUrl = "https://github.com/O/R/issues/12";
    private const string Token = "follow-test-token-0123456789";
    private static readonly TimeSpan FastPoll = TimeSpan.FromMilliseconds(10);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "codenetix-tests-" + Guid.NewGuid().ToString("N"));
    private readonly StringWriter _stdout = new();
    private readonly StringWriter _stderr = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IConfiguration Config(string url, string token = Token) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CODENETIX:Host:Url"] = url,
            ["CODENETIX:Host:AccessToken"] = token,
        }).Build();

    private Task<int> RunAsync(string[] args, IConfiguration configuration, HttpMessageHandler? handler = null, TimeSpan? noProgressTimeout = null) =>
        FixMode.RunAsync(args, configuration, _stdout, _stderr, handler, [TimeSpan.Zero], Ct, FastPoll, noProgressTimeout);

    [Fact]
    public async Task Follow_gives_up_when_the_event_never_gets_a_run()
    {
        await using var api = await FollowApi.StartAsync();
        api.EventPollsBeforeRun = int.MaxValue;

        var exitCode = await RunAsync(["--follow", IssueUrl], Config(api.BaseUrl), noProgressTimeout: TimeSpan.FromMilliseconds(200));

        Assert.Equal(CodeNetixCommandLine.ExitFailure, exitCode);
        var error = _stderr.ToString();
        Assert.Contains(api.EventId.ToString(), error, StringComparison.Ordinal);
        Assert.Contains($"GET /api/events/{api.EventId}", error, StringComparison.Ordinal);
        Assert.Equal(0, api.RunEventPolls);
    }

    [Fact]
    public async Task Follow_gives_up_when_the_run_stops_producing_events()
    {
        await using var api = await FollowApi.StartAsync();
        api.RunEvents.Add(new { type = "RunCreated", eventId = api.EventId, objectKey = "github:o/r#12", seq = 1, runId = api.RunId });
        api.RunEvents.Add(new { type = "StateChanged", from = (string?)null, to = "Received", seq = 2, runId = api.RunId });

        var exitCode = await RunAsync(["--follow", IssueUrl], Config(api.BaseUrl), noProgressTimeout: TimeSpan.FromMilliseconds(200));

        Assert.Equal(CodeNetixCommandLine.ExitFailure, exitCode);
        Assert.Equal("[2] StateChanged → Received", StdoutLines[^1]);
        Assert.Contains($"GET /api/runs/{api.RunId}", _stderr.ToString(), StringComparison.Ordinal);
        Assert.True(api.RunEventPolls > 1);
    }

    private string[] StdoutLines => _stdout.ToString().ReplaceLineEndings("\n").TrimEnd().Split('\n');

    private static object[] FailedRun(Guid runId, Guid eventId) =>
    [
        new { type = "RunCreated", eventId, objectKey = "github:o/r#12", seq = 1, runId, at = DateTimeOffset.UtcNow },
        new { type = "StateChanged", from = (string?)null, to = "Received", failureReason = (string?)null, detail = (string?)null, seq = 2, runId, at = DateTimeOffset.UtcNow },
        new { type = "StateChanged", from = "Received", to = "Failed", failureReason = "StageError", detail = "Análisis no disponible todavía", seq = 3, runId, at = DateTimeOffset.UtcNow },
    ];

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Follow_prints_every_event_and_exits_1_on_Failed(bool followFirst)
    {
        await using var api = await FollowApi.StartAsync();
        api.RunEvents.AddRange(FailedRun(api.RunId, api.EventId));
        api.EventPollsBeforeRun = 2; // el Dispatcher todavía no ha creado la Ejecución

        string[] args = followFirst ? ["--follow", IssueUrl] : [IssueUrl, "--follow"];
        var exitCode = await RunAsync(args, Config(api.BaseUrl));

        Assert.Equal(CodeNetixCommandLine.ExitFailure, exitCode);
        Assert.Equal(
        [
            $"Evento {api.EventId} registrado.",
            $"Ejecución {api.RunId}:",
            "[1] RunCreated github:o/r#12",
            "[2] StateChanged → Received",
            "[3] StateChanged Received → Failed (StageError: Análisis no disponible todavía)",
        ], StdoutLines);
        Assert.Empty(_stderr.ToString());
        Assert.True(api.EventPolls >= 3);
        Assert.All(api.Authorizations, a => Assert.Equal($"Bearer {Token}", a));
    }

    [Fact]
    public async Task Follow_polls_with_afterSeq_until_a_terminal_state_and_exits_0_on_PullRequestCreated()
    {
        await using var api = await FollowApi.StartAsync();
        var runId = api.RunId;
        object[] events =
        [
            new { type = "RunCreated", eventId = api.EventId, objectKey = "github:o/r#12", seq = 1, runId },
            new { type = "StateChanged", from = (string?)null, to = "Received", seq = 2, runId },
            new { type = "StateChanged", from = "Received", to = "Analyzing", seq = 3, runId },
            new { type = "OutputAppended", seq = 4, runId }, // tipo desconocido para el cliente
            new { type = "StateChanged", from = "Deciding", to = "PullRequestCreated", seq = 5, runId },
        ];
        api.RunEvents.AddRange(events);
        api.VisibleEvents = 2; // los eventos se van publicando entre sondeos

        var exitCode = await RunAsync([IssueUrl, "--follow"], Config(api.BaseUrl));

        Assert.Equal(CodeNetixCommandLine.ExitOk, exitCode);
        Assert.Equal(
        [
            "[1] RunCreated github:o/r#12",
            "[2] StateChanged → Received",
            "[3] StateChanged Received → Analyzing",
            "[4] OutputAppended",
            "[5] StateChanged Deciding → PullRequestCreated",
        ], StdoutLines.Skip(2));
        Assert.Contains("0", api.AfterSeqs);
        Assert.Contains("2", api.AfterSeqs);
        Assert.Contains("4", api.AfterSeqs);
    }

    [Fact]
    public async Task Follow_exits_1_on_Cancelled()
    {
        await using var api = await FollowApi.StartAsync();
        api.RunEvents.Add(new { type = "StateChanged", from = "Coding", to = "Cancelled", seq = 1, runId = api.RunId });

        var exitCode = await RunAsync(["--follow", IssueUrl], Config(api.BaseUrl));

        Assert.Equal(CodeNetixCommandLine.ExitFailure, exitCode);
        Assert.Equal("[1] StateChanged Coding → Cancelled", StdoutLines[^1]);
    }

    [Fact]
    public async Task Follow_of_an_event_ignored_by_the_Dispatcher_prints_the_reason_and_exits_1()
    {
        await using var api = await FollowApi.StartAsync();
        api.IgnoredReason = "ya hay una Ejecución en curso para este objeto (abcd1234)";

        var exitCode = await RunAsync(["--follow", IssueUrl], Config(api.BaseUrl));

        Assert.Equal(CodeNetixCommandLine.ExitFailure, exitCode);
        Assert.Equal($"Evento {api.EventId} ignorado: ya hay una Ejecución en curso para este objeto (abcd1234).", _stderr.ToString().Trim());
        Assert.Equal(0, api.RunEventPolls);
    }

    [Fact]
    public async Task Follow_tolerates_transient_errors_while_polling()
    {
        await using var api = await FollowApi.StartAsync();
        api.RunEvents.AddRange(FailedRun(api.RunId, api.EventId));
        api.TransientRunEventFailures = 2;

        var exitCode = await RunAsync(["--follow", IssueUrl], Config(api.BaseUrl));

        Assert.Equal(CodeNetixCommandLine.ExitFailure, exitCode);
        Assert.Equal(3, StdoutLines.Count(l => l.StartsWith('[')));
        Assert.Empty(_stderr.ToString());
    }

    [Fact]
    public async Task Without_follow_only_the_event_is_registered()
    {
        await using var api = await FollowApi.StartAsync();

        var exitCode = await RunAsync([IssueUrl], Config(api.BaseUrl));

        Assert.Equal(CodeNetixCommandLine.ExitOk, exitCode);
        Assert.Equal([$"Evento {api.EventId} registrado."], StdoutLines);
        Assert.Equal(0, api.EventPolls);
    }

    public static TheoryData<string[]> FollowUsageCases => new()
    {
        new[] { "--follow" },
        new[] { "--follow", "--follow" },
        new[] { "--folow", IssueUrl },
    };

    [Theory]
    [MemberData(nameof(FollowUsageCases))]
    public async Task Follow_without_a_url_or_with_an_unknown_option_prints_usage(string[] args)
    {
        var exitCode = await RunAsync(args, Config("http://127.0.0.1:1"));

        Assert.Equal(CodeNetixCommandLine.ExitUsage, exitCode);
        Assert.Contains("--follow", _stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task End_to_end_follow_prints_every_event_and_exits_1()
    {
        var dataDirectory = Path.Combine(_root, "host-data");
        var webRoot = Path.Combine(_root, "wwwroot");
        Directory.CreateDirectory(webRoot);
        await using var factory = new CodeNetixFactory(dataDirectory, webRoot, new Dictionary<string, string?>(CodeNetixFactory.CompleteSettings));
        using var handler = factory.Server.CreateHandler();

        var exitCode = await FixMode.RunAsync(
            ["--follow", IssueUrl],
            Config(factory.Server.BaseAddress.ToString(), CodeNetixFactory.TestAccessToken),
            _stdout,
            _stderr,
            handler,
            [TimeSpan.Zero],
            Ct,
            TimeSpan.FromMilliseconds(50));

        Assert.True(exitCode == CodeNetixCommandLine.ExitFailure, _stderr.ToString());
        var lines = StdoutLines;
        Assert.Matches(new Regex("^Evento [0-9a-f-]{36} registrado\\.$"), lines[0]);
        Assert.Matches(new Regex("^Ejecución [0-9a-f-]{36}:$"), lines[1]);
        var events = lines.Skip(2).ToList();
        Assert.Equal(14, events.Count);
        Assert.Equal("[1] RunCreated github:o/r#12", events[0]);
        Assert.Equal("[2] StateChanged → Received", events[1]);
        Assert.Matches(new Regex("^\\[3\\] RepositoryPrepared [0-9a-f]{40}$"), events[2]);
        Assert.Equal("[4] RepoConfigResolved defaults", events[3]);
        Assert.Equal("[5] StateChanged Received → Analyzing", events[4]);
        Assert.Equal("[6] SandboxCreated " + FakeSandboxManager.Image, events[5]);
        Assert.Equal("[7] SpecGenerated claude-sonnet-5-5 (1 intento(s))", events[6]);
        Assert.Equal("[8] StateChanged Analyzing → Coding", events[7]);
        Assert.Equal("[9] OutputAppended", events[8]);
        Assert.Equal("[10] StateChanged Coding → Reviewing", events[9]);
        Assert.Equal("[11] VerdictRecorded ronda 1 security → approved (0 hallazgo(s))", events[10]);
        Assert.Equal("[12] VerdictRecorded ronda 1 quality → approved (0 hallazgo(s))", events[11]);
        Assert.Equal("[13] VerdictRecorded ronda 1 tests → approved (0 hallazgo(s))", events[12]);
        Assert.Equal("[14] StateChanged Reviewing → Failed (StageError: Creación de PR no disponible)", events[13]);
        Assert.Empty(_stderr.ToString());
    }

    public void Dispose()
    {
        _stdout.Dispose();
        _stderr.Dispose();
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

    /// <summary>
    /// API falsa: <c>POST /api/events/manual</c> (201 Accepted), <c>GET /api/events/{id}</c> (sin <c>runId</c> durante
    /// <see cref="EventPollsBeforeRun"/> sondeos, o <c>Ignored</c>) y <c>GET /api/runs/{id}/events?afterSeq=N</c>.
    /// </summary>
    private sealed class FollowApi : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private int _eventPolls;
        private int _runEventPolls;

        private FollowApi()
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Services.AddProblemDetails();
            _app = builder.Build();

            _app.MapPost("/api/events/manual", (HttpContext context) =>
            {
                Authorizations.Enqueue(context.Request.Headers.Authorization.ToString());
                return Results.Json(new { eventId = EventId, created = true, status = "Accepted" }, statusCode: 201);
            });

            _app.MapGet("/api/events/{id:guid}", (HttpContext context, Guid id) =>
            {
                Authorizations.Enqueue(context.Request.Headers.Authorization.ToString());
                var n = Interlocked.Increment(ref _eventPolls);
                if (id != EventId)
                {
                    return Results.Problem(statusCode: 404, detail: "No existe.");
                }

                if (IgnoredReason is not null)
                {
                    return Results.Json(new { eventId = EventId, status = "Ignored", reason = IgnoredReason, runId = (Guid?)null });
                }

                return n <= EventPollsBeforeRun
                    ? Results.Json(new { eventId = EventId, status = "Accepted", reason = (string?)null, runId = (Guid?)null })
                    : Results.Json(new { eventId = EventId, status = "Accepted", reason = (string?)null, runId = RunId });
            });

            _app.MapGet("/api/runs/{id}/events", (HttpContext context, string id, long? afterSeq) =>
            {
                Authorizations.Enqueue(context.Request.Headers.Authorization.ToString());
                var n = Interlocked.Increment(ref _runEventPolls);
                AfterSeqs.Enqueue((afterSeq ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (n <= TransientRunEventFailures)
                {
                    return Results.Problem(statusCode: 500, detail: "fallo transitorio");
                }

                if (!Guid.TryParse(id, out var runId) || runId != RunId)
                {
                    return Results.Problem(statusCode: 404, detail: "No existe.");
                }

                // Publicación gradual: en el sondeo n se ven n * VisibleEvents eventos.
                var visible = VisibleEvents <= 0 ? RunEvents.Count : Math.Min(RunEvents.Count, n * VisibleEvents);
                var after = afterSeq ?? 0;
                return Results.Json(RunEvents.Take(visible).Where((_, i) => i + 1 > after).ToArray());
            });
        }

        public Guid EventId { get; } = Guid.CreateVersion7();

        public Guid RunId { get; } = Guid.CreateVersion7();

        public List<object> RunEvents { get; } = [];

        public int EventPollsBeforeRun { get; set; }

        public int VisibleEvents { get; set; }

        public int TransientRunEventFailures { get; set; }

        public string? IgnoredReason { get; set; }

        public int EventPolls => _eventPolls;

        public int RunEventPolls => _runEventPolls;

        public ConcurrentQueue<string> Authorizations { get; } = new();

        public ConcurrentQueue<string> AfterSeqs { get; } = new();

        public string BaseUrl => _app.Urls.First();

        public static async Task<FollowApi> StartAsync()
        {
            var api = new FollowApi();
            await api._app.StartAsync(Ct);
            return api;
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync(CancellationToken.None);
            await _app.DisposeAsync();
        }
    }
}
