using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using CodeNetix.Contracts;
using CodeNetix.Host.Features.CancelRun;
using CodeNetix.Host.Features.GetRun;
using CodeNetix.Host.Hubs;
using CodeNetix.Pipeline.Data;
using CodeNetix.Pipeline.Domain;
using CodeNetix.Pipeline.Events;
using CodeNetix.Pipeline.Features.CancelRun;
using CodeNetix.Pipeline.Features.RunPipeline;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CodeNetix.Host.Tests;

/// <summary>
/// Pruebas de integración de la Historia 4.4 (FR-23, AD-2):
/// cancelación segura de Ejecuciones activas en <c>Coding</c> (Sandbox liberado en &lt; 10 s, estado <c>Cancelled</c>
/// en <c>/hubs/runs</c> y <c>GET /api/runs/{id}</c>, sin PR, con aviso <c>RunCancelled</c>), cancelación en
/// <c>Received</c> por <c>shortId</c>, rechazo <c>409 Conflict</c> en <c>Deciding</c> y estados terminales,
/// y <c>404 Not Found</c> / <c>401 Unauthorized</c>.
/// </summary>
public sealed class CancelRunTests : IDisposable
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "codenetix-cancel-" + Guid.NewGuid().ToString("N"));
    private readonly string _dataDirectory;
    private readonly string _webRoot;

    public CancelRunTests()
    {
        _dataDirectory = Path.Combine(_root, "data");
        _webRoot = Path.Combine(_root, "wwwroot");
        Directory.CreateDirectory(_webRoot);
        File.WriteAllText(Path.Combine(_webRoot, "index.html"), "<!doctype html><html><body>ok</body></html>");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Cancelling_active_run_in_Coding_releases_sandbox_within_10s_reaches_Cancelled_notifies_RunCancelled_and_never_creates_PR()
    {
        var blockingCoder = new BlockingCodingAgent();
        var prPublisher = new RecordingPullRequestPublisher();
        var notifier = new RecordingIssueNotifier();

        await using var baseFactory = new CodeNetixFactory(_dataDirectory, _webRoot, CodeNetixFactory.CompleteSettings);
        await using var factory = baseFactory.WithWebHostBuilder(b =>
            b.ConfigureTestServices(services =>
            {
                services.AddSingleton<ICodingAgent>(blockingCoder);
                services.AddSingleton<IPullRequestPublisher>(prPublisher);
                services.AddSingleton<IIssueNotifier>(notifier);
            }));

        using var client = CreateAuthorizedClient(factory);

        // Enviar evento manual y esperar a que el Programador entre en Coding.
        var runId = await SubmitManualIssueAndWaitForRunAsync(client, "https://github.com/o/r/issues/4401");
        var codingRunId = await blockingCoder.EnteredCoding.Task.WaitAsync(TimeSpan.FromSeconds(15), Ct);
        Assert.Equal(runId, codingRunId);

        // Conectar SignalR a /hubs/runs y suscribirse a la Ejecución activa.
        var hubEvents = Channel.CreateUnbounded<RunEvent>();
        await using var connection = CreateHubConnection(factory, CodeNetixFactory.TestAccessToken);
        connection.On<JsonElement>(RunsHub.EventMethod, payload =>
        {
            var ev = JsonSerializer.Deserialize<RunEvent>(payload.GetRawText(), WebJson);
            if (ev is not null && ev.RunId == runId)
            {
                hubEvents.Writer.TryWrite(ev);
            }
        });
        await connection.StartAsync(Ct);
        await connection.InvokeAsync("SubscribeRun", runId, Ct);

        // Solicitar cancelación mientras está en Coding y medir el tiempo hasta liberar el Sandbox.
        var sw = Stopwatch.StartNew();
        using var cancelResponse = await client.PostAsync($"/api/runs/{runId}/cancel", content: null, Ct);
        Assert.Equal(HttpStatusCode.Accepted, cancelResponse.StatusCode);

        var cancelPayload = await cancelResponse.Content.ReadFromJsonAsync<CancelRunResponse>(WebJson, Ct);
        Assert.NotNull(cancelPayload);
        Assert.Equal(runId, cancelPayload.RunId);
        Assert.True(cancelPayload.CancellationRequested);

        // Esperar a que la Ejecución alcance Cancelled y el Sandbox sea liberado.
        var finalRun = await WaitForRunStateAsync(client, runId, RunState.Cancelled, TimeSpan.FromSeconds(10));
        var session = Assert.Single(baseFactory.Sandbox.Sessions, s => s.RunId == runId);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (session.Disposals == 0 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(25, Ct);
        }

        sw.Stop();
        Assert.True(session.Disposals >= 1, "El Sandbox debe haberse liberado al cancelar.");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"El Sandbox tardó {sw.Elapsed.TotalSeconds:F2}s en liberarse (límite 10s).");

        // Verificar que /hubs/runs recibió el estado final Cancelled.
        var receivedCancelled = await WaitForHubEventAsync(
            hubEvents.Reader,
            ev => ev is StateChanged { To: RunState.Cancelled },
            TimeSpan.FromSeconds(10));
        Assert.NotNull(receivedCancelled);

        // Verificar que GET /api/runs/{id} refleja Cancelled, FailureReason nulo, SandboxReleased y nunca se creó PR.
        var refreshed = await client.GetFromJsonAsync<RunResponse>($"/api/runs/{runId}", WebJson, Ct);
        Assert.NotNull(refreshed);
        Assert.Equal(RunState.Cancelled, refreshed.State);
        Assert.Null(refreshed.FailureReason);
        Assert.Contains(refreshed.Events, e => e is SandboxReleased);
        Assert.Contains(refreshed.Events, e => e is StateChanged { To: RunState.Cancelled });
        Assert.Null(refreshed.PullRequest);
        Assert.Equal(0, prPublisher.PublishCalls);

        // Esperar a que IIssueNotifier reciba NotificationKind.RunCancelled.
        var notification = await notifier.WaitForKindAsync(runId, NotificationKind.RunCancelled, TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(NotificationKind.RunCancelled, notification.Kind);
        Assert.Equal(4401, notification.Number);

        Assert.Equal(RunState.Cancelled, finalRun.State);
        await connection.StopAsync(Ct);
    }

    [Fact]
    public async Task Cancelling_pending_run_in_Received_by_shortId_transitions_immediately_and_notifies_RunCancelled()
    {
        var notifier = new RecordingIssueNotifier();

        await using var baseFactory = new CodeNetixFactory(_dataDirectory, _webRoot, CodeNetixFactory.CompleteSettings);
        await using var factory = baseFactory.WithWebHostBuilder(b =>
            b.ConfigureTestServices(services => services.AddSingleton<IIssueNotifier>(notifier)));

        using var client = CreateAuthorizedClient(factory);

        // Sembrar una Ejecución en Received pendiente directamente en BD.
        var (runId, shortId) = await SeedRunAsync(factory.Services, "github:o/r#4402", RunState.Received);

        using var response = await client.PostAsync($"/api/runs/{shortId}/cancel", content: null, Ct);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<CancelRunResponse>(WebJson, Ct);
        Assert.NotNull(body);
        Assert.Equal(runId, body.RunId);
        Assert.Equal(shortId, body.ShortId);
        Assert.Equal(RunState.Cancelled, body.State);
        Assert.True(body.CancellationRequested);

        var detail = await client.GetFromJsonAsync<RunResponse>($"/api/runs/{runId}", WebJson, Ct);
        Assert.NotNull(detail);
        Assert.Equal(RunState.Cancelled, detail.State);
        Assert.Contains(detail.Events, e => e is StateChanged { From: RunState.Received, To: RunState.Cancelled });

        var notification = await notifier.WaitForKindAsync(runId, NotificationKind.RunCancelled, TimeSpan.FromSeconds(5), Ct);
        Assert.Equal(4402, notification.Number);

        // Si el orquestador toma luego la Ejecución ya cancelada, debe omitirla de forma idempotente.
        var eventsBeforeOrchestrator = (await client.GetFromJsonAsync<RunResponse>($"/api/runs/{runId}", WebJson, Ct))!.Events.Count;
        var orchestrator = factory.Services.GetRequiredService<IRunOrchestrator>();
        await orchestrator.RunAsync(runId, Ct);

        Assert.Empty(baseFactory.Sandbox.Sessions);
        var afterOrchestrator = await client.GetFromJsonAsync<RunResponse>($"/api/runs/{runId}", WebJson, Ct);
        Assert.NotNull(afterOrchestrator);
        Assert.Equal(RunState.Cancelled, afterOrchestrator.State);
        Assert.Equal(eventsBeforeOrchestrator, afterOrchestrator.Events.Count);
    }

    [Fact]
    public async Task Cancelling_run_in_Deciding_returns_409_Conflict_and_publishes_PR_normally()
    {
        var reviewer = new ApprovingReviewerAgent();
        var prPublisher = new BlockingPullRequestPublisher();

        await using var baseFactory = new CodeNetixFactory(_dataDirectory, _webRoot, CodeNetixFactory.CompleteSettings);
        await using var factory = baseFactory.WithWebHostBuilder(b =>
            b.ConfigureTestServices(services =>
            {
                services.AddSingleton<IReviewerAgent>(reviewer);
                services.AddSingleton<IPullRequestPublisher>(prPublisher);
            }));

        using var client = CreateAuthorizedClient(factory);

        var runId = await SubmitManualIssueAndWaitForRunAsync(client, "https://github.com/o/r/issues/4403");
        var decidingRunId = await prPublisher.EnteredPublish.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        Assert.Equal(runId, decidingRunId);

        // Intentar cancelar mientras está en Deciding debe devolver 409 Conflict (ProblemDetails).
        using var conflictResponse = await client.PostAsync($"/api/runs/{runId}/cancel", content: null, Ct);
        Assert.Equal(HttpStatusCode.Conflict, conflictResponse.StatusCode);
        Assert.Equal("application/problem+json", conflictResponse.Content.Headers.ContentType?.MediaType);

        // Liberar la publicación del PR y comprobar que completa PullRequestCreated normalmente.
        prPublisher.ReleasePublish.TrySetResult();
        var completed = await WaitForRunStateAsync(client, runId, RunState.PullRequestCreated, TimeSpan.FromSeconds(15));
        Assert.Equal(RunState.PullRequestCreated, completed.State);
        Assert.Equal(1, prPublisher.PublishCalls);
        Assert.NotNull(completed.PullRequest);
        Assert.Equal(4403, completed.PullRequest.Number);
    }

    [Theory]
    [InlineData(RunState.PullRequestCreated)]
    [InlineData(RunState.Failed)]
    [InlineData(RunState.Cancelled)]
    public async Task Cancelling_run_in_terminal_state_returns_409_Conflict(RunState terminalState)
    {
        await using var factory = new CodeNetixFactory(_dataDirectory, _webRoot, CodeNetixFactory.CompleteSettings);
        using var client = factory.CreateAuthorizedClient();

        var (runId, _) = await SeedRunAsync(factory.Services, $"github:o/r#{4410 + (int)terminalState}", terminalState);
        var before = await client.GetFromJsonAsync<RunResponse>($"/api/runs/{runId}", WebJson, Ct);
        Assert.NotNull(before);

        using var response = await client.PostAsync($"/api/runs/{runId}/cancel", content: null, Ct);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var after = await client.GetFromJsonAsync<RunResponse>($"/api/runs/{runId}", WebJson, Ct);
        Assert.NotNull(after);
        Assert.Equal(before.State, after.State);
        Assert.Equal(before.Events.Count, after.Events.Count);
    }

    [Fact]
    public async Task Cancelling_active_run_in_Reviewing_aborts_before_Deciding_releases_sandbox_and_never_creates_PR()
    {
        var blockingReviewer = new BlockingReviewerAgent();
        var prPublisher = new RecordingPullRequestPublisher();
        var notifier = new RecordingIssueNotifier();

        await using var baseFactory = new CodeNetixFactory(_dataDirectory, _webRoot, CodeNetixFactory.CompleteSettings);
        await using var factory = baseFactory.WithWebHostBuilder(b =>
            b.ConfigureTestServices(services =>
            {
                services.AddSingleton<IReviewerAgent>(blockingReviewer);
                services.AddSingleton<IPullRequestPublisher>(prPublisher);
                services.AddSingleton<IIssueNotifier>(notifier);
            }));

        using var client = CreateAuthorizedClient(factory);

        var runId = await SubmitManualIssueAndWaitForRunAsync(client, "https://github.com/o/r/issues/4420");
        var reviewingRunId = await blockingReviewer.EnteredReviewing.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        Assert.Equal(runId, reviewingRunId);

        using var cancelResponse = await client.PostAsync($"/api/runs/{runId}/cancel", content: null, Ct);
        Assert.Equal(HttpStatusCode.Accepted, cancelResponse.StatusCode);

        var finalRun = await WaitForRunStateAsync(client, runId, RunState.Cancelled, TimeSpan.FromSeconds(10));
        Assert.Equal(RunState.Cancelled, finalRun.State);
        Assert.Null(finalRun.FailureReason);
        Assert.Equal(0, prPublisher.PublishCalls);
        Assert.Null(finalRun.PullRequest);

        var session = Assert.Single(baseFactory.Sandbox.Sessions, s => s.RunId == runId);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (session.Disposals == 0 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(25, Ct);
        }

        Assert.True(session.Disposals >= 1, "El Sandbox debe liberarse al cancelar en Reviewing.");
        var notification = await notifier.WaitForKindAsync(runId, NotificationKind.RunCancelled, TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(4420, notification.Number);
    }

    [Fact]
    public async Task Host_shutdown_cancels_RunOrchestrator_without_marking_Cancelled_or_sending_RunCancelled_and_releases_sandbox()
    {
        var blockingCoder = new BlockingCodingAgent();
        var notifier = new RecordingIssueNotifier();

        await using var baseFactory = new CodeNetixFactory(_dataDirectory, _webRoot, CodeNetixFactory.CompleteSettings);
        await using var factory = baseFactory.WithWebHostBuilder(b =>
            b.ConfigureTestServices(services =>
            {
                services.AddSingleton<ICodingAgent>(blockingCoder);
                services.AddSingleton<IIssueNotifier>(notifier);
            }));

        using var client = CreateAuthorizedClient(factory);

        // Sembrar una Ejecución en Received e invocar el orquestador directamente con un stoppingToken propio.
        var (runId, _) = await SeedRunAsync(factory.Services, "github:o/r#4421", RunState.Received);
        var orchestrator = factory.Services.GetRequiredService<IRunOrchestrator>();

        using var shutdownCts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var runTask = orchestrator.RunAsync(runId, shutdownCts.Token);

        var codingRunId = await blockingCoder.EnteredCoding.Task.WaitAsync(TimeSpan.FromSeconds(15), Ct);
        Assert.Equal(runId, codingRunId);

        // Simular apagado del Host SIN RequestCancelAsync.
        shutdownCts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);

        var detail = await client.GetFromJsonAsync<RunResponse>($"/api/runs/{runId}", WebJson, Ct);
        Assert.NotNull(detail);
        Assert.NotEqual(RunState.Cancelled, detail.State);
        Assert.DoesNotContain(detail.Events, e => e is StateChanged { To: RunState.Cancelled });

        var session = Assert.Single(baseFactory.Sandbox.Sessions, s => s.RunId == runId);
        Assert.True(session.Disposals >= 1, "El Sandbox debe liberarse incluso ante apagado del Host.");
        Assert.False(notifier.HasKind(runId, NotificationKind.RunCancelled), "El apagado del Host no debe publicar RunCancelled.");
    }

    [Fact]
    public void Registry_TryRequestCancel_before_TryEnterDeciding_blocks_entering_Deciding()
    {
        var registry = new RunCancellationRegistry();
        var runId = Guid.NewGuid();
        using var handle = registry.BeginRun(runId, CancellationToken.None);

        var cancelled = registry.TryRequestCancel(runId, out var inDeciding);
        Assert.True(cancelled);
        Assert.False(inDeciding);
        Assert.True(handle.Token.IsCancellationRequested);
        Assert.True(registry.IsUserCancelRequested(runId));

        Assert.False(registry.TryEnterDeciding(runId));
    }

    [Fact]
    public void Registry_TryEnterDeciding_before_TryRequestCancel_reports_inDeciding_without_cancelling_token()
    {
        var registry = new RunCancellationRegistry();
        var runId = Guid.NewGuid();
        using var handle = registry.BeginRun(runId, CancellationToken.None);

        Assert.True(registry.TryEnterDeciding(runId));

        var cancelled = registry.TryRequestCancel(runId, out var inDeciding);
        Assert.False(cancelled);
        Assert.True(inDeciding);
        Assert.False(handle.Token.IsCancellationRequested);
        Assert.False(registry.IsUserCancelRequested(runId));
    }

    [Fact]
    public async Task Registry_parallel_race_between_TryRequestCancel_and_TryEnterDeciding_is_mutually_exclusive()
    {
        var registry = new RunCancellationRegistry();

        for (var i = 0; i < 100; i++)
        {
            var runId = Guid.NewGuid();
            using var handle = registry.BeginRun(runId, CancellationToken.None);
            using var barrier = new Barrier(2);

            var cancelTask = Task.Run(() =>
            {
                barrier.SignalAndWait(Ct);
                var ok = registry.TryRequestCancel(runId, out var inDeciding);
                return (ok, inDeciding);
            }, Ct);

            var decidingTask = Task.Run(() =>
            {
                barrier.SignalAndWait(Ct);
                return registry.TryEnterDeciding(runId);
            }, Ct);

            var (cancelAccepted, inDeciding) = await cancelTask;
            var enteredDeciding = await decidingTask;

            // Exclusión mutua estricta: nunca ambos true, y siempre exactamente uno gana.
            Assert.NotEqual(cancelAccepted, enteredDeciding);
            if (enteredDeciding)
            {
                Assert.True(inDeciding);
                Assert.False(handle.Token.IsCancellationRequested);
            }
            else
            {
                Assert.False(inDeciding);
                Assert.True(handle.Token.IsCancellationRequested);
            }
        }
    }

    [Fact]
    public async Task Cancelling_nonexistent_run_returns_404_and_without_token_returns_401()
    {
        await using var factory = new CodeNetixFactory(_dataDirectory, _webRoot, CodeNetixFactory.CompleteSettings);
        using var authorized = factory.CreateAuthorizedClient();
        using var anonymous = factory.CreateClient();

        using var unauthenticated = await anonymous.PostAsync("/api/runs/deadbeef/cancel", content: null, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);

        using var notFoundText = await authorized.PostAsync("/api/runs/no-existe/cancel", content: null, Ct);
        Assert.Equal(HttpStatusCode.NotFound, notFoundText.StatusCode);
        Assert.Equal("application/problem+json", notFoundText.Content.Headers.ContentType?.MediaType);

        using var notFoundShortId = await authorized.PostAsync("/api/runs/deadbeef/cancel", content: null, Ct);
        Assert.Equal(HttpStatusCode.NotFound, notFoundShortId.StatusCode);
        Assert.Equal("application/problem+json", notFoundShortId.Content.Headers.ContentType?.MediaType);
    }

    private static HttpClient CreateAuthorizedClient(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", CodeNetixFactory.TestAccessToken);
        return client;
    }

    private static HubConnection CreateHubConnection(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory, string accessToken) =>
        new HubConnectionBuilder()
            .WithUrl(
                new Uri(factory.Server.BaseAddress, $"{RunsHub.Route}?access_token={Uri.EscapeDataString(accessToken)}"),
                options =>
                {
                    options.Transports = HttpTransportType.LongPolling;
                    options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                })
            .Build();

    private static async Task<Guid> SubmitManualIssueAndWaitForRunAsync(HttpClient client, string issueUrl)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/events/manual",
            new { url = issueUrl, requestId = Guid.NewGuid().ToString("N") },
            Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(WebJson, Ct);
        var eventId = body.GetProperty("eventId").GetGuid();

        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var runs = await client.GetFromJsonAsync<List<RunSummary>>("/api/runs", WebJson, Ct);
            var match = runs?.FirstOrDefault(r => r.EventId == eventId);
            if (match is not null)
            {
                return match.RunId;
            }

            await Task.Delay(50, Ct);
        }

        throw new TimeoutException($"No se creó la Ejecución para el evento {eventId} a tiempo.");
    }

    private static async Task<RunResponse> WaitForRunStateAsync(HttpClient client, Guid runId, RunState expectedState, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        RunResponse? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            last = await client.GetFromJsonAsync<RunResponse>($"/api/runs/{runId}", WebJson, Ct);
            if (last is not null && last.State == expectedState)
            {
                return last;
            }

            await Task.Delay(50, Ct);
        }

        throw new TimeoutException($"La Ejecución {runId} no alcanzó {expectedState} a tiempo (último estado: {last?.State}).");
    }

    private static async Task<RunEvent> WaitForHubEventAsync(
        ChannelReader<RunEvent> reader,
        Func<RunEvent, bool> predicate,
        TimeSpan timeout)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeoutCts.CancelAfter(timeout);

        while (await reader.WaitToReadAsync(timeoutCts.Token))
        {
            while (reader.TryRead(out var item))
            {
                if (predicate(item))
                {
                    return item;
                }
            }
        }

        throw new TimeoutException("No se recibió el evento esperado por /hubs/runs.");
    }

    private static async Task<(Guid RunId, string ShortId)> SeedRunAsync(IServiceProvider services, string objectKey, RunState targetState)
    {
        var factory = services.GetRequiredService<IDbContextFactory<PipelineDbContext>>();
        var log = services.GetRequiredService<RunEventLog>();

        await using var context = await factory.CreateDbContextAsync(Ct);
        var (run, created, initial) = PipelineRun.Create(Guid.NewGuid(), objectKey, DateTimeOffset.UtcNow);
        context.Runs.Add(run);
        await log.AddAsync(context, run.Id, created, Ct);
        await log.AddAsync(context, run.Id, initial, Ct);

        switch (targetState)
        {
            case RunState.Received:
                break;

            case RunState.Cancelled:
                await log.AddTransitionAsync(context, run, run.Cancel(), Ct);
                break;

            case RunState.Failed:
                await log.AddTransitionAsync(context, run, run.Fail(FailureReason.StageError, "Fallo de prueba"), Ct);
                break;

            case RunState.PullRequestCreated:
                await log.AddTransitionAsync(context, run, run.StartAnalysis(), Ct);
                await log.AddTransitionAsync(context, run, run.StartCoding(), Ct);
                await log.AddTransitionAsync(context, run, run.StartReview(), Ct);
                await log.AddTransitionAsync(context, run, run.StartDeciding(), Ct);
                await log.AddTransitionAsync(context, run, run.CompletePullRequest(), Ct);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(targetState), targetState, "Estado de semilla no soportado.");
        }

        await log.SaveAndPublishAsync(context, Ct);
        return (run.Id, run.ShortId);
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

    private sealed class BlockingCodingAgent : ICodingAgent
    {
        public TaskCompletionSource<Guid> EnteredCoding { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<CodingResult> RunAsync(CodingInput input, CancellationToken cancellationToken)
        {
            EnteredCoding.TrySetResult(input.RunId);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new CodingResult.Completed("ok", "fake-coder");
        }
    }

    private sealed class BlockingReviewerAgent : IReviewerAgent
    {
        public TaskCompletionSource<Guid> EnteredReviewing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ReviewResult> ReviewAsync(ReviewInput input, CancellationToken cancellationToken)
        {
            EnteredReviewing.TrySetResult(input.RunId);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            var verdict = new Verdict(VerdictDecision.Approved, "Aprobado en prueba", []);
            return new ReviewResult(verdict, Attempts: 1, Model: "fake-reviewer");
        }
    }

    private sealed class ApprovingReviewerAgent : IReviewerAgent
    {
        public Task<ReviewResult> ReviewAsync(ReviewInput input, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var verdict = new Verdict(VerdictDecision.Approved, "Aprobado en prueba", []);
            return Task.FromResult(new ReviewResult(verdict, Attempts: 1, Model: "fake-reviewer"));
        }
    }

    private sealed class RecordingPullRequestPublisher : IPullRequestPublisher
    {
        private int _publishCalls;

        public int PublishCalls => Volatile.Read(ref _publishCalls);

        public Task<PullRequestInfo> PublishAsync(PullRequestRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _publishCalls);
            throw new InvalidOperationException("No debería haberse invocado IPullRequestPublisher tras cancelar.");
        }

        public Task<PullRequestInfo?> FindByRunAsync(Guid runId, CancellationToken cancellationToken) =>
            Task.FromResult<PullRequestInfo?>(null);
    }

    private sealed class BlockingPullRequestPublisher : IPullRequestPublisher
    {
        private readonly ConcurrentDictionary<Guid, PullRequestInfo> _published = new();
        private int _publishCalls;

        public int PublishCalls => Volatile.Read(ref _publishCalls);

        public TaskCompletionSource<Guid> EnteredPublish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleasePublish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<PullRequestInfo> PublishAsync(PullRequestRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _publishCalls);
            EnteredPublish.TrySetResult(request.RunId);
            await ReleasePublish.Task.WaitAsync(cancellationToken);

            var info = new PullRequestInfo(
                Number: 4403,
                Url: $"https://github.com/{request.Owner}/{request.Repo}/pull/4403",
                BranchName: $"codenetix/{request.RunId}",
                CommitSha: "abcdef0123456789abcdef0123456789abcdef01",
                Title: request.Title,
                CreatedAt: DateTimeOffset.UtcNow);
            _published[request.RunId] = info;
            return info;
        }

        public Task<PullRequestInfo?> FindByRunAsync(Guid runId, CancellationToken cancellationToken) =>
            Task.FromResult(_published.TryGetValue(runId, out var pr) ? pr : null);
    }

    private sealed record RecordedNotification(Guid RunId, string Owner, string Repo, long Number, NotificationKind Kind, string Body);

    private sealed class RecordingIssueNotifier : IIssueNotifier
    {
        private readonly ConcurrentQueue<RecordedNotification> _notifications = new();

        public Task<IssueNotification> NotifyAsync(
            Guid runId,
            string owner,
            string repo,
            long number,
            NotificationKind kind,
            string body,
            CancellationToken cancellationToken)
        {
            _notifications.Enqueue(new RecordedNotification(runId, owner, repo, number, kind, body));
            return Task.FromResult(IssueNotification.Posted($"https://github.com/{owner}/{repo}/issues/{number}#issuecomment-1"));
        }

        public bool HasKind(Guid runId, NotificationKind kind) =>
            _notifications.Any(n => n.RunId == runId && n.Kind == kind);

        public async Task<RecordedNotification> WaitForKindAsync(Guid runId, NotificationKind kind, TimeSpan timeout, CancellationToken ct)
        {
            var deadline = DateTimeOffset.UtcNow + timeout;
            while (DateTimeOffset.UtcNow < deadline)
            {
                var match = _notifications.FirstOrDefault(n => n.RunId == runId && n.Kind == kind);
                if (match is not null)
                {
                    return match;
                }

                await Task.Delay(25, ct);
            }

            throw new TimeoutException($"No se recibió la notificación {kind} para la Ejecución {runId}.");
        }
    }
}
