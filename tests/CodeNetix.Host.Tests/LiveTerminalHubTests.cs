using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using CodeNetix.Contracts;
using CodeNetix.Host.Hubs;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace CodeNetix.Host.Tests;

/// <summary>
/// Pruebas de integración de la Historia 4.2 (Terminal en vivo):
/// - Streaming en vivo de <see cref="OutputAppended"/> por <c>/hubs/runs</c> y <c>GET /api/runs/{id}/events</c>
///   con secretos enmascarados (AD-13: nunca en claro).
/// - Límite de 5 MiB por Etapa con corte respetando UTF-8 multibyte, emisión de un único
///   <see cref="OutputTruncated"/>, descarte posterior para esa Etapa y aislamiento respecto a otras Etapas.
/// - Reconexión SignalR con <see cref="OutputAppended"/> en curso y recuperación sin huecos ni duplicados vía <c>afterSeq</c>.
/// </summary>
public sealed class LiveTerminalHubTests : IDisposable
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "codenetix-term-" + Guid.NewGuid().ToString("N"));
    private readonly string _dataDirectory;
    private readonly string _webRoot;

    public LiveTerminalHubTests()
    {
        _dataDirectory = Path.Combine(_root, "data");
        _webRoot = Path.Combine(_root, "wwwroot");
        Directory.CreateDirectory(_webRoot);
        File.WriteAllText(Path.Combine(_webRoot, "index.html"), "<!doctype html><html><body>ok</body></html>");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OutputAppended_streams_live_with_secrets_redacted_on_hub_and_events_endpoint()
    {
        const string AnthropicKey = "sk-ant-api03-secretvalue1234567890abcdef";
        const string GitHubToken = "ghp_secretgithubtoken0123456789abcdef";
        const string AdminToken = "cnx_supersecretadmintoken0123456789";

        var settings = new Dictionary<string, string?>(CodeNetixFactory.CompleteSettings)
        {
            ["CODENETIX:Providers:Anthropic:ApiKey"] = AnthropicKey,
            ["CODENETIX:GitHub:Token"] = GitHubToken,
            [CodeNetixFactory.AccessTokenKey] = AdminToken,
        };

        await using var factory = new CodeNetixFactory(_dataDirectory, _webRoot, settings);
        using var client = factory.CreateAuthorizedClient(AdminToken);

        var runId = await SubmitManualIssueAndWaitForRunAsync(client, "https://github.com/o/r/issues/4201");

        var received = Channel.CreateUnbounded<JsonElement>();
        await using var connection = CreateHubConnection(factory, AdminToken);
        connection.On<JsonElement>(RunsHub.EventMethod, payload => received.Writer.TryWrite(payload.Clone()));

        await connection.StartAsync(Ct);
        await connection.InvokeAsync("SubscribeRun", runId, Ct);

        var sink = factory.Services.GetRequiredService<IRunEventSink>();
        var stageId = Guid.NewGuid();

        var stdoutEv = await sink.AppendAsync(
            runId,
            stageId,
            new OutputAppended($"Exportando ANTHROPIC_API_KEY={AnthropicKey} y GITHUB_TOKEN={GitHubToken}\n", "stdout"),
            Ct);
        var stderrEv = await sink.AppendAsync(
            runId,
            stageId,
            new OutputAppended($"Header Authorization: Bearer {AdminToken}\n", "stderr"),
            Ct);

        var liveStdout = await ReadMatchingSeqAsync(received.Reader, stdoutEv.Seq, runId);
        var liveStderr = await ReadMatchingSeqAsync(received.Reader, stderrEv.Seq, runId);

        var liveStdoutText = liveStdout.GetProperty("text").GetString() ?? string.Empty;
        var liveStderrText = liveStderr.GetProperty("text").GetString() ?? string.Empty;

        Assert.Equal("stdout", liveStdout.GetProperty("stream").GetString());
        Assert.Equal("stderr", liveStderr.GetProperty("stream").GetString());
        Assert.Equal(stageId, liveStdout.GetProperty("stageId").GetGuid());
        Assert.Equal(stageId, liveStderr.GetProperty("stageId").GetGuid());

        // Ningún secreto puede llegar en claro por SignalR (AD-13).
        Assert.DoesNotContain(AnthropicKey, liveStdoutText, StringComparison.Ordinal);
        Assert.DoesNotContain("secretvalue1234567890", liveStdoutText, StringComparison.Ordinal);
        Assert.DoesNotContain(GitHubToken, liveStdoutText, StringComparison.Ordinal);
        Assert.DoesNotContain("secretgithubtoken", liveStdoutText, StringComparison.Ordinal);
        Assert.DoesNotContain(AdminToken, liveStderrText, StringComparison.Ordinal);
        Assert.DoesNotContain("supersecretadmintoken", liveStderrText, StringComparison.Ordinal);
        Assert.Contains("…", liveStdoutText, StringComparison.Ordinal);
        Assert.Contains("…", liveStderrText, StringComparison.Ordinal);

        // Tampoco pueden aparecer en claro al consultar GET /api/runs/{id}/events.
        using var httpResponse = await client.GetAsync($"/api/runs/{runId}/events?afterSeq={stdoutEv.Seq - 1}", Ct);
        httpResponse.EnsureSuccessStatusCode();
        var rawJson = await httpResponse.Content.ReadAsStringAsync(Ct);

        Assert.DoesNotContain(AnthropicKey, rawJson, StringComparison.Ordinal);
        Assert.DoesNotContain("secretvalue1234567890", rawJson, StringComparison.Ordinal);
        Assert.DoesNotContain(GitHubToken, rawJson, StringComparison.Ordinal);
        Assert.DoesNotContain("secretgithubtoken", rawJson, StringComparison.Ordinal);
        Assert.DoesNotContain(AdminToken, rawJson, StringComparison.Ordinal);
        Assert.DoesNotContain("supersecretadmintoken", rawJson, StringComparison.Ordinal);

        var httpEvents = JsonSerializer.Deserialize<List<JsonElement>>(rawJson, WebJson);
        Assert.NotNull(httpEvents);
        var httpStdout = Assert.Single(httpEvents, e => e.GetProperty("seq").GetInt64() == stdoutEv.Seq);
        var httpStderr = Assert.Single(httpEvents, e => e.GetProperty("seq").GetInt64() == stderrEv.Seq);
        Assert.Equal(httpStdout.GetRawText(), liveStdout.GetRawText());
        Assert.Equal(httpStderr.GetRawText(), liveStderr.GetRawText());

        await connection.InvokeAsync("UnsubscribeRun", runId, Ct);
        await connection.StopAsync(Ct);
    }

    [Fact]
    public async Task Stage_output_exceeding_5MiB_emits_single_OutputTruncated_and_isolates_other_stages()
    {
        await using var factory = new CodeNetixFactory(_dataDirectory, _webRoot, CodeNetixFactory.CompleteSettings);
        using var client = factory.CreateAuthorizedClient();

        var runId = await SubmitManualIssueAndWaitForRunAsync(client, "https://github.com/o/r/issues/4202");

        var liveEvents = Channel.CreateUnbounded<RunEvent>();
        await using var connection = CreateHubConnection(factory, CodeNetixFactory.TestAccessToken);
        connection.On<JsonElement>(RunsHub.EventMethod, payload =>
        {
            var ev = JsonSerializer.Deserialize<RunEvent>(payload.GetRawText(), WebJson);
            if (ev is not null && ev.RunId == runId)
            {
                liveEvents.Writer.TryWrite(ev);
            }
        });

        await connection.StartAsync(Ct);
        await connection.InvokeAsync("SubscribeRun", runId, Ct);

        var sink = factory.Services.GetRequiredService<IRunEventSink>();
        var stageA = Guid.NewGuid();
        var stageB = Guid.NewGuid();

        // 4 MiB + (1 MiB - 2 bytes) dejan exactamente 2 bytes libres antes de 5 MiB.
        // Luego emitimos un fragmento multibyte UTF-8 ("🔥éfin\n", donde 🔥 ocupa 4 bytes y é 2 bytes)
        // para comprobar que el corte respeta UTF-8 sin generar U+FFFD ni superar 5 MiB.
        var oneMiBChunk = new string('A', 1024 * 1024);
        for (var i = 0; i < 4; i++)
        {
            await sink.AppendAsync(runId, stageA, new OutputAppended(oneMiBChunk), Ct);
        }

        await sink.AppendAsync(runId, stageA, new OutputAppended(new string('B', (1024 * 1024) - 2)), Ct);
        await sink.AppendAsync(runId, stageA, new OutputAppended("🔥éfin\n"), Ct);

        // Un fragmento posterior tras el truncado debe descartarse por completo sin persistir ni emitir.
        var discarded = await sink.AppendAsync(runId, stageA, new OutputAppended("descartado-tras-truncado\n"), Ct);
        Assert.Equal(0, discarded.Seq);

        // Otra Etapa distinta en la misma Ejecución debe seguir pudiendo emitir salida normalmente.
        var stageBEvent = await sink.AppendAsync(runId, stageB, new OutputAppended("salida-etapa-B\n"), Ct);
        Assert.True(stageBEvent.Seq > 0);

        // Esperar en vivo hasta recibir el evento de stageB (garantiza que todos los eventos previos de SignalR ya se entregaron).
        var collectedLive = await CollectUntilSeqAsync(liveEvents.Reader, stageBEvent.Seq);

        // Verificar eventos recibidos en vivo por /hubs/runs.
        var liveTruncatedA = Assert.Single(collectedLive.OfType<OutputTruncated>(), e => e.StageId == stageA);
        Assert.Equal(OutputTruncated.DefaultMaxBytes, liveTruncatedA.MaxBytes);

        var liveOutputA = collectedLive.OfType<OutputAppended>().Where(e => e.StageId == stageA).ToList();
        Assert.All(liveOutputA, e =>
        {
            Assert.True(e.Seq < liveTruncatedA.Seq);
            Assert.DoesNotContain('\uFFFD', e.Text);
        });
        Assert.True(liveOutputA.Sum(e => Encoding.UTF8.GetByteCount(e.Text)) <= OutputTruncated.DefaultMaxBytes);

        var liveStageB = Assert.Single(collectedLive.OfType<OutputAppended>(), e => e.StageId == stageB);
        Assert.Equal("salida-etapa-B\n", liveStageB.Text);

        // Verificar también lo persistido vía GET /api/runs/{id}/events.
        var httpEvents = await client.GetFromJsonAsync<List<RunEvent>>($"/api/runs/{runId}/events", WebJson, Ct);
        Assert.NotNull(httpEvents);

        var persistedTruncatedA = Assert.Single(httpEvents.OfType<OutputTruncated>(), e => e.StageId == stageA);
        Assert.Equal(OutputTruncated.DefaultMaxBytes, persistedTruncatedA.MaxBytes);
        Assert.Equal(liveTruncatedA.Seq, persistedTruncatedA.Seq);

        var persistedOutputA = httpEvents.OfType<OutputAppended>().Where(e => e.StageId == stageA).ToList();
        Assert.All(persistedOutputA, e =>
        {
            Assert.True(e.Seq < persistedTruncatedA.Seq);
            Assert.DoesNotContain('\uFFFD', e.Text);
        });
        var totalBytesA = persistedOutputA.Sum(e => Encoding.UTF8.GetByteCount(e.Text));
        Assert.True(totalBytesA <= OutputTruncated.DefaultMaxBytes);

        var persistedStageB = Assert.Single(httpEvents.OfType<OutputAppended>(), e => e.StageId == stageB);
        Assert.Equal("salida-etapa-B\n", persistedStageB.Text);

        await connection.InvokeAsync("UnsubscribeRun", runId, Ct);
        await connection.StopAsync(Ct);
    }

    [Fact]
    public async Task Reconnection_with_live_OutputAppended_recovers_missed_chunks_via_afterSeq_without_gaps_or_duplicates()
    {
        await using var factory = new CodeNetixFactory(_dataDirectory, _webRoot, CodeNetixFactory.CompleteSettings);
        using var client = factory.CreateAuthorizedClient();

        var runId = await SubmitManualIssueAndWaitForRunAsync(client, "https://github.com/o/r/issues/4203");
        var sink = factory.Services.GetRequiredService<IRunEventSink>();
        var stageId = Guid.NewGuid();

        var liveEvents = Channel.CreateUnbounded<RunEvent>();
        await using var connection = CreateHubConnection(factory, CodeNetixFactory.TestAccessToken);
        connection.On<JsonElement>(RunsHub.EventMethod, payload =>
        {
            var ev = JsonSerializer.Deserialize<RunEvent>(payload.GetRawText(), WebJson);
            if (ev is not null && ev.RunId == runId)
            {
                liveEvents.Writer.TryWrite(ev);
            }
        });

        await connection.StartAsync(Ct);
        await connection.InvokeAsync("SubscribeRun", runId, Ct);

        // 1. Recibir fragmento inicial en vivo.
        var mergedBySeq = new Dictionary<long, OutputAppended>();
        var initialEv = await sink.AppendAsync(runId, stageId, new OutputAppended("chunk-inicial\n"), Ct);
        var liveInitial = (OutputAppended)await ReadMatchingRunEventAsync(liveEvents.Reader, initialEv.Seq);
        mergedBySeq[liveInitial.Seq] = liveInitial;

        // 2. Desconectar y emitir fragmentos perdidos mientras el cliente está desconectado.
        await connection.StopAsync(Ct);
        var missed1 = await sink.AppendAsync(runId, stageId, new OutputAppended("chunk-perdido-1\n"), Ct);
        var missed2 = await sink.AppendAsync(runId, stageId, new OutputAppended("chunk-perdido-2\n"), Ct);
        var missed3 = await sink.AppendAsync(runId, stageId, new OutputAppended("chunk-perdido-3\n"), Ct);

        // 3. Recuperar con GET /api/runs/{id}/events?afterSeq=N.
        var catchUp = await client.GetFromJsonAsync<List<RunEvent>>(
            $"/api/runs/{runId}/events?afterSeq={initialEv.Seq}",
            WebJson,
            Ct);
        Assert.NotNull(catchUp);
        foreach (var ev in catchUp.OfType<OutputAppended>().Where(e => e.StageId == stageId))
        {
            mergedBySeq[ev.Seq] = ev;
        }

        // 4. Reconectar y emitir un fragmento nuevo en vivo.
        await connection.StartAsync(Ct);
        await connection.InvokeAsync("SubscribeRun", runId, Ct);

        var liveAfterReconnect = await sink.AppendAsync(runId, stageId, new OutputAppended("chunk-tras-reconectar\n"), Ct);
        var receivedAfterReconnect = (OutputAppended)await ReadMatchingRunEventAsync(liveEvents.Reader, liveAfterReconnect.Seq);
        mergedBySeq[receivedAfterReconnect.Seq] = receivedAfterReconnect;

        // 5. Verificar secuencia contigua, ordenada y sin duplicados.
        var expectedSeqs = new[] { initialEv.Seq, missed1.Seq, missed2.Seq, missed3.Seq, liveAfterReconnect.Seq };
        var ordered = mergedBySeq.Values.OrderBy(e => e.Seq).ToList();

        Assert.Equal(expectedSeqs, ordered.Select(e => e.Seq).ToArray());
        Assert.Equal(
            new[]
            {
                "chunk-inicial\n",
                "chunk-perdido-1\n",
                "chunk-perdido-2\n",
                "chunk-perdido-3\n",
                "chunk-tras-reconectar\n",
            },
            ordered.Select(e => e.Text).ToArray());

        await connection.InvokeAsync("UnsubscribeRun", runId, Ct);
        await connection.StopAsync(Ct);
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

    private static async Task<JsonElement> ReadMatchingSeqAsync(ChannelReader<JsonElement> reader, long targetSeq, Guid expectedRunId)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

        while (await reader.WaitToReadAsync(timeoutCts.Token))
        {
            while (reader.TryRead(out var item))
            {
                if (item.TryGetProperty("runId", out var runIdProp) && runIdProp.GetGuid() != expectedRunId)
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

    private static async Task<List<RunEvent>> CollectUntilSeqAsync(ChannelReader<RunEvent> reader, long targetSeq)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));

        var items = new List<RunEvent>();
        while (await reader.WaitToReadAsync(timeoutCts.Token))
        {
            while (reader.TryRead(out var item))
            {
                items.Add(item);
                if (item.Seq == targetSeq)
                {
                    return items;
                }
            }
        }

        throw new TimeoutException($"No se recibió el evento seq={targetSeq} por SignalR.");
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
}
