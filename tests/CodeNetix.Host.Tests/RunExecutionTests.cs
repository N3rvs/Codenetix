using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CodeNetix.Contracts;
using CodeNetix.Ingestion.Data;
using CodeNetix.Persistence;
using CodeNetix.Pipeline.Data;
using CodeNetix.Pipeline.Domain;
using CodeNetix.Pipeline.Events;
using CodeNetix.Pipeline.Features.DispatchEvents;
using CodeNetix.Pipeline.Features.RunPipeline;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace CodeNetix.Host.Tests;

/// <summary>Matriz de la historia 1.6: Dispatcher, <c>PipelineRun</c>, <c>run_events</c> y <c>GET /api/runs</c>.</summary>
public sealed class RunExecutionTests : IDisposable
{
    private const string IssueUrl = "https://github.com/O/R/issues/12";
    private const string ObjectKey = "github:o/r#12";
    private const string WarmUpUrl = "https://github.com/O/R/issues/99";
    private const string PoisonUrl = "https://github.com/O/R/issues/666";
    private const string PoisonObjectKey = "github:o/r#666";
    private const string GitHubToken = "ghp_testtoken0123456789abcdefghijklmn"; // el de CompleteSettings
    private static readonly TimeSpan DispatchDeadline = TimeSpan.FromSeconds(2);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "codenetix-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _dataDirectory;
    private readonly string _webRoot;

    public RunExecutionTests()
    {
        _dataDirectory = Path.Combine(_root, "data");
        _webRoot = Path.Combine(_root, "wwwroot");
        Directory.CreateDirectory(_webRoot);
        File.WriteAllText(Path.Combine(_webRoot, "index.html"), "<!doctype html><html><body>Panel</body></html>");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string DatabasePath => Path.Combine(_dataDirectory, PersistenceOptions.DatabaseFileName);

    private CodeNetixFactory CreateReadyFactory() =>
        new(_dataDirectory, _webRoot, new Dictionary<string, string?>(CodeNetixFactory.CompleteSettings));

    // --- Evento aceptado ---------------------------------------------------------------

    [Fact]
    public async Task Accepted_event_becomes_a_run_within_two_seconds_with_its_events_in_order()
    {
        await using var factory = CreateReadyFactory();
        using var client = factory.CreateAuthorizedClient();

        var stopwatch = Stopwatch.StartNew();
        var eventId = await SubmitAsync(client);
        var info = await WaitForEventAsync(client, eventId, e => e.RunId is not null, DispatchDeadline);
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed <= DispatchDeadline, $"Tardó {stopwatch.Elapsed}.");
        Assert.Equal("Accepted", info.Status);
        var runId = info.RunId!.Value;

        var run = await WaitForRunAsync(client, runId.ToString(), r => r.State == "PullRequestCreated");
        Assert.Equal(runId, run.RunId);
        Assert.Equal(7, run.RunId.Version);
        Assert.Matches("^[0-9a-f]{8}$", run.ShortId);
        Assert.Null(run.FailureReason);
        Assert.Null(run.FailureDetail);
        Assert.Equal(eventId, run.EventId);
        Assert.Equal(ObjectKey, run.ObjectKey);
        Assert.True(run.UpdatedAt >= run.CreatedAt);
        Assert.Equal(TimeSpan.Zero, run.CreatedAt.Offset);

        // GET /api/runs/<shortId> devuelve lo mismo que con el GUID, con los eventos en orden de seq.
        using var byShortId = await client.GetAsync($"/api/runs/{run.ShortId}", Ct);
        Assert.Equal(HttpStatusCode.OK, byShortId.StatusCode);
        using var json = JsonDocument.Parse(await byShortId.Content.ReadAsStringAsync(Ct));
        var root = json.RootElement;
        Assert.Equal(runId, root.GetProperty("runId").GetGuid());
        Assert.Equal("PullRequestCreated", root.GetProperty("state").GetString());
        var events = root.GetProperty("events").EnumerateArray().ToList();
        Assert.Equal(Enumerable.Range(1, 18).Select(i => (long)i), events.Select(e => e.GetProperty("seq").GetInt64()));
        Assert.All(events, e => Assert.Equal(runId, e.GetProperty("runId").GetGuid()));
        Assert.Equal(
            [
                "RunCreated", "StateChanged", "RepositoryPrepared", "RepoConfigResolved", "StateChanged",
                "SandboxCreated", "SpecGenerated", "StateChanged", "OutputAppended", "StateChanged",
                "VerdictRecorded", "VerdictRecorded", "VerdictRecorded", "StateChanged", "RiskClassified", "PullRequestOpened",
                "StateChanged", "SandboxReleased",
            ],
            events.Select(e => e.GetProperty("type").GetString()));

        Assert.Equal(eventId, events[0].GetProperty("eventId").GetGuid());
        Assert.Equal(ObjectKey, events[0].GetProperty("objectKey").GetString());

        Assert.Equal(JsonValueKind.Null, events[1].GetProperty("from").ValueKind);
        Assert.Equal("Received", events[1].GetProperty("to").GetString());

        Assert.Matches("^[0-9a-f]{40}$", events[2].GetProperty("baseSha").GetString());
        Assert.Equal("defaults", events[3].GetProperty("source").GetString());

        Assert.Equal("Received", events[4].GetProperty("from").GetString());
        Assert.Equal("Analyzing", events[4].GetProperty("to").GetString());

        // AD-16 paso 2: el Sandbox se crea tras resolver la configuración y se libera al final (en el finally).
        Assert.Equal(FakeSandboxManager.Image, events[5].GetProperty("image").GetString());

        Assert.Equal(1, events[6].GetProperty("attempts").GetInt32());
        Assert.Equal("claude-sonnet-5-5", events[6].GetProperty("model").GetString());

        Assert.Equal("Analyzing", events[7].GetProperty("from").GetString());
        Assert.Equal("Coding", events[7].GetProperty("to").GetString());

        Assert.Equal("Coding", events[9].GetProperty("from").GetString());
        Assert.Equal("Reviewing", events[9].GetProperty("to").GetString());

        Assert.Equal("Reviewing", events[13].GetProperty("from").GetString());
        Assert.Equal("Deciding", events[13].GetProperty("to").GetString());

        Assert.Equal("RiskClassified", events[14].GetProperty("type").GetString());
        Assert.Equal("Minor", events[14].GetProperty("riskClass").GetString());

        Assert.Equal("PullRequestOpened", events[15].GetProperty("type").GetString());
        Assert.True(events[15].GetProperty("number").GetInt64() > 0);

        Assert.Equal("Deciding", events[16].GetProperty("from").GetString());
        Assert.Equal("PullRequestCreated", events[16].GetProperty("to").GetString());
        Assert.True(events[16].GetProperty("at").GetDateTimeOffset() >= events[0].GetProperty("at").GetDateTimeOffset());




        var session = Assert.Single(factory.Sandbox.Sessions);
        Assert.Equal(runId, session.RunId);
        Assert.Equal(1, session.Disposals);

        // ing_events: despachado y enlazado a la Ejecución.
        await using var connection = await OpenReadOnlyAsync();
        Assert.Equal(runId, Guid.Parse((string)(await ScalarAsync(connection, $"SELECT run_id FROM ing_events WHERE id = '{eventId.ToString().ToUpperInvariant()}';"))!));
        Assert.NotNull(await ScalarAsync(connection, $"SELECT dispatched_at FROM ing_events WHERE id = '{eventId.ToString().ToUpperInvariant()}';"));
    }

    [Fact]
    public async Task Every_run_has_its_StateChanged_events_with_gapless_seq()
    {
        await using var factory = CreateReadyFactory();
        using var client = factory.CreateAuthorizedClient();

        var runIds = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var eventId = await SubmitAsync(client);
            var info = await WaitForEventAsync(client, eventId, e => e.RunId is not null, TimeSpan.FromSeconds(5));
            var run = await WaitForRunAsync(client, info.RunId!.Value.ToString(), r => r.State == "PullRequestCreated");
            runIds.Add(run.RunId);
        }

        Assert.Equal(3, runIds.Distinct().Count());
        await using var connection = await OpenReadOnlyAsync();
        Assert.Equal(3L, await ScalarAsync(connection, "SELECT COUNT(*) FROM run_runs;"));

        // Cada fila de run_runs: seq 1..n sin huecos, y un StateChanged por cada Estado recorrido.
        Assert.Equal(0L, await ScalarAsync(connection,
            "SELECT COUNT(*) FROM run_runs r WHERE (SELECT COUNT(*) FROM run_events e WHERE e.run_id = r.id) <> (SELECT MAX(seq) FROM run_events e WHERE e.run_id = r.id) OR (SELECT MIN(seq) FROM run_events e WHERE e.run_id = r.id) <> 1;"));
        Assert.Equal(0L, await ScalarAsync(connection,
            "SELECT COUNT(*) FROM run_runs r WHERE NOT EXISTS (SELECT 1 FROM run_events e WHERE e.run_id = r.id AND e.type = 'StateChanged' AND json_extract(e.payload_json, '$.to') = r.state);"));
        Assert.Equal(18L, await ScalarAsync(connection, "SELECT COUNT(*) FROM run_events WHERE type = 'StateChanged';")); // null→Received→Analyzing→Coding→Reviewing→Deciding→PullRequestCreated
    }

    // --- Objeto ocupado / libre ----------------------------------------------------------

    [Fact]
    public async Task Event_for_an_object_with_a_non_terminal_run_is_ignored_with_its_shortId()
    {
        await using var factory = CreateReadyFactory();
        using var client = factory.CreateAuthorizedClient();
        var busy = await CreateRunAsync(factory, ObjectKey);

        var eventId = await SubmitAsync(client);
        var info = await WaitForEventAsync(client, eventId, e => e.Status == "Ignored", TimeSpan.FromSeconds(10));

        Assert.Equal($"ya hay una Ejecución en curso para este objeto ({busy.ShortId})", info.Reason);
        Assert.Null(info.RunId);
        await using var connection = await OpenReadOnlyAsync();
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM run_runs WHERE object_key = 'github:o/r#12';"));
        Assert.Equal("Received", await ScalarAsync(connection, "SELECT state FROM run_runs WHERE object_key = 'github:o/r#12';")); // la Ejecución en curso no se toca
    }

    [Fact]
    public async Task Event_for_an_object_whose_previous_run_is_terminal_creates_a_new_run()
    {
        await using var factory = CreateReadyFactory();
        using var client = factory.CreateAuthorizedClient();

        var first = await WaitForEventAsync(client, await SubmitAsync(client), e => e.RunId is not null, TimeSpan.FromSeconds(5));
        await WaitForRunAsync(client, first.RunId!.Value.ToString(), r => r.State == "PullRequestCreated");

        var second = await WaitForEventAsync(client, await SubmitAsync(client), e => e.RunId is not null || e.Status == "Ignored", TimeSpan.FromSeconds(5));

        Assert.Equal("Accepted", second.Status);
        Assert.NotEqual(first.RunId, second.RunId);
    }

    [Fact]
    public async Task Partial_unique_index_rejects_a_second_non_terminal_run_for_the_same_object()
    {
        await using var factory = CreateReadyFactory();
        _ = factory.Server;
        await CreateRunAsync(factory, "github:x/y#1");

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => CreateRunAsync(factory, "github:x/y#1"));

        Assert.True(RunEventLog.IsUniqueViolation(ex));
    }

    // --- Atomicidad y redacción ----------------------------------------------------------

    [Fact]
    public async Task Failure_saving_the_RunEvent_rolls_back_the_state_change()
    {
        await using var factory = CreateReadyFactory();
        _ = factory.Server;
        var run = await CreateRunAsync(factory, "github:a/b#1");
        var connectionString = factory.Services.GetRequiredService<IDatabaseConnection>().ConnectionString;
        var log = factory.Services.GetRequiredService<RunEventLog>();

        var options = new DbContextOptionsBuilder<PipelineDbContext>();
        PipelineDbContext.Configure(options, connectionString);
        options.AddInterceptors(new ConflictingRunEventInterceptor(connectionString));
        await using (var context = new PipelineDbContext(options.Options))
        {
            var tracked = await context.Runs.SingleAsync(r => r.Id == run.Id, Ct);
            var change = tracked.Fail(FailureReason.StageError, "fallo");
            var appended = await log.AddTransitionAsync(context, tracked, change, Ct);
            Assert.Equal(3, appended.Seq);

            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(Ct));
        }

        await using var connection = await OpenReadOnlyAsync();
        var runId = run.Id.ToString().ToUpperInvariant();
        Assert.Equal("Received", await ScalarAsync(connection, $"SELECT state FROM run_runs WHERE id = '{runId}';"));
        Assert.Null(await ScalarAsync(connection, $"SELECT failure_reason FROM run_runs WHERE id = '{runId}';"));
        Assert.Equal("Conflicto", await ScalarAsync(connection, $"SELECT type FROM run_events WHERE run_id = '{runId}' AND seq = 3;"));
    }

    [Fact]
    public async Task Secrets_in_the_payload_are_persisted_masked()
    {
        await using var factory = CreateReadyFactory();
        using var client = factory.CreateAuthorizedClient();
        var run = await CreateRunAsync(factory, "github:s/t#1");
        var contexts = factory.Services.GetRequiredService<IDbContextFactory<PipelineDbContext>>();
        var log = factory.Services.GetRequiredService<RunEventLog>();

        await using (var context = await contexts.CreateDbContextAsync(Ct))
        {
            var tracked = await context.Runs.SingleAsync(r => r.Id == run.Id, Ct);
            var change = tracked.Fail(FailureReason.CloneFailed, $"git clone falló con {GitHubToken} y sk-ant-otraclave0123456789abcdef");
            await log.AddTransitionAsync(context, tracked, change, Ct);
            await context.SaveChangesAsync(Ct);
        }

        await using var connection = await OpenReadOnlyAsync();
        var runId = run.Id.ToString().ToUpperInvariant();
        var payload = (string)(await ScalarAsync(connection, $"SELECT payload_json FROM run_events WHERE run_id = '{runId}' AND seq = 3;"))!;
        var detail = (string)(await ScalarAsync(connection, $"SELECT failure_detail FROM run_runs WHERE id = '{runId}';"))!;
        foreach (var stored in new[] { payload, detail })
        {
            Assert.DoesNotContain(GitHubToken, stored, StringComparison.Ordinal);
            Assert.DoesNotContain("sk-ant-otraclave0123456789abcdef", stored, StringComparison.Ordinal);
            Assert.Contains("ghp…klmn", stored, StringComparison.Ordinal);
        }

        using var json = JsonDocument.Parse(payload);
        Assert.Equal("StateChanged", json.RootElement.GetProperty("type").GetString());
        Assert.Equal("CloneFailed", json.RootElement.GetProperty("failureReason").GetString());
    }

    [Fact]
    public async Task External_sink_rejects_state_transitions_and_unknown_runs()
    {
        await using var factory = CreateReadyFactory();
        _ = factory.Server;
        var sink = factory.Services.GetRequiredService<IRunEventSink>();
        var run = await CreateRunAsync(factory, "github:e/f#1");

        await Assert.ThrowsAsync<ArgumentException>(() => sink.AppendAsync(run.Id, null, new StateChanged(RunState.Received, RunState.Failed), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => sink.AppendAsync(run.Id, null, new RunCreated(Guid.NewGuid(), "x"), Ct));
    }

    [Fact]
    public async Task External_sink_appends_with_the_next_seq_and_redacts_the_payload()
    {
        await using var factory = CreateReadyFactory();
        _ = factory.Server;
        var sink = factory.Services.GetRequiredService<IRunEventSink>();
        var run = await CreateRunAsync(factory, "github:k/l#1"); // seq 1 y 2

        var first = await sink.AppendAsync(run.Id, null, new RepositoryPrepared($"sha-con-{GitHubToken}"), Ct);
        var second = await sink.AppendAsync(run.Id, null, new RepoConfigResolved(ConfigSource.Defaults), Ct);

        Assert.Equal(3, first.Seq);
        Assert.Equal(run.Id, first.RunId);
        Assert.Equal(TimeSpan.Zero, first.At.Offset);
        Assert.Equal(4, second.Seq);

        // Lo devuelto es lo persistido: ya redactado.
        var prepared = Assert.IsType<RepositoryPrepared>(first);
        Assert.DoesNotContain(GitHubToken, prepared.BaseSha, StringComparison.Ordinal);
        Assert.Contains("ghp…klmn", prepared.BaseSha, StringComparison.Ordinal);

        await using var connection = await OpenReadOnlyAsync();
        var runId = run.Id.ToString().ToUpperInvariant();
        var payload = (string)(await ScalarAsync(connection, $"SELECT payload_json FROM run_events WHERE run_id = '{runId}' AND seq = 3;"))!;
        Assert.DoesNotContain(GitHubToken, payload, StringComparison.Ordinal);
        Assert.Equal("RepositoryPrepared", await ScalarAsync(connection, $"SELECT type FROM run_events WHERE run_id = '{runId}' AND seq = 3;"));
        Assert.Equal("RepoConfigResolved", await ScalarAsync(connection, $"SELECT type FROM run_events WHERE run_id = '{runId}' AND seq = 4;"));
        Assert.Equal("defaults", await ScalarAsync(connection, $"SELECT json_extract(payload_json, '$.source') FROM run_events WHERE run_id = '{runId}' AND seq = 4;"));

        var read = await factory.Services.GetRequiredService<IRunReader>().GetEventsAsync(run.Id, 2, Ct);
        Assert.Equal([3L, 4L], read.Select(e => e.Seq));
        Assert.IsType<RepoConfigResolved>(read[1]);
    }

    [Fact]
    public async Task External_sink_retries_with_a_new_seq_when_another_writer_takes_it()
    {
        await using var factory = CreateReadyFactory();
        _ = factory.Server;
        var run = await CreateRunAsync(factory, "github:m/n#1"); // seq 1 y 2
        var connectionString = factory.Services.GetRequiredService<IDatabaseConnection>().ConnectionString;
        var sink = new RunEventSink(new OneShotConflictContextFactory(connectionString), factory.Services.GetRequiredService<RunEventLog>());

        var appended = await sink.AppendAsync(run.Id, null, new RepoConfigResolved(ConfigSource.File), Ct);

        Assert.Equal(4, appended.Seq); // el 3 lo ocupó el otro escritor
        await using var connection = await OpenReadOnlyAsync();
        var runId = run.Id.ToString().ToUpperInvariant();
        Assert.Equal("Conflicto", await ScalarAsync(connection, $"SELECT type FROM run_events WHERE run_id = '{runId}' AND seq = 3;"));
        Assert.Equal("RepoConfigResolved", await ScalarAsync(connection, $"SELECT type FROM run_events WHERE run_id = '{runId}' AND seq = 4;"));
    }

    /// <summary>El primer contexto lleva <see cref="ConflictingRunEventInterceptor"/>; los siguientes, no.</summary>
    private sealed class OneShotConflictContextFactory(string connectionString) : IDbContextFactory<PipelineDbContext>
    {
        private int _created;

        public PipelineDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<PipelineDbContext>();
            PipelineDbContext.Configure(options, connectionString);
            if (Interlocked.Increment(ref _created) == 1)
            {
                options.AddInterceptors(new ConflictingRunEventInterceptor(connectionString));
            }

            return new PipelineDbContext(options.Options);
        }
    }

    // --- Lectura -------------------------------------------------------------------------

    [Fact]
    public async Task AfterSeq_returns_only_later_events_in_order()
    {
        await using var factory = CreateReadyFactory();
        using var client = factory.CreateAuthorizedClient();
        var info = await WaitForEventAsync(client, await SubmitAsync(client), e => e.RunId is not null, TimeSpan.FromSeconds(5));
        var run = await WaitForRunAsync(client, info.RunId!.Value.ToString(), r => r.State == "PullRequestCreated");

        var afterFour = await GetEventsAsync(client, run.ShortId, "?afterSeq=4");
        var all = await GetEventsAsync(client, run.RunId.ToString(), string.Empty);
        var none = await GetEventsAsync(client, run.RunId.ToString(), "?afterSeq=18");

        Assert.Equal(Enumerable.Range(5, 14).Select(i => (long)i), afterFour.Select(e => e.GetProperty("seq").GetInt64()));
        Assert.Equal("PullRequestCreated", afterFour[12].GetProperty("to").GetString());
        Assert.Equal(Enumerable.Range(1, 18).Select(i => (long)i), all.Select(e => e.GetProperty("seq").GetInt64()));
        Assert.Empty(none);

        using var negative = await client.GetAsync($"/api/runs/{run.RunId}/events?afterSeq=-1", Ct);
        Assert.Equal(HttpStatusCode.BadRequest, negative.StatusCode);
    }

    [Fact]
    public async Task ShortId_lookup_matches_the_guid_and_unknown_ids_are_404()
    {
        await using var factory = CreateReadyFactory();
        using var client = factory.CreateAuthorizedClient();
        var run = await CreateRunAsync(factory, "github:g/h#1");

        var byGuid = await GetRunAsync(client, run.Id.ToString());
        var byShortId = await GetRunAsync(client, run.ShortId);
        var byUpperShortId = await GetRunAsync(client, run.ShortId.ToUpperInvariant());

        Assert.Equal(byGuid, byShortId with { });
        Assert.Equal(byGuid.RunId, byUpperShortId.RunId);
        Assert.Equal("Received", byGuid.State);

        foreach (var path in new[]
        {
            "/api/runs/00000000",
            "/api/runs/" + Guid.NewGuid(),
            "/api/runs/zzzzzzzz",
            "/api/runs/abc",
            "/api/runs/" + Guid.NewGuid() + "/events",
            "/api/events/" + Guid.NewGuid(),
        })
        {
            using var response = await client.GetAsync(path, Ct);
            Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{path}: {response.StatusCode}");
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        }
    }

    [Fact]
    public async Task Run_and_event_endpoints_require_the_admin_token()
    {
        await using var factory = CreateReadyFactory();
        using var client = factory.CreateClient();

        foreach (var path in new[] { "/api/runs/abcdef12", "/api/runs/abcdef12/events", "/api/events/" + Guid.NewGuid() })
        {
            using var response = await client.GetAsync(path, Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    // --- Diagnóstico ---------------------------------------------------------------------

    [Fact]
    public async Task Dispatcher_creates_nothing_in_Diagnostic()
    {
        await using var factory = new CodeNetixFactory(_dataDirectory, _webRoot); // sin keys -> Diagnostic
        using var client = factory.CreateAuthorizedClient();
        (await client.GetAsync("/api/health", Ct)).EnsureSuccessStatusCode();

        // En Diagnostic la API no acepta Eventos: se inserta uno aceptado directamente.
        var contexts = factory.Services.GetRequiredService<IDbContextFactory<IngestionDbContext>>();
        await using (var context = await contexts.CreateDbContextAsync(Ct))
        {
            context.Events.Add(new EventRecord
            {
                Id = Guid.CreateVersion7(),
                Source = EventSource.Manual,
                ObjectKey = ObjectKey,
                DedupKey = "test:diagnostic",
                PayloadJson = "{}",
                Status = EventStatus.Accepted,
                ReceivedAt = DateTimeOffset.UtcNow,
            });
            await context.SaveChangesAsync(Ct);
        }

        factory.Services.GetRequiredService<IEventDispatchSignal>().Notify();
        await Task.Delay(DispatchDeadline + TimeSpan.FromMilliseconds(500), Ct);

        await using var connection = await OpenReadOnlyAsync();
        Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM run_runs;"));
        Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM ing_events WHERE dispatched_at IS NOT NULL;"));
    }

    // --- Resiliencia del Dispatcher (revisión 1) ------------------------------------------

    [Fact]
    public async Task Throwing_orchestrator_closes_the_run_as_StageError_and_the_next_event_is_dispatched()
    {
        await using var baseFactory = CreateReadyFactory();
        await using var factory = baseFactory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddSingleton<IRunOrchestrator>(new ThrowingOrchestrator())));
        using var client = Authorized(factory);

        var first = await WaitForEventAsync(client, await SubmitAsync(client), e => e.RunId is not null, TimeSpan.FromSeconds(5));
        var firstRun = await WaitForRunAsync(client, first.RunId!.Value.ToString(), r => r.State == "Failed");

        Assert.Equal("StageError", firstRun.FailureReason);
        Assert.StartsWith(StuckRunRecovery.DetailPrefix + "orquestador roto", firstRun.FailureDetail, StringComparison.Ordinal);
        Assert.DoesNotContain(GitHubToken, firstRun.FailureDetail, StringComparison.Ordinal);

        // El objeto queda libre: el siguiente Evento crea otra Ejecución, que también se cierra.
        var second = await WaitForEventAsync(client, await SubmitAsync(client), e => e.RunId is not null || e.Status == "Ignored", TimeSpan.FromSeconds(5));
        Assert.Equal("Accepted", second.Status);
        Assert.NotEqual(first.RunId, second.RunId);
        var secondRun = await WaitForRunAsync(client, second.RunId!.Value.ToString(), r => r.State == "Failed");
        Assert.Equal("StageError", secondRun.FailureReason);

        using var events = await client.GetAsync($"/api/runs/{firstRun.RunId}/events", Ct);
        using var json = JsonDocument.Parse(await events.Content.ReadAsStringAsync(Ct));
        Assert.Equal([1L, 2L, 3L], json.RootElement.EnumerateArray().Select(e => e.GetProperty("seq").GetInt64()));
    }

    [Fact]
    public async Task Run_left_in_Received_by_a_crash_is_orchestrated_on_the_next_start()
    {
        Guid runId;
        await using (var baseFactory = CreateReadyFactory())
        await using (var crashed = baseFactory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddSingleton<IRunOrchestrator>(new NoopOrchestrator()))))
        {
            using var client = Authorized(crashed);
            var info = await WaitForEventAsync(client, await SubmitAsync(client), e => e.RunId is not null, TimeSpan.FromSeconds(5));
            runId = info.RunId!.Value;
            Assert.Equal("Received", (await GetRunAsync(client, runId.ToString())).State);
        }

        SqliteConnection.ClearAllPools();
        await using var restarted = CreateReadyFactory();
        using var restartedClient = restarted.CreateAuthorizedClient();
        var run = await WaitForRunAsync(restartedClient, runId.ToString(), r => r.State == "PullRequestCreated");

        Assert.Equal("PullRequestCreated", run.State);
    }

    [Fact]
    public async Task Run_already_created_for_an_undispatched_event_is_linked_without_a_second_run()
    {
        await using var factory = CreateReadyFactory();
        using var client = factory.CreateAuthorizedClient();
        var eventId = Guid.CreateVersion7();

        // Corte simulado: la Ejecución existe, pero el Evento sigue pendiente (dispatched_at nulo).
        var run = await CreateRunAsync(factory, "github:c/d#1", eventId);
        await InsertAcceptedEventAsync(factory, eventId, "github:c/d#1");
        factory.Services.GetRequiredService<IEventDispatchSignal>().Notify();

        var info = await WaitForEventAsync(client, eventId, e => e.RunId is not null || e.Status == "Ignored", TimeSpan.FromSeconds(10));

        Assert.Equal("Accepted", info.Status);
        Assert.Equal(run.Id, info.RunId);
        await using var connection = await OpenReadOnlyAsync();
        Assert.Equal(1L, await ScalarAsync(connection, $"SELECT COUNT(*) FROM run_runs WHERE event_id = '{eventId.ToString().ToUpperInvariant()}';"));
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM run_runs WHERE object_key = 'github:c/d#1';"));
    }

    [Fact]
    public async Task Event_that_always_fails_is_ignored_after_five_attempts_and_newer_events_are_dispatched()
    {
        var handler = new PoisonHandler();
        await using var baseFactory = CreateReadyFactory();
        await using var factory = baseFactory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.AddSingleton(new DispatcherOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(50),
                RetryBackoff = TimeSpan.FromMilliseconds(100),
                MaxAttempts = 5,
            });
            s.AddSingleton<IRunDispatchHandler>(sp => handler.Wrap(sp.GetRequiredService<RunDispatchHandler>()));
        }));
        using var client = Authorized(factory);

        var poisonId = await SubmitAsync(client, PoisonUrl);
        var newerId = await SubmitAsync(client);

        // El nuevo no espera a que se agoten los reintentos del envenenado.
        var newer = await WaitForEventAsync(client, newerId, e => e.RunId is not null, TimeSpan.FromSeconds(5));
        Assert.Equal("Accepted", newer.Status);

        var poison = await WaitForEventAsync(client, poisonId, e => e.Status == "Ignored", TimeSpan.FromSeconds(10));
        Assert.StartsWith(Dispatcher.UndispatchableReasonPrefix + "fallo permanente", poison.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(GitHubToken, poison.Reason, StringComparison.Ordinal);
        Assert.Null(poison.RunId);
        Assert.Equal(5, handler.PoisonAttempts);

        await Task.Delay(300, Ct); // ya no se reintenta
        Assert.Equal(5, handler.PoisonAttempts);
        await using var connection = await OpenReadOnlyAsync();
        Assert.Equal(0L, await ScalarAsync(connection, $"SELECT COUNT(*) FROM run_runs WHERE object_key = '{PoisonObjectKey}';"));
    }

    private static async Task InsertAcceptedEventAsync(WebApplicationFactory<Program> factory, Guid eventId, string objectKey)
    {
        var contexts = factory.Services.GetRequiredService<IDbContextFactory<IngestionDbContext>>();
        await using var context = await contexts.CreateDbContextAsync(Ct);
        context.Events.Add(new EventRecord
        {
            Id = eventId,
            Source = EventSource.Manual,
            ObjectKey = objectKey,
            DedupKey = "test:" + eventId,
            PayloadJson = "{}",
            Status = EventStatus.Accepted,
            ReceivedAt = DateTimeOffset.UtcNow,
        });
        await context.SaveChangesAsync(Ct);
    }

    private sealed class ThrowingOrchestrator : IRunOrchestrator
    {
        public Task RunAsync(Guid runId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException($"orquestador roto\ncon {GitHubToken}");
    }

    private sealed class NoopOrchestrator : IRunOrchestrator
    {
        public Task RunAsync(Guid runId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>Falla siempre con los Eventos de <see cref="PoisonObjectKey"/>; el resto, al despacho real.</summary>
    private sealed class PoisonHandler
    {
        private int _poisonAttempts;

        public int PoisonAttempts => Volatile.Read(ref _poisonAttempts);

        public IRunDispatchHandler Wrap(RunDispatchHandler inner) => new Wrapper(this, inner);

        private sealed class Wrapper(PoisonHandler owner, RunDispatchHandler inner) : IRunDispatchHandler
        {
            public Task<DispatchResult> DispatchAsync(PendingEvent pending, CancellationToken cancellationToken)
            {
                if (pending.ObjectKey == PoisonObjectKey)
                {
                    Interlocked.Increment(ref owner._poisonAttempts);
                    throw new InvalidOperationException($"fallo permanente con {GitHubToken}");
                }

                return inner.DispatchAsync(pending, cancellationToken);
            }
        }
    }

    // --- Utilidades ----------------------------------------------------------------------

    /// <summary>
    /// Crea una Ejecución en <c>Received</c> como lo hace el Dispatcher (sin pasar por el orquestador). Antes
    /// espera a que el Dispatcher haya hecho su reanudación de arranque (que cerraría una Ejecución en <c>Received</c>).
    /// </summary>
    private static async Task<PipelineRun> CreateRunAsync(WebApplicationFactory<Program> factory, string objectKey, Guid? eventId = null)
    {
        await WarmUpDispatcherAsync(factory);
        var contexts = factory.Services.GetRequiredService<IDbContextFactory<PipelineDbContext>>();
        var log = factory.Services.GetRequiredService<RunEventLog>();
        await using var context = await contexts.CreateDbContextAsync(Ct);
        var (run, created, initial) = PipelineRun.Create(eventId ?? Guid.CreateVersion7(), objectKey, DateTimeOffset.UtcNow);
        context.Runs.Add(run);
        await log.AddAsync(context, run.Id, created, Ct);
        await log.AddAsync(context, run.Id, initial, Ct);
        await context.SaveChangesAsync(Ct);
        return run;
    }

    private static readonly ConditionalWeakTable<WebApplicationFactory<Program>, object> WarmedUp = [];

    /// <summary>
    /// La reanudación de arranque precede al bucle de despacho: cuando un Evento ya tiene Ejecución, ha terminado.
    /// </summary>
    private static async Task WarmUpDispatcherAsync(WebApplicationFactory<Program> factory)
    {
        if (WarmedUp.TryGetValue(factory, out _))
        {
            return;
        }

        using var client = Authorized(factory);
        var eventId = await SubmitAsync(client, WarmUpUrl);
        await WaitForEventAsync(client, eventId, e => e.RunId is not null, TimeSpan.FromSeconds(5));
        WarmedUp.AddOrUpdate(factory, new object());
    }

    private static HttpClient Authorized(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", CodeNetixFactory.TestAccessToken);
        return client;
    }

    private static async Task<Guid> SubmitAsync(HttpClient client, string url = IssueUrl)
    {
        using var response = await client.PostAsJsonAsync("/api/events/manual", new { url, requestId = Guid.NewGuid().ToString() }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<EventBody>(Ct);
        Assert.Equal("Accepted", body!.Status);
        return body.EventId;
    }

    private static async Task<EventBody> WaitForEventAsync(HttpClient client, Guid eventId, Func<EventBody, bool> done, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            using var response = await client.GetAsync($"/api/events/{eventId}", Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = (await response.Content.ReadFromJsonAsync<EventBody>(Ct))!;
            Assert.Equal(eventId, body.EventId);
            if (done(body))
            {
                return body;
            }

            Assert.True(stopwatch.Elapsed < timeout, $"El Evento {eventId} no llegó al estado esperado en {timeout} (status={body.Status}).");
            await Task.Delay(50, Ct);
        }
    }

    private static async Task<RunBody> WaitForRunAsync(HttpClient client, string id, Func<RunBody, bool> done)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            var run = await GetRunAsync(client, id);
            if (done(run) && await SandboxSettledAsync(client, id))
            {
                return run;
            }

            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15), $"La Ejecución {id} sigue en {run.State}.");
            await Task.Delay(50, Ct);
        }
    }

    /// <summary>Si la Ejecución creó un Sandbox, ¿se ha registrado ya su liberación (el último paso del orquestador)?</summary>
    private static async Task<bool> SandboxSettledAsync(HttpClient client, string id)
    {
        var types = (await GetEventsAsync(client, id, string.Empty)).Select(e => e.GetProperty("type").GetString()).ToList();
        return !types.Contains("SandboxCreated") || types.Contains("SandboxReleased");
    }

    private static async Task<RunBody> GetRunAsync(HttpClient client, string id)
    {
        using var response = await client.GetAsync($"/api/runs/{id}", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<RunBody>(Ct))!;
    }

    private static async Task<List<JsonElement>> GetEventsAsync(HttpClient client, string id, string query)
    {
        using var response = await client.GetAsync($"/api/runs/{id}/events{query}", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(JsonValueKind.Array, json.RootElement.ValueKind);
        return [.. json.RootElement.EnumerateArray().Select(e => e.Clone())];
    }

    private async Task<SqliteConnection> OpenReadOnlyAsync()
    {
        var connection = new SqliteConnection($"Data Source={DatabasePath};Mode=ReadOnly");
        await connection.OpenAsync(Ct);
        return connection;
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(Ct);
        return value is DBNull ? null : value;
    }

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

    private sealed record EventBody(Guid EventId, string? Status, string? Reason, Guid? RunId);

    private sealed record RunBody(
        Guid RunId,
        string ShortId,
        string State,
        string? FailureReason,
        string? FailureDetail,
        Guid EventId,
        string ObjectKey,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);

    /// <summary>Otro escritor ocupa el <c>seq</c> que se va a guardar: el <c>INSERT</c> del <c>RunEvent</c> falla.</summary>
    private sealed class ConflictingRunEventInterceptor(string connectionString) : SaveChangesInterceptor
    {
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var pending = eventData.Context!.ChangeTracker.Entries<RunEventRecord>().Single(e => e.State == EntityState.Added).Entity;
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO run_events (run_id, seq, type, payload_json, at) VALUES ($runId, $seq, 'Conflicto', '{}', 0);";
            command.Parameters.AddWithValue("$runId", pending.RunId.ToString().ToUpperInvariant());
            command.Parameters.AddWithValue("$seq", pending.Seq);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return result;
        }
    }
}
