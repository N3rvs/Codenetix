using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using CodeNetix.Contracts;
using CodeNetix.Host.Hubs;
using CodeNetix.Host.Secrets;
using CodeNetix.Pipeline.Data;
using CodeNetix.Pipeline.Domain;
using CodeNetix.Pipeline.Events;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace CodeNetix.Host.Tests;

/// <summary>
/// Matriz I/O de la Historia 4.1: autenticación de <c>/hubs/runs</c> (<c>?access_token=</c> solo en <c>/hubs/**</c>),
/// redacción completa del query token en logs reales, invariante post-commit de <see cref="RunEventLog.SaveAndPublishAsync"/>,
/// entrega en vivo de <see cref="RunEvent"/> y aislamiento de grupos, reanudación sin pérdida con <c>afterSeq</c>,
/// fallback 404 en <c>/hubs/*</c> y <c>/openapi/*</c>, y listado <c>GET /api/runs</c>.
/// </summary>
public sealed class LivePanelHubTests : IDisposable
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "codenetix-hub-" + Guid.NewGuid().ToString("N"));
    private readonly string _dataDirectory;
    private readonly string _webRoot;

    public LivePanelHubTests()
    {
        _dataDirectory = Path.Combine(_root, "data");
        _webRoot = Path.Combine(_root, "wwwroot");
        Directory.CreateDirectory(_webRoot);
        File.WriteAllText(Path.Combine(_webRoot, "index.html"), "<!doctype html><html><body>ok</body></html>");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Hub_without_token_returns_401()
    {
        await using var factory = new CodeNetixFactory(_dataDirectory, _webRoot, CodeNetixFactory.CompleteSettings);
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/hubs/runs/negotiate?negotiateVersion=1", content: null, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(response.Headers.WwwAuthenticate, h => h.Scheme == "Bearer");
    }

    [Fact]
    public async Task Query_access_token_is_accepted_on_hubs_rejected_on_api_and_never_appears_in_host_logs()
    {
        const string CustomNonCnxToken = "custom-non-cnx-secret-token-9999";
        var settings = new Dictionary<string, string?>(CodeNetixFactory.CompleteSettings)
        {
            [CodeNetixFactory.AccessTokenKey] = CustomNonCnxToken,
        };

        var capturedLogs = new ConcurrentQueue<string>();
        await using var baseFactory = new CodeNetixFactory(_dataDirectory, _webRoot, settings);
        await using var factory = baseFactory.WithWebHostBuilder(b =>
        {
            b.ConfigureLogging(logging =>
            {
                logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Information);
            });
            b.ConfigureTestServices(services =>
            {
                services.AddSingleton<ILoggerProvider>(sp =>
                    new RedactedCapturingLoggerProvider(
                        new RedactingConsoleFormatter(
                            sp.GetRequiredService<ISecretRedactor>(),
                            sp.GetRequiredService<IOptionsMonitor<ConsoleFormatterOptions>>()),
                        capturedLogs));
            });
        });

        using var client = factory.CreateClient();

        // /api/runs NO acepta access_token en query string (AD-17).
        using var apiResponse = await client.GetAsync($"/api/runs?access_token={Uri.EscapeDataString(CustomNonCnxToken)}", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, apiResponse.StatusCode);

        // /hubs/runs/negotiate SÍ acepta access_token en query string.
        using var hubResponse = await client.PostAsync(
            $"/hubs/runs/negotiate?negotiateVersion=1&access_token={Uri.EscapeDataString(CustomNonCnxToken)}",
            content: null,
            Ct);
        Assert.Equal(HttpStatusCode.OK, hubResponse.StatusCode);

        // Un access_token inválido en /hubs sigue devolviendo 401 y no incluye el token en la respuesta ni en logs.
        const string WrongCustomToken = "totally-unknown-query-secret-8888";
        using var invalidHubResponse = await client.PostAsync(
            $"/hubs/runs/negotiate?negotiateVersion=1&access_token={Uri.EscapeDataString(WrongCustomToken)}",
            content: null,
            Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, invalidHubResponse.StatusCode);
        var errorBody = await invalidHubResponse.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain(WrongCustomToken, errorBody, StringComparison.Ordinal);

        // Verificar logs reales capturados a través de RedactingConsoleFormatter.
        var allLogs = string.Join('\n', capturedLogs);
        Assert.Contains("/hubs/runs/negotiate", allLogs, StringComparison.Ordinal);
        Assert.Contains("access_token=***", allLogs, StringComparison.Ordinal);
        Assert.DoesNotContain(CustomNonCnxToken, allLogs, StringComparison.Ordinal);
        Assert.DoesNotContain("9999", allLogs, StringComparison.Ordinal);
        Assert.DoesNotContain(WrongCustomToken, allLogs, StringComparison.Ordinal);
        Assert.DoesNotContain("8888", allLogs, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_hubs_and_openapi_paths_return_404_problem_details_never_index_html()
    {
        await using var factory = new CodeNetixFactory(_dataDirectory, _webRoot, CodeNetixFactory.CompleteSettings);
        using var client = factory.CreateAuthorizedClient();

        foreach (var path in new[] { "/hubs/desconocido", "/openapi/desconocido" })
        {
            using var response = await client.GetAsync(path, Ct);
            var body = await response.Content.ReadAsStringAsync(Ct);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            Assert.DoesNotContain("<!doctype html>", body, StringComparison.OrdinalIgnoreCase);
        }

        // Un documento OpenAPI inexistente tampoco devuelve index.html.
        using var missingDoc = await client.GetAsync("/openapi/v99.json", Ct);
        Assert.Equal(HttpStatusCode.NotFound, missingDoc.StatusCode);
        Assert.DoesNotContain("<!doctype html>", await missingDoc.Content.ReadAsStringAsync(Ct), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Subscribed_hub_client_receives_live_RunEvents_with_same_polymorphic_json_as_events_endpoint()
    {
        await using var factory = new CodeNetixFactory(_dataDirectory, _webRoot, CodeNetixFactory.CompleteSettings);
        using var client = factory.CreateAuthorizedClient();

        var runId = await SubmitManualIssueAndWaitForRunAsync(client, "https://github.com/o/r/issues/4101");

        var received = Channel.CreateUnbounded<JsonElement>();
        await using var connection = CreateHubConnection(factory, CodeNetixFactory.TestAccessToken);
        connection.On<JsonElement>(RunsHub.EventMethod, payload => received.Writer.TryWrite(payload.Clone()));

        await connection.StartAsync(Ct);
        await connection.InvokeAsync("SubscribeRun", runId, Ct);

        var sink = factory.Services.GetRequiredService<IRunEventSink>();
        var ev1 = await sink.AppendAsync(runId, null, new SpecGenerated(1, "test-model"), Ct);
        var ev2 = await sink.AppendAsync(runId, null, new VerdictRecorded(1, ReviewerKind.Security, VerdictDecision.Approved, 0, "test-model"), Ct);

        var live1 = await ReadMatchingSeqAsync(received.Reader, ev1.Seq);
        var live2 = await ReadMatchingSeqAsync(received.Reader, ev2.Seq);

        Assert.Equal(nameof(SpecGenerated), live1.GetProperty("type").GetString());
        Assert.Equal(ev1.Seq, live1.GetProperty("seq").GetInt64());
        Assert.Equal("test-model", live1.GetProperty("model").GetString());

        Assert.Equal(nameof(VerdictRecorded), live2.GetProperty("type").GetString());
        Assert.Equal(ev2.Seq, live2.GetProperty("seq").GetInt64());
        Assert.Equal("security", live2.GetProperty("reviewer").GetString());
        Assert.Equal("approved", live2.GetProperty("decision").GetString());

        using var httpEventsResponse = await client.GetAsync($"/api/runs/{runId}/events?afterSeq={ev1.Seq - 1}", Ct);
        httpEventsResponse.EnsureSuccessStatusCode();
        var httpEvents = await httpEventsResponse.Content.ReadFromJsonAsync<List<JsonElement>>(WebJson, Ct);
        Assert.NotNull(httpEvents);

        var http1 = Assert.Single(httpEvents, e => e.GetProperty("seq").GetInt64() == ev1.Seq);
        var http2 = Assert.Single(httpEvents, e => e.GetProperty("seq").GetInt64() == ev2.Seq);
        Assert.Equal(http1.GetRawText(), live1.GetRawText());
        Assert.Equal(http2.GetRawText(), live2.GetRawText());

        await connection.InvokeAsync("UnsubscribeRun", runId, Ct);
        await connection.StopAsync(Ct);
    }

    [Fact]
    public async Task Group_isolation_and_UnsubscribeRun_prevent_unwanted_events()
    {
        await using var factory = new CodeNetixFactory(_dataDirectory, _webRoot, CodeNetixFactory.CompleteSettings);
        using var client = factory.CreateAuthorizedClient();

        var runA = await SubmitManualIssueAndWaitForRunAsync(client, "https://github.com/o/r/issues/4120");
        var runB = await SubmitManualIssueAndWaitForRunAsync(client, "https://github.com/o/r/issues/4121");

        var received = Channel.CreateUnbounded<JsonElement>();
        await using var connection = CreateHubConnection(factory, CodeNetixFactory.TestAccessToken);
        connection.On<JsonElement>(RunsHub.EventMethod, payload => received.Writer.TryWrite(payload.Clone()));

        await connection.StartAsync(Ct);
        await connection.InvokeAsync("SubscribeRun", runA, Ct);

        var sink = factory.Services.GetRequiredService<IRunEventSink>();
        // Evento en runB no debe llegar al cliente suscrito solo a runA.
        await sink.AppendAsync(runB, null, new OutputAppended("solo-run-B\n"), Ct);
        var evA1 = await sink.AppendAsync(runA, null, new OutputAppended("para-run-A\n"), Ct);

        var liveA1 = await ReadMatchingSeqAsync(received.Reader, evA1.Seq, runA);
        Assert.Equal(runA, liveA1.GetProperty("runId").GetGuid());
        Assert.Equal("para-run-A\n", liveA1.GetProperty("text").GetString());

        // Drenar y comprobar que ningún evento de runB se coló en el canal.
        while (received.Reader.TryRead(out var extra))
        {
            Assert.NotEqual(runB, extra.GetProperty("runId").GetGuid());
        }

        // Tras UnsubscribeRun(runA), los nuevos eventos de runA ya no deben llegar.
        await connection.InvokeAsync("UnsubscribeRun", runA, Ct);
        var evA2 = await sink.AppendAsync(runA, null, new OutputAppended("despues-de-unsubscribe\n"), Ct);
        await Task.Delay(250, Ct);

        while (received.Reader.TryRead(out var afterUnsub))
        {
            var seq = afterUnsub.GetProperty("seq").GetInt64();
            Assert.NotEqual(evA2.Seq, seq);
        }

        await connection.StopAsync(Ct);
    }

    [Fact]
    public async Task SaveAndPublishAsync_publishes_only_after_sqlite_commit_and_never_on_DbUpdateException()
    {
        var dbPath = Path.Combine(_root, "postcommit.db");
        Directory.CreateDirectory(_root);
        var connectionString = $"Data Source={dbPath}";
        var dbOptions = new DbContextOptionsBuilder<PipelineDbContext>();
        PipelineDbContext.Configure(dbOptions, connectionString);

        await using (var setup = new PipelineDbContext(dbOptions.Options))
        {
            await setup.Database.MigrateAsync(Ct);
        }

        var committedSeenInsidePublisher = false;
        var publishAttempts = 0;

        var verifyingPublisher = new CallbackRunEventPublisher(async (ev, ct) =>
        {
            Interlocked.Increment(ref publishAttempts);
            // Abrir un DbContext completamente nuevo: si el commit en SQLite ocurrió ANTES de publicar,
            // la fila ya debe ser visible desde otra conexión.
            await using var readerContext = new PipelineDbContext(dbOptions.Options);
            committedSeenInsidePublisher = await readerContext.Events
                .AsNoTracking()
                .AnyAsync(e => e.RunId == ev.RunId && e.Seq == ev.Seq, ct);
        });

        var log = new RunEventLog(new SecretRedactor([]), TimeProvider.System, verifyingPublisher);

        Guid runId;
        await using (var ctx = new PipelineDbContext(dbOptions.Options))
        {
            var (run, created, initial) = PipelineRun.Create(Guid.NewGuid(), "github:o/r#900", DateTimeOffset.UtcNow);
            runId = run.Id;
            ctx.Runs.Add(run);
            await log.AddAsync(ctx, run.Id, created, Ct);
            await log.AddAsync(ctx, run.Id, initial, Ct);
            await log.SaveAndPublishAsync(ctx, Ct);
        }

        Assert.Equal(2, publishAttempts);
        Assert.True(committedSeenInsidePublisher, "El evento debe estar confirmado en SQLite antes de invocar IRunEventPublisher.PublishAsync.");

        // Caso de fallo en SaveChangesAsync: forzar DbUpdateException por clave primaria duplicada (RunId, Seq = 1).
        var attemptsBeforeFailure = publishAttempts;
        await using (var failingCtx = new PipelineDbContext(dbOptions.Options))
        {
            failingCtx.Events.Add(new RunEventRecord
            {
                RunId = runId,
                Seq = 1, // Ya existe en SQLite -> UNIQUE/PK violation.
                Type = nameof(OutputAppended),
                PayloadJson = RunEventJson.Serialize(new OutputAppended("duplicado") with { RunId = runId, Seq = 1, At = DateTimeOffset.UtcNow }),
                At = DateTimeOffset.UtcNow,
            });

            await Assert.ThrowsAsync<DbUpdateException>(() => log.SaveAndPublishAsync(failingCtx, Ct));
        }

        Assert.Equal(attemptsBeforeFailure, publishAttempts);
    }

    [Fact]
    public async Task Publisher_failure_does_not_rollback_or_break_persisted_events()
    {
        var failingPublisher = new FailingRunEventPublisher();
        await using var baseFactory = new CodeNetixFactory(_dataDirectory, _webRoot, CodeNetixFactory.CompleteSettings);
        await using var factory = baseFactory.WithWebHostBuilder(b =>
            b.ConfigureTestServices(services => services.AddSingleton<IRunEventPublisher>(failingPublisher)));

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", CodeNetixFactory.TestAccessToken);

        var runId = await SubmitManualIssueAndWaitForRunAsync(client, "https://github.com/o/r/issues/4102");

        var sink = factory.Services.GetRequiredService<IRunEventSink>();
        var appended = await sink.AppendAsync(runId, null, new OutputAppended("linea post-commit\n"), Ct);

        Assert.True(failingPublisher.Attempts > 0);
        var events = await client.GetFromJsonAsync<List<RunEvent>>($"/api/runs/{runId}/events?afterSeq={appended.Seq - 1}", WebJson, Ct);
        Assert.NotNull(events);
        Assert.Single(events, e => e.Seq == appended.Seq && e is OutputAppended { Text: "linea post-commit\n" });
    }

    [Fact]
    public async Task Reconnection_with_real_HubConnection_resumes_without_loss_or_duplicates_and_rejects_negative_afterSeq()
    {
        await using var factory = new CodeNetixFactory(_dataDirectory, _webRoot, CodeNetixFactory.CompleteSettings);
        using var client = factory.CreateAuthorizedClient();

        var runId = await SubmitManualIssueAndWaitForRunAsync(client, "https://github.com/o/r/issues/4103");
        var sink = factory.Services.GetRequiredService<IRunEventSink>();

        // Fase 1: conectado en vivo, recibir firstBatch por SignalR. La Ejecución sigue orquestándose en segundo
        // plano y añade sus propios eventos entre los del test: se guardan todos los recibidos en vivo (no solo los
        // esperados) para que la fusión con afterSeq pueda demostrar que no falta ni se repite ninguno.
        var liveReceived = Channel.CreateUnbounded<RunEvent>();
        var liveBySeq = new ConcurrentDictionary<long, RunEvent>();
        var liveDuplicates = new ConcurrentQueue<long>();
        await using var connection = CreateHubConnection(factory, CodeNetixFactory.TestAccessToken);
        connection.On<JsonElement>(RunsHub.EventMethod, payload =>
        {
            var ev = JsonSerializer.Deserialize<RunEvent>(payload.GetRawText(), WebJson);
            if (ev is not null && ev.RunId == runId)
            {
                if (!liveBySeq.TryAdd(ev.Seq, ev))
                {
                    liveDuplicates.Enqueue(ev.Seq);
                }

                liveReceived.Writer.TryWrite(ev);
            }
        });

        await connection.StartAsync(Ct);
        await connection.InvokeAsync("SubscribeRun", runId, Ct);

        var collectedBySeq = new Dictionary<long, RunEvent>();
        var firstBatch = new List<long>();
        for (var i = 0; i < 3; i++)
        {
            var ev = await sink.AppendAsync(runId, null, new OutputAppended($"antes-{i}\n"), Ct);
            firstBatch.Add(ev.Seq);
            var liveEv = await ReadMatchingRunEventAsync(liveReceived.Reader, ev.Seq);
            collectedBySeq[liveEv.Seq] = liveEv;
        }

        var lastSeenSeq = firstBatch[^1];

        // Fase 2: desconectar, emitir missedBatch mientras el cliente está caído.
        await connection.StopAsync(Ct);

        var missedBatch = new List<long>();
        for (var i = 0; i < 4; i++)
        {
            var ev = await sink.AppendAsync(runId, null, new OutputAppended($"perdido-{i}\n"), Ct);
            missedBatch.Add(ev.Seq);
        }

        // Fase 3: reconectar, re-suscribir, emitir afterReconnectBatch en vivo y hacer catch-up con afterSeq.
        await connection.StartAsync(Ct);
        await connection.InvokeAsync("SubscribeRun", runId, Ct);

        var afterReconnectBatch = new List<long>();
        for (var i = 0; i < 2; i++)
        {
            var ev = await sink.AppendAsync(runId, null, new OutputAppended($"despues-{i}\n"), Ct);
            afterReconnectBatch.Add(ev.Seq);
            var liveEv = await ReadMatchingRunEventAsync(liveReceived.Reader, ev.Seq);
            collectedBySeq[liveEv.Seq] = liveEv;
        }

        // Lo recibido en vivo hasta lastSeenSeq (incluidos los eventos del orquestador intercalados).
        foreach (var (seq, ev) in liveBySeq)
        {
            if (seq <= lastSeenSeq)
            {
                collectedBySeq[seq] = ev;
            }
        }

        // Catch-up desde lastSeenSeq (incluye missedBatch y solapa con afterReconnectBatch: debe deduplicar por seq).
        var resumed = await client.GetFromJsonAsync<List<RunEvent>>($"/api/runs/{runId}/events?afterSeq={lastSeenSeq}", WebJson, Ct);
        Assert.NotNull(resumed);
        Assert.True(resumed.All(e => e.Seq > lastSeenSeq));
        foreach (var ev in resumed)
        {
            collectedBySeq[ev.Seq] = ev;
        }

        // Verificar que tenemos firstBatch + missedBatch + afterReconnectBatch contiguos, ordenados y sin duplicados.
        var expectedSeqs = firstBatch.Concat(missedBatch).Concat(afterReconnectBatch).ToList();
        foreach (var seq in expectedSeqs)
        {
            Assert.True(collectedBySeq.ContainsKey(seq), $"Falta seq={seq} tras fusionar en vivo + afterSeq.");
        }

        var mergedSlice = collectedBySeq.Values
            .Where(e => e.Seq >= firstBatch[0] && e.Seq <= afterReconnectBatch[^1])
            .OrderBy(e => e.Seq)
            .ToList();
        // El orquestador intercala sus propios eventos: el tramo [primero, último] debe estar completo, no medir solo los del test.
        Assert.Equal(afterReconnectBatch[^1] - firstBatch[0] + 1, mergedSlice.Select(e => e.Seq).Distinct().LongCount());
        for (var i = 1; i < mergedSlice.Count; i++)
        {
            Assert.Equal(mergedSlice[i - 1].Seq + 1, mergedSlice[i].Seq);
        }

        // SignalR no entrega dos veces el mismo seq (la deduplicación del Panel no puede tapar un duplicado real).
        Assert.Empty(liveDuplicates);

        // afterSeq < 0 -> 400 Bad Request
        using var badRequest = await client.GetAsync($"/api/runs/{runId}/events?afterSeq=-1", Ct);
        Assert.Equal(HttpStatusCode.BadRequest, badRequest.StatusCode);

        await connection.StopAsync(Ct);
    }

    [Fact]
    public async Task List_runs_returns_recent_runs_ordered_descending_and_validates_limit()
    {
        await using var factory = new CodeNetixFactory(_dataDirectory, _webRoot, CodeNetixFactory.CompleteSettings);
        using var client = factory.CreateAuthorizedClient();

        var firstRunId = await SubmitManualIssueAndWaitForRunAsync(client, "https://github.com/o/r/issues/4110");
        await Task.Delay(20, Ct);
        var secondRunId = await SubmitManualIssueAndWaitForRunAsync(client, "https://github.com/o/r/issues/4111");

        var runs = await client.GetFromJsonAsync<List<RunSummary>>("/api/runs", WebJson, Ct);
        Assert.NotNull(runs);
        Assert.True(runs.Count >= 2);
        var firstIdx = runs.FindIndex(r => r.RunId == firstRunId);
        var secondIdx = runs.FindIndex(r => r.RunId == secondRunId);
        Assert.True(secondIdx >= 0 && firstIdx >= 0 && secondIdx < firstIdx, "Las Ejecuciones deben venir ordenadas por CreatedAt descendente.");

        // limit=1 solo devuelve la más reciente.
        var limited = await client.GetFromJsonAsync<List<RunSummary>>("/api/runs?limit=1", WebJson, Ct);
        Assert.NotNull(limited);
        var only = Assert.Single(limited);
        Assert.Equal(secondRunId, only.RunId);

        // limit=500 se acota a MaxLimit y devuelve 200 OK.
        using var clampedResponse = await client.GetAsync("/api/runs?limit=500", Ct);
        Assert.Equal(HttpStatusCode.OK, clampedResponse.StatusCode);

        // limit <= 0 -> 400 Bad Request.
        using var zeroResponse = await client.GetAsync("/api/runs?limit=0", Ct);
        Assert.Equal(HttpStatusCode.BadRequest, zeroResponse.StatusCode);

        using var negativeResponse = await client.GetAsync("/api/runs?limit=-5", Ct);
        Assert.Equal(HttpStatusCode.BadRequest, negativeResponse.StatusCode);
    }

    private static HubConnection CreateHubConnection(CodeNetixFactory factory, string accessToken) =>
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

    private static async Task<JsonElement> ReadMatchingSeqAsync(ChannelReader<JsonElement> reader, long targetSeq, Guid? expectedRunId = null)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

        while (await reader.WaitToReadAsync(timeoutCts.Token))
        {
            while (reader.TryRead(out var item))
            {
                if (expectedRunId is { } rid && item.TryGetProperty("runId", out var runIdProp) && runIdProp.GetGuid() != rid)
                {
                    continue;
                }

                if (item.TryGetProperty("seq", out var seqProp) && seqProp.GetInt64() == targetSeq)
                {
                    return item;
                }
            }
        }

        throw new TimeoutException($"No se recibió el evento seq={targetSeq} por SignalR.");
    }

    private static async Task<RunEvent> ReadMatchingRunEventAsync(ChannelReader<RunEvent> reader, long targetSeq)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

        while (await reader.WaitToReadAsync(timeoutCts.Token))
        {
            while (reader.TryRead(out var item))
            {
                if (item.Seq == targetSeq)
                {
                    return item;
                }
            }
        }

        throw new TimeoutException($"No se recibió el RunEvent seq={targetSeq} por SignalR.");
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

    private sealed class FailingRunEventPublisher : IRunEventPublisher
    {
        private int _attempts;

        public int Attempts => Volatile.Read(ref _attempts);

        public Task PublishAsync(RunEvent runEvent, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _attempts);
            throw new InvalidOperationException("Fallo simulado de SignalR");
        }
    }

    private sealed class CallbackRunEventPublisher(Func<RunEvent, CancellationToken, Task> onPublish) : IRunEventPublisher
    {
        public Task PublishAsync(RunEvent runEvent, CancellationToken cancellationToken) =>
            onPublish(runEvent, cancellationToken);
    }

    private sealed class RedactedCapturingLoggerProvider(RedactingConsoleFormatter formatter, ConcurrentQueue<string> messages)
        : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new RedactedCapturingLogger(categoryName, formatter, messages);

        public void Dispose()
        {
        }

        private sealed class RedactedCapturingLogger(string categoryName, RedactingConsoleFormatter formatter, ConcurrentQueue<string> messages)
            : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> msgFormatter)
            {
                using var writer = new StringWriter();
                var entry = new LogEntry<TState>(logLevel, categoryName, eventId, state, exception, msgFormatter);
                formatter.Write(in entry, scopeProvider: null, writer);
                var formatted = writer.ToString();
                if (!string.IsNullOrEmpty(formatted))
                {
                    messages.Enqueue(formatted);
                }
            }
        }
    }
}
