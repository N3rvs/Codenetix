using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CodeNetix.Contracts;
using CodeNetix.Host.Configuration;
using CodeNetix.Host.Features.ReceiveWebhook;
using CodeNetix.Ingestion.Features.ReceiveSentryWebhook;
using CodeNetix.Persistence;
using CodeNetix.Pipeline.Data;
using CodeNetix.Pipeline.Domain;
using CodeNetix.Pipeline.Events;
using CodeNetix.Pipeline.Features.DispatchEvents;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CodeNetix.Host.Tests;

/// <summary>
/// Historia 7.3 (FR-7, FR-8, AD-4, AD-10, AD-17, AD-18): <c>POST :5001/webhooks/sentry</c> en <c>serve</c> contra un GitHub
/// falso. Las peticiones van por <c>TestServer</c> con <c>LocalPort</c> = 5001 (nunca se enlaza :5000/:5001) y con firmas
/// HMAC SHA-256 reales (<c>Sentry-Hook-Signature</c>). Una prueba por fila de la matriz de la spec.
/// </summary>
public sealed class SentryWebhookTests : IAsyncLifetime
{
    private const string Secret = "sentry-client-secret-0123456789abcdef";
    private const string Project = "front-end";
    private const string IssueId = "123";
    private const string Permalink = "https://sentry.io/organizations/acme/issues/123/";
    private static readonly string Token = CodeNetixFactory.CompleteSettings["CODENETIX:GitHub:Token"]!;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "codenetix-sentry-" + Guid.NewGuid().ToString("N"));
    private readonly string _dataDirectory;
    private readonly string _webRoot;
    private readonly ConcurrentQueue<string> _logs = new();
    private FakeGitHub _gitHub = null!;
    private int _startupCalls;

    public SentryWebhookTests()
    {
        _dataDirectory = Path.Combine(_root, "data");
        _webRoot = Path.Combine(_root, "wwwroot");
        Directory.CreateDirectory(_webRoot);
        File.WriteAllText(Path.Combine(_webRoot, "index.html"), "<!doctype html><html><body>Panel</body></html>");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string DatabasePath => Path.Combine(_dataDirectory, PersistenceOptions.DatabaseFileName);

    public async ValueTask InitializeAsync() => _gitHub = await FakeGitHub.StartAsync(Ct);

    // --- Fila 1: incidencia nueva → 202 en < 1 s; issue de seguimiento y Evento Accepted sentry:<id> ---

    [Fact]
    public async Task New_issue_is_acknowledged_in_under_a_second_and_becomes_an_Accepted_event_on_a_tracking_issue()
    {
        await using var host = await StartAsync(Settings());
        _gitHub.Delay = TimeSpan.FromSeconds(2); // crear la issue es lento: la respuesta no lo espera

        var delivery = Guid.NewGuid().ToString();
        var watch = Stopwatch.StartNew();
        var response = await SendAsync(host, "issue", IssuePayload(), delivery);
        watch.Stop();

        Assert.Equal(StatusCodes.Status202Accepted, response.Status);
        Assert.Contains("\"queued\"", response.Body, StringComparison.Ordinal);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"El webhook tardó {watch.Elapsed} en responder.");

        Assert.True(await WaitForAsync(async () => (await ReadEventsAsync()).Count == 1), "El procesado asíncrono debe crear el Evento.");
        var issue = Assert.Single(_gitHub.CreatedIssues("o", "r"));
        Assert.Equal([SentryEventKey.TrackingLabel], issue.Labels);
        Assert.Contains("<!-- codenetix:sentry-issue:123 -->", issue.Body, StringComparison.Ordinal);
        Assert.StartsWith("[Sentry] NullReferenceException", issue.Title, StringComparison.Ordinal);
        Assert.Contains(Permalink, issue.Body, StringComparison.Ordinal);

        var row = Assert.Single(await ReadEventsAsync());
        Assert.Equal(("Sentry", "sentry:123", $"github:o/r#{issue.Number}"), (row.Source, row.DedupKey, row.ObjectKey));
        Assert.Equal(((string?)null, "Accepted", (string?)null), (row.Actor, row.Status, row.IgnoredReason));
        using (var payload = JsonDocument.Parse(row.Payload))
        {
            var root = payload.RootElement;
            Assert.Equal(IssueId, root.GetProperty("sentryIssueId").GetString());
            Assert.Equal(Project, root.GetProperty("project").GetString());
            Assert.Equal("created", root.GetProperty("kind").GetString());
            Assert.Equal(Permalink, root.GetProperty("permalink").GetString());
            Assert.Equal("o/r", root.GetProperty("repository").GetString());
            Assert.Equal(issue.Number, root.GetProperty("issueNumber").GetInt64());
            Assert.Equal(delivery, root.GetProperty("deliveryId").GetString());
        }

        Assert.DoesNotContain("data", row.Payload, StringComparison.Ordinal);

        _gitHub.Delay = TimeSpan.Zero;
        Assert.True(await WaitForAsync(async () => (await ReadEventsAsync()).Single().RunId is not null), "El Dispatcher debe crear la Ejecución.");
    }

    // --- Fila 2: proyecto sin correspondencia → 200 ignored, Evento Ignored persistido y visible en el Panel ---

    [Fact]
    public async Task Unmapped_project_is_persisted_as_Ignored_before_answering_and_is_listed_for_the_Panel()
    {
        await using var host = await StartAsync(Settings(project: null));

        var delivery = Guid.NewGuid().ToString();
        var response = await SendAsync(host, "issue", IssuePayload(), delivery);

        Assert.Equal(StatusCodes.Status200OK, response.Status);
        Assert.Contains("\"ignored\"", response.Body, StringComparison.Ordinal);
        Assert.Contains("CODENETIX__Sentry__Projects__front-end", response.Body, StringComparison.Ordinal);

        // Persistido antes de responder: sin esperar.
        var row = Assert.Single(await ReadEventsAsync());
        Assert.Equal(("Sentry", "sentry:123", "sentry:123", "Ignored"), (row.Source, row.DedupKey, row.ObjectKey, row.Status));
        Assert.Equal(SentryEventRecorder.UnmappedReason(Project), row.IgnoredReason);
        Assert.Equal(_startupCalls, _gitHub.TotalCalls);

        // Reentrega (mismo Request-ID) y otra entrega de la misma incidencia: el mismo Evento.
        await SendAsync(host, "issue", IssuePayload(), delivery);
        await SendAsync(host, "issue", IssuePayload(), Guid.NewGuid().ToString());
        Assert.Single(await ReadEventsAsync());

        using var client = host.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", CodeNetixFactory.TestAccessToken);
        var listed = await client.GetFromJsonAsync<List<IgnoredItem>>("/api/events/ignored", Ct);
        var item = Assert.Single(listed!);
        Assert.Equal(("Sentry", "sentry:123", SentryEventRecorder.UnmappedReason(Project)), (item.Source, item.ObjectKey, item.Reason));

        using var badLimit = await client.GetAsync("/api/events/ignored?limit=0", Ct);
        Assert.Equal(StatusCodes.Status400BadRequest, (int)badLimit.StatusCode);
        using var anonymous = host.Factory.CreateClient();
        using var unauthorized = await anonymous.GetAsync("/api/events/ignored", Ct);
        Assert.Equal(StatusCodes.Status401Unauthorized, (int)unauthorized.StatusCode);
    }

    // --- Fila 3: firma inválida → 401 sin detalle, nada procesado ---

    [Theory]
    [InlineData("missing")]
    [InlineData("repeated")]
    [InlineData("short")]
    [InlineData("not-hex")]
    [InlineData("prefixed")]
    [InlineData("other-secret")]
    [InlineData("tampered-body")]
    public async Task Invalid_signature_is_401_without_details_and_nothing_is_processed(string variant)
    {
        await using var host = await StartAsync(Settings(), captureLogs: true);
        var body = IssuePayload();
        var valid = Sign(Secret, body);
        var signature = variant switch
        {
            "missing" => null,
            "short" => valid[..^2],
            "not-hex" => new string('z', 64),
            "prefixed" => "sha256=" + valid,
            "other-secret" => Sign("another-secret-0123456789", body),
            _ => valid,
        };
        string[]? repeated = variant == "repeated" ? [valid, valid] : null;
        var sent = variant == "tampered-body" ? body.Replace("\"123\"", "\"124\"", StringComparison.Ordinal) : body;

        var response = await SendAsync(host, "issue", sent, Guid.NewGuid().ToString(), signature: signature, sign: false, signatureHeaders: repeated);

        Assert.Equal(StatusCodes.Status401Unauthorized, response.Status);
        Assert.Contains(ReceiveWebhookEndpoint.InvalidSignatureTitle, response.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"detail\"", response.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, response.Body, StringComparison.Ordinal);
        Assert.Equal(0, host.Ingestion.Reader.Count);
        Assert.Empty(await ReadEventsAsync());
        Assert.Equal(_startupCalls, _gitHub.TotalCalls);
        Assert.DoesNotContain(_logs, l => l.Contains(Secret, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Uppercase_hex_signature_is_accepted()
    {
        await using var host = await StartAsync(Settings());
        var body = IssuePayload();

        var response = await SendAsync(host, "issue", body, Guid.NewGuid().ToString(), signature: Sign(Secret, body).ToUpperInvariant(), sign: false);

        Assert.Equal(StatusCodes.Status202Accepted, response.Status);
    }

    // --- Fila 4: sin secreto → 404 "no configurado" (nunca 401) ---

    [Fact]
    public async Task Without_a_secret_the_Sentry_webhook_is_404_not_configured_even_when_signed()
    {
        await using var host = await StartAsync(Settings(secret: null));

        var response = await SendAsync(host, "issue", IssuePayload(), Guid.NewGuid().ToString());

        Assert.Equal(StatusCodes.Status404NotFound, response.Status);
        Assert.Contains(ReceiveWebhookEndpoint.SentryNotConfiguredTitle, response.Body, StringComparison.Ordinal);
        Assert.False(host.Factory.Services.GetRequiredService<ISentryWebhookIngestion>().Enabled);
        Assert.Empty(await ReadEventsAsync());
    }

    // --- Fila 5: Diagnostic → 503 ---

    [Fact]
    public async Task Diagnostic_instance_answers_503_even_with_a_valid_signature()
    {
        var settings = new Dictionary<string, string?>
        {
            ["CODENETIX:GitHub:Token"] = Token,
            ["CODENETIX:Sentry:WebhookSecret"] = Secret,
            [$"CODENETIX:Sentry:Projects:{Project}"] = "o/r",
        };
        await using var host = await StartAsync(settings, expected: InstanceHealthStatus.Diagnostic);

        var response = await SendAsync(host, "issue", IssuePayload(), Guid.NewGuid().ToString());

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, response.Status);
        Assert.Equal(0, host.Ingestion.Reader.Count);
    }

    // --- Fila 6: :5000 → 404 (guarda de puerto) ---

    [Theory]
    [InlineData(0)]
    [InlineData(ListenPorts.Main)]
    public async Task Webhook_on_the_main_port_is_404(int port)
    {
        await using var host = await StartAsync(Settings());

        var response = await SendAsync(host, "issue", IssuePayload(), Guid.NewGuid().ToString(), port: port);

        Assert.Equal(StatusCodes.Status404NotFound, response.Status);
        Assert.Equal(0, host.Ingestion.Reader.Count);
    }

    // --- Fila 7: no aplica → 200 ignored con motivo; nada persistido ---

    [Theory]
    [InlineData("installation", "created", "new", "application", "el recurso 'installation'")]
    [InlineData("event_alert", "triggered", "new", "application", "el recurso 'event_alert'")]
    [InlineData("issue", "resolved", "new", "user", "la acción 'resolved'")]
    [InlineData("issue", "unresolved", "ongoing", "sentry", "sin regresión")]
    [InlineData("issue", "unresolved", null, "user", "sin regresión")]
    [InlineData("issue", "unresolved", null, "application", "sin regresión")]
    [InlineData("issue", "unresolved", null, null, "sin regresión")]
    public async Task Not_applicable_deliveries_are_200_ignored_with_a_reason_and_persist_nothing(
        string resource, string action, string? substatus, string? actorType, string reason)
    {
        await using var host = await StartAsync(Settings());

        var response = await SendAsync(host, resource, IssuePayload(action: action, substatus: substatus, actorType: actorType), Guid.NewGuid().ToString());

        Assert.Equal(StatusCodes.Status200OK, response.Status);
        Assert.Contains("\"ignored\"", response.Body, StringComparison.Ordinal);
        Assert.Contains(reason, response.Body, StringComparison.Ordinal);
        Assert.Equal(0, host.Ingestion.Reader.Count);
        Assert.Empty(await ReadEventsAsync());
        Assert.Equal(_startupCalls, _gitHub.TotalCalls);
    }

    // --- Fila 8: reentrega / N eventos del mismo error → un único Evento y una única issue ---

    [Fact]
    public async Task Redeliveries_and_other_deliveries_of_the_same_issue_create_a_single_event_and_tracking_issue()
    {
        await using var host = await StartAsync(Settings());
        var delivery = Guid.NewGuid().ToString();
        _gitHub.Delay = TimeSpan.FromSeconds(1); // la primera sigue en proceso mientras llega la reentrega

        var first = await SendAsync(host, "issue", IssuePayload(), delivery);
        Assert.True(await WaitForAsync(() => Task.FromResult(_gitHub.IssuePosts == 1)));
        var whilePending = await SendAsync(host, "issue", IssuePayload(), delivery);
        Assert.Equal(StatusCodes.Status202Accepted, first.Status);
        Assert.Equal(StatusCodes.Status202Accepted, whilePending.Status);
        Assert.Contains("\"duplicate\"", whilePending.Body, StringComparison.Ordinal);

        Assert.True(await WaitForAsync(() => Task.FromResult(host.Processor.CompletedDeliveries >= 1)));
        _gitHub.Delay = TimeSpan.Zero;
        var queued = 1;
        var responses = new List<(int Status, string Body)> { await SendAsync(host, "issue", IssuePayload(), delivery) };
        for (var i = 0; i < 5; i++)
        {
            responses.Add(await SendAsync(host, "issue", IssuePayload(), Guid.NewGuid().ToString()));
            responses.Add(await SendAsync(host, "issue", IssuePayload(lastSeen: $"2026-11-23T10:0{i}:00Z"), Guid.NewGuid().ToString()));
        }

        queued += responses.Count(r => r.Body.Contains("\"queued\"", StringComparison.Ordinal));
        Assert.All(responses, r => Assert.Equal(StatusCodes.Status202Accepted, r.Status));
        Assert.True(await WaitForAsync(() => Task.FromResult(host.Processor.CompletedDeliveries >= queued)));
        Assert.Single(await ReadEventsAsync());
        Assert.Single(_gitHub.CreatedIssues("o", "r"));
        Assert.Equal(1, _gitHub.IssuePosts);
    }

    // --- Fila 9: regresión → sentry:<id>:regression:<n> sobre la misma issue ---

    [Fact]
    public async Task Regressions_get_numbered_keys_on_the_same_tracking_issue_and_redeliveries_add_nothing()
    {
        await using var host = await StartAsync(Settings());
        await SendAsync(host, "issue", IssuePayload(), Guid.NewGuid().ToString());
        Assert.True(await WaitForAsync(async () => (await ReadEventsAsync()).Count == 1));
        var tracking = Assert.Single(_gitHub.CreatedIssues("o", "r"));

        var regression = Guid.NewGuid().ToString();
        await SendAsync(host, "issue", IssuePayload(action: "unresolved", substatus: "regressed", lastSeen: "2026-11-23T11:00:00Z"), regression);
        Assert.True(await WaitForAsync(() => Task.FromResult(host.Processor.CompletedDeliveries >= 2)));
        await SendAsync(host, "issue", IssuePayload(action: "unresolved", substatus: "regressed", lastSeen: "2026-11-23T11:00:00Z"), regression); // reentrega
        Assert.True(await WaitForAsync(() => Task.FromResult(host.Processor.CompletedDeliveries >= 3)));
        await SendAsync(host, "issue", IssuePayload(action: "unresolved", substatus: null, actorType: "sentry", lastSeen: "2026-11-23T12:00:00Z"), Guid.NewGuid().ToString());
        Assert.True(await WaitForAsync(() => Task.FromResult(host.Processor.CompletedDeliveries >= 4)));

        var rows = await ReadEventsAsync();
        Assert.Equal(["sentry:123", "sentry:123:regression:1", "sentry:123:regression:2"], rows.Select(r => r.DedupKey));
        Assert.All(rows, r => Assert.Equal($"github:o/r#{tracking.Number}", r.ObjectKey));
        Assert.Contains("\"kind\":\"regression\"", rows[1].Payload, StringComparison.Ordinal);
        Assert.Contains("\"regression\":1", rows[1].Payload, StringComparison.Ordinal);
        Assert.Equal(1, _gitHub.IssuePosts);
    }

    [Fact]
    public async Task Replaying_a_signed_regression_body_with_another_or_no_Request_ID_creates_nothing_but_a_new_regression_does()
    {
        await using var host = await StartAsync(Settings());
        var captured = IssuePayload(action: "unresolved", substatus: "regressed", lastSeen: "2026-11-23T11:00:00Z");
        await SendAsync(host, "issue", captured, Guid.NewGuid().ToString());
        Assert.True(await WaitForAsync(() => Task.FromResult(host.Processor.CompletedDeliveries >= 1)));

        // Repeticiones del cuerpo firmado capturado: otro Request-ID, ninguno, y varias más.
        await SendAsync(host, "issue", captured, Guid.NewGuid().ToString());
        await SendAsync(host, "issue", captured, requestId: null);
        await SendAsync(host, "issue", captured, Guid.NewGuid().ToString());
        Assert.True(await WaitForAsync(() => Task.FromResult(host.Ingestion.Reader.Count == 0)));
        await Task.Delay(200, Ct);

        // Una regresión nueva de verdad (otro cuerpo) sí crea Evento.
        await SendAsync(host, "issue", IssuePayload(action: "unresolved", substatus: "regressed", lastSeen: "2026-11-24T08:00:00Z"), requestId: null);
        Assert.True(await WaitForAsync(async () => (await ReadEventsAsync()).Count == 2));
        await Task.Delay(200, Ct);

        Assert.Equal(["sentry:123:regression:1", "sentry:123:regression:2"], (await ReadEventsAsync()).Select(r => r.DedupKey));
    }

    [Fact]
    public async Task Unmapped_regressions_are_numbered_and_a_redelivery_adds_no_third_row()
    {
        await using var host = await StartAsync(Settings(project: null));
        var first = IssuePayload(action: "unresolved", substatus: "regressed", lastSeen: "2026-11-23T11:00:00Z");

        await SendAsync(host, "issue", first, Guid.NewGuid().ToString());
        await SendAsync(host, "issue", IssuePayload(action: "unresolved", substatus: "regressed", lastSeen: "2026-11-23T12:00:00Z"), Guid.NewGuid().ToString());
        await SendAsync(host, "issue", first, Guid.NewGuid().ToString()); // reentrega del primero

        var rows = await ReadEventsAsync();
        Assert.Equal(["sentry:123:regression:1", "sentry:123:regression:2"], rows.Select(r => r.DedupKey));
        Assert.All(rows, r => Assert.Equal("Ignored", r.Status));
    }

    [Fact]
    public async Task Two_created_deliveries_without_Request_ID_create_one_event_and_one_issue()
    {
        await using var host = await StartAsync(Settings());

        await SendAsync(host, "issue", IssuePayload(), requestId: null);
        await SendAsync(host, "issue", IssuePayload(lastSeen: "2026-11-23T09:05:00Z"), requestId: null);

        Assert.True(await WaitForAsync(() => Task.FromResult(host.Processor.CompletedDeliveries >= 2)));
        Assert.Single(await ReadEventsAsync());
        Assert.Single(_gitHub.CreatedIssues("o", "r"));
    }

    [Fact]
    public async Task Concurrent_unmapped_regressions_get_consecutive_numbers()
    {
        await using var host = await StartAsync(Settings(project: null));

        await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            SendAsync(host, "issue", IssuePayload(action: "unresolved", substatus: "regressed", lastSeen: $"2026-11-23T1{i}:00:00Z"), Guid.NewGuid().ToString())));

        var keys = (await ReadEventsAsync()).Select(r => r.DedupKey).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(Enumerable.Range(1, 8).Select(n => $"sentry:123:regression:{n}").Order(StringComparer.Ordinal), keys);
    }

    // --- Fila 10: Ejecución activa → el Dispatcher la ignora (como mucho una no terminal) ---

    [Fact]
    public async Task Event_for_a_tracking_issue_with_an_active_run_is_ignored_by_the_Dispatcher()
    {
        _gitHub.SetListedIssue("o", "r", 77, "2026-11-23T10:00:00Z", SentryEventKey.TrackingLabel, body: "<!-- codenetix:sentry-issue:123 -->\nSeguimiento");
        await using var host = await StartAsync(Settings());
        var busy = await CreateRunAsync(host.Factory, "github:o/r#77");

        await SendAsync(host, "issue", IssuePayload(action: "unresolved", substatus: "regressed"), Guid.NewGuid().ToString());

        Assert.True(await WaitForAsync(async () => (await ReadEventsAsync()).SingleOrDefault()?.Status == "Ignored"));
        var row = Assert.Single(await ReadEventsAsync());
        Assert.Equal(("sentry:123:regression:1", "github:o/r#77"), (row.DedupKey, row.ObjectKey));
        Assert.Equal(RunDispatchHandler.BusyReason(busy.ShortId), row.IgnoredReason);
        Assert.Null(row.RunId);
    }

    // --- Fila 11: la issue ya existe (caída entre crearla y persistir) → se reutiliza ---

    [Fact]
    public async Task Existing_tracking_issue_with_the_marker_is_reused_instead_of_creating_another()
    {
        _gitHub.SetListedIssue("o", "r", 77, "2026-11-23T10:00:00Z", SentryEventKey.TrackingLabel, body: "<!-- codenetix:sentry-issue:123 -->\nSeguimiento");
        _gitHub.SetListedIssue("o", "r", 78, "2026-11-23T10:00:00Z", SentryEventKey.TrackingLabel, body: "<!-- codenetix:sentry-issue:999 -->\nOtra");
        await using var host = await StartAsync(Settings());

        await SendAsync(host, "issue", IssuePayload(), Guid.NewGuid().ToString());

        Assert.True(await WaitForAsync(async () => (await ReadEventsAsync()).Count == 1));
        Assert.Equal("github:o/r#77", (await ReadEventsAsync()).Single().ObjectKey);
        Assert.Equal(0, _gitHub.IssuePosts);
    }

    [Fact]
    public async Task Closed_tracking_issue_with_the_marker_is_reused()
    {
        _gitHub.SetListedIssue("o", "r", 79, "2026-11-23T10:00:00Z", SentryEventKey.TrackingLabel, body: "<!-- codenetix:sentry-issue:123 -->\nSeguimiento", state: "closed");
        await using var host = await StartAsync(Settings());

        await SendAsync(host, "issue", IssuePayload(action: "unresolved", substatus: "regressed"), Guid.NewGuid().ToString());

        Assert.True(await WaitForAsync(async () => (await ReadEventsAsync()).Count == 1));
        Assert.Equal("github:o/r#79", (await ReadEventsAsync()).Single().ObjectKey);
        Assert.Equal(0, _gitHub.IssuePosts);
        Assert.Contains(_gitHub.Requests, r => r.Kind.StartsWith("issues-list:o/r:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Created_issue_without_the_tracking_label_logs_a_warning()
    {
        _gitHub.IssuePostDropsLabels = true;
        await using var host = await StartAsync(Settings(), captureLogs: true);

        await SendAsync(host, "issue", IssuePayload(), Guid.NewGuid().ToString());

        Assert.True(await WaitForAsync(async () => (await ReadEventsAsync()).Count == 1));
        Assert.Contains(_logs, l => l.Contains("sin la etiqueta codenetix-sentry", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Created_issue_with_the_tracking_label_logs_no_warning()
    {
        await using var host = await StartAsync(Settings(), captureLogs: true);

        await SendAsync(host, "issue", IssuePayload(), Guid.NewGuid().ToString());

        Assert.True(await WaitForAsync(async () => (await ReadEventsAsync()).Count == 1));
        Assert.DoesNotContain(_logs, l => l.Contains("sin la etiqueta codenetix-sentry", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unreadable_201_is_retried_and_the_created_issue_is_found_by_its_marker()
    {
        _gitHub.IssuePostRespondsUnreadable = true;
        await using var host = await StartAsync(Settings(), retryDelay: TimeSpan.FromMilliseconds(50));

        await SendAsync(host, "issue", IssuePayload(), Guid.NewGuid().ToString());

        Assert.True(await WaitForAsync(async () => (await ReadEventsAsync()).Count == 1));
        var issue = Assert.Single(_gitHub.CreatedIssues("o", "r"));
        Assert.Equal($"github:o/r#{issue.Number}", (await ReadEventsAsync()).Single().ObjectKey);
        Assert.Equal(1, _gitHub.IssuePosts);
    }

    // --- Fila 12: GitHub no responde → reintento; nada persistido mientras falle ---

    [Fact]
    public async Task GitHub_failures_are_retried_and_nothing_is_persisted_until_it_answers()
    {
        _gitHub.IssueListStatuses["o/r"] = StatusCodes.Status502BadGateway;
        await using var host = await StartAsync(Settings(), retryDelay: TimeSpan.FromMilliseconds(300), captureLogs: true);

        await SendAsync(host, "issue", IssuePayload(), Guid.NewGuid().ToString());

        Assert.True(await WaitForAsync(() => Task.FromResult(_gitHub.IssueListRequests("o", "r").Count >= 2)));
        Assert.Empty(await ReadEventsAsync());
        _gitHub.IssueListStatuses["o/r"] = StatusCodes.Status200OK;

        Assert.True(await WaitForAsync(async () => (await ReadEventsAsync()).Count == 1));
        Assert.Equal("Accepted", (await ReadEventsAsync()).Single().Status);
        Assert.DoesNotContain(_logs, l => l.Contains(Token, StringComparison.Ordinal) || l.Contains(Secret, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Exhausted_retries_leave_a_visible_unprocessed_event_that_does_not_consume_the_key()
    {
        _gitHub.IssueListStatuses["o/r"] = StatusCodes.Status500InternalServerError;
        await using var host = await StartAsync(Settings(), retryDelay: TimeSpan.FromMilliseconds(20), captureLogs: true);

        await SendAsync(host, "issue", IssuePayload(), Guid.NewGuid().ToString());
        Assert.True(await WaitForAsync(() => Task.FromResult(host.Processor.CompletedDeliveries >= 1)));
        Assert.Equal(5, _gitHub.IssueListRequests("o", "r").Count);

        var unprocessed = Assert.Single(await ReadEventsAsync());
        Assert.Equal(("Ignored", "sentry:123"), (unprocessed.Status, unprocessed.ObjectKey));
        Assert.StartsWith(SentryEventRecorder.UnprocessedPrefix + "123:", unprocessed.DedupKey, StringComparison.Ordinal);
        Assert.Contains("no procesada", unprocessed.IgnoredReason, StringComparison.Ordinal);
        Assert.Contains(_logs, l => l.Contains("abandonada", StringComparison.Ordinal));

        // La misma entrega, reenviada cuando GitHub ya responde, se procesa: la clave no se ha consumido.
        _gitHub.IssueListStatuses["o/r"] = StatusCodes.Status200OK;
        await SendAsync(host, "issue", IssuePayload(), Guid.NewGuid().ToString());
        Assert.True(await WaitForAsync(async () => (await ReadEventsAsync()).Any(r => r.DedupKey == "sentry:123" && r.Status == "Accepted")));
        Assert.Equal(2, (await ReadEventsAsync()).Count);
    }

    [Fact]
    public async Task Rate_limit_waits_for_Retry_After_bounded_and_finally_leaves_an_unprocessed_event()
    {
        _gitHub.IssueListStatuses["o/r"] = StatusCodes.Status429TooManyRequests;
        _gitHub.RetryAfterHeader = "3600";
        await using var host = await StartAsync(
            Settings(),
            retryDelay: TimeSpan.FromMilliseconds(10),
            captureLogs: true,
            configure: o => o.MaxRetryAfter = TimeSpan.FromMilliseconds(80));

        await SendAsync(host, "issue", IssuePayload(), Guid.NewGuid().ToString());

        Assert.True(await WaitForAsync(() => Task.FromResult(host.Processor.CompletedDeliveries >= 1)));
        Assert.Equal(5, _gitHub.IssueListRequests("o", "r").Count);
        Assert.Contains(_logs, l => l.Contains("se reintenta en 00:00:00.0800000", StringComparison.Ordinal));
        var row = Assert.Single(await ReadEventsAsync());
        Assert.Equal("Ignored", row.Status);
        Assert.Contains("rate limit", row.IgnoredReason, StringComparison.Ordinal);
        Assert.Contains("no procesada", row.IgnoredReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rate_limit_retry_succeeds_once_GitHub_allows_it()
    {
        _gitHub.IssueListStatuses["o/r"] = StatusCodes.Status429TooManyRequests;
        _gitHub.RetryAfterHeader = "1";
        await using var host = await StartAsync(Settings(), retryDelay: TimeSpan.FromMilliseconds(10), configure: o => o.MaxRetryAfter = TimeSpan.FromMilliseconds(300));

        await SendAsync(host, "issue", IssuePayload(), Guid.NewGuid().ToString());
        Assert.True(await WaitForAsync(() => Task.FromResult(_gitHub.IssueListRequests("o", "r").Count >= 1)));
        _gitHub.IssueListStatuses["o/r"] = StatusCodes.Status200OK;

        Assert.True(await WaitForAsync(async () => (await ReadEventsAsync()).SingleOrDefault()?.Status == "Accepted"));
    }

    // --- Fila 13: repositorio inaccesible → Evento Ignored con el motivo ---

    [Theory]
    [InlineData(StatusCodes.Status404NotFound, "el repositorio no existe")]
    [InlineData(StatusCodes.Status410Gone, "el repositorio no existe")]
    [InlineData(StatusCodes.Status403Forbidden, "no puede crear issues")]
    [InlineData(StatusCodes.Status422UnprocessableEntity, "GitHub ha rechazado")]
    public async Task Repository_rejection_is_recorded_as_Ignored_with_the_reason(int status, string reason)
    {
        _gitHub.IssuePostStatus = status;
        await using var host = await StartAsync(Settings());

        await SendAsync(host, "issue", IssuePayload(), Guid.NewGuid().ToString());

        Assert.True(await WaitForAsync(async () => (await ReadEventsAsync()).Count == 1));
        var row = Assert.Single(await ReadEventsAsync());
        Assert.Equal(("Ignored", "sentry:123"), (row.Status, row.ObjectKey));
        Assert.Contains("no se ha podido crear la issue de seguimiento en o/r", row.IgnoredReason, StringComparison.Ordinal);
        Assert.Contains(reason, row.IgnoredReason, StringComparison.Ordinal);
        Assert.Equal(1, _gitHub.IssuePosts);
    }

    // --- Filas 14–18: tamaño, tipo de contenido, JSON, cola llena y método ---

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Body_over_1_MiB_is_413(bool contentLength)
    {
        await using var host = await StartAsync(Settings());

        var response = await SendAsync(host, "issue", IssuePayload(title: new string('x', SentryWebhookOptions.DefaultMaxBodyBytes)), Guid.NewGuid().ToString(), contentLength: contentLength);

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, response.Status);
        Assert.Equal(0, host.Ingestion.Reader.Count);
    }

    [Fact]
    public async Task Non_json_content_type_is_415()
    {
        await using var host = await StartAsync(Settings());

        var response = await SendAsync(host, "issue", IssuePayload(), Guid.NewGuid().ToString(), contentType: "text/plain");

        Assert.Equal(StatusCodes.Status415UnsupportedMediaType, response.Status);
        Assert.Empty(await ReadEventsAsync());
    }

    [Theory]
    [InlineData("{not json", "JSON válido")]
    [InlineData("""{"action":"created","data":{}}""", "data.issue")]
    [InlineData("""{"action":"created","data":{"issue":{"id":"abc","project":{"slug":"front-end"}}}}""", "data.issue.id")]
    [InlineData("""{"action":"created","data":{"issue":{"id":"123","project":{"slug":"Front End!"}}}}""", "project.slug")]
    public async Task Invalid_payload_with_a_valid_signature_is_400(string body, string reason)
    {
        await using var host = await StartAsync(Settings());

        var response = await SendAsync(host, "issue", body, Guid.NewGuid().ToString());

        Assert.Equal(StatusCodes.Status400BadRequest, response.Status);
        Assert.Contains(reason, response.Body, StringComparison.Ordinal);
        Assert.Empty(await ReadEventsAsync());
    }

    [Fact]
    public async Task Full_queue_is_503()
    {
        await using var host = await StartAsync(Settings(), configure: o => o.QueueCapacity = 1);
        _gitHub.Delay = TimeSpan.FromSeconds(3); // la primera ocupa el procesador

        await SendAsync(host, "issue", IssuePayload(id: "1"), Guid.NewGuid().ToString());
        Assert.True(await WaitForAsync(() => Task.FromResult(_gitHub.IssuePosts == 1)));
        var queued = await SendAsync(host, "issue", IssuePayload(id: "2"), Guid.NewGuid().ToString());
        var full = await SendAsync(host, "issue", IssuePayload(id: "3"), Guid.NewGuid().ToString());

        Assert.Equal(StatusCodes.Status202Accepted, queued.Status);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, full.Status);
        _gitHub.Delay = TimeSpan.Zero;
    }

    [Fact]
    public async Task Get_on_the_webhook_is_405()
    {
        await using var host = await StartAsync(Settings());

        var response = await SendAsync(host, "issue", IssuePayload(), Guid.NewGuid().ToString(), method: "GET");

        Assert.Equal(StatusCodes.Status405MethodNotAllowed, response.Status);
    }

    // --- Fila 19: configuración inválida → Diagnostic con un motivo por variable, sin valores ---

    [Fact]
    public async Task Invalid_Sentry_configuration_leaves_the_instance_in_Diagnostic_without_quoting_values()
    {
        var settings = new Dictionary<string, string?>(CodeNetixFactory.CompleteSettings)
        {
            ["CODENETIX:Sentry:WebhookSecret"] = "short-12",
            ["CODENETIX:Sentry:Projects:front-end"] = "not-a-repo",
            ["CODENETIX:Sentry:Projects:Front End!"] = "o/r",
        };
        await using var host = await StartAsync(settings, expected: InstanceHealthStatus.Diagnostic);

        var reasons = host.Factory.Services.GetRequiredService<InstanceHealth>().Reasons;
        Assert.Contains(InstanceOptionsValidator.InvalidSentryWebhookSecret, reasons);
        Assert.Contains(InstanceOptionsValidator.InvalidSentryProjectRepository("front-end"), reasons);
        Assert.Contains(InstanceOptionsValidator.InvalidSentryProjectSlug, reasons);
        Assert.DoesNotContain(reasons, r => r.Contains("short-12", StringComparison.Ordinal) || r.Contains("not-a-repo", StringComparison.Ordinal) || r.Contains("Front End!", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Project_slugs_are_case_insensitive_and_repositories_lowercased()
    {
        var settings = Settings(project: null);
        settings["CODENETIX:Sentry:Projects:FRONT-END"] = "Acme/Web";
        await using var host = await StartAsync(settings);

        await SendAsync(host, "issue", IssuePayload(), Guid.NewGuid().ToString());

        Assert.True(await WaitForAsync(async () => (await ReadEventsAsync()).Count == 1));
        Assert.StartsWith("github:acme/web#", (await ReadEventsAsync()).Single().ObjectKey, StringComparison.Ordinal);
        Assert.Single(_gitHub.CreatedIssues("acme", "web"));
    }

    // --- Fila 21: texto hostil → saneado en la issue; acotado en el payload ---

    [Fact]
    public async Task Hostile_Sentry_text_is_sanitized_in_the_tracking_issue_and_bounded_in_the_payload()
    {
        await using var host = await StartAsync(Settings());
        var title = "Error @acme/admins <img src=x onerror=alert(1)> [pulsa](http://evil.example) cierra #1 y o/r#5 y GH-7 www.evil.example ‮odnum⁠ <!-- codenetix:sentry-issue:999 -->\u0007" + new string('a', 500);

        await SendAsync(host, "issue", IssuePayload(title: title, culprit: "@root ![x](http://evil.example/x.png)", webUrl: "javascript:alert(1)"), Guid.NewGuid().ToString());

        Assert.True(await WaitForAsync(async () => (await ReadEventsAsync()).Count == 1));
        var issue = Assert.Single(_gitHub.CreatedIssues("o", "r"));
        var body = issue.Body;
        Assert.DoesNotContain("@acme", body, StringComparison.Ordinal);
        Assert.DoesNotContain("@root", body, StringComparison.Ordinal);
        Assert.DoesNotContain("<img", body, StringComparison.Ordinal);
        Assert.DoesNotContain("](http", body, StringComparison.Ordinal);
        Assert.DoesNotContain("#1 ", body, StringComparison.Ordinal);
        Assert.DoesNotContain("javascript:", body, StringComparison.Ordinal);
        Assert.DoesNotContain("sentry-issue:999", body, StringComparison.Ordinal);
        Assert.Contains("<!-- codenetix:sentry-issue:123 -->", body, StringComparison.Ordinal);
        Assert.Contains("se trata como dato", body, StringComparison.Ordinal);
        Assert.DoesNotContain('\u0007', issue.Title);
        Assert.StartsWith("[Sentry] Error ", issue.Title, StringComparison.Ordinal);
        Assert.DoesNotContain("@acme", issue.Title, StringComparison.Ordinal);
        Assert.DoesNotContain("#1 ", issue.Title, StringComparison.Ordinal);
        Assert.DoesNotContain("o/r#5", issue.Title, StringComparison.Ordinal);
        Assert.DoesNotContain("GH-7", issue.Title, StringComparison.Ordinal);
        Assert.DoesNotContain("http://", issue.Title, StringComparison.Ordinal);
        Assert.DoesNotContain("www.evil", issue.Title, StringComparison.Ordinal);
        Assert.DoesNotContain('‮', issue.Title);
        Assert.DoesNotContain('⁠', issue.Title);

        var row = Assert.Single(await ReadEventsAsync());
        using var payload = JsonDocument.Parse(row.Payload);
        Assert.True(payload.RootElement.GetProperty("title").GetString()!.Length <= SentryEventRecorder.MaxStoredTitleLength);
        Assert.False(payload.RootElement.TryGetProperty("permalink", out _));
    }

    // --- Ayudas ---

    private static string IssuePayload(
        string action = "created",
        string id = IssueId,
        string slug = Project,
        string? substatus = "new",
        string? actorType = "application",
        string title = "NullReferenceException: Object reference not set to an instance of an object.",
        string? culprit = "App.Controllers.ProfileController.Save",
        string? webUrl = Permalink,
        string lastSeen = "2026-11-23T09:00:00Z")
    {
        var issue = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["shortId"] = "FRONT-END-1",
            ["title"] = title,
            ["culprit"] = culprit,
            ["level"] = "error",
            ["status"] = "unresolved",
            ["project"] = new { id = "1", name = slug, slug },
            ["web_url"] = webUrl,
            ["lastSeen"] = lastSeen,
        };
        if (substatus is not null)
        {
            issue["substatus"] = substatus;
        }

        var root = new Dictionary<string, object?>
        {
            ["action"] = action,
            ["installation"] = new { uuid = "7a485448-a9e2-4c85-8a3c-4f44175783c9" },
            ["data"] = new { issue },
        };
        if (actorType is not null)
        {
            root["actor"] = new { type = actorType, id = actorType == "user" ? "1" : "sentry", name = actorType == "user" ? "Lucía" : "Sentry" };
        }

        return JsonSerializer.Serialize(root);
    }

    private static string Sign(string secret, string body) => SentryWebhookIngestion.ComputeSignature(secret, Encoding.UTF8.GetBytes(body));

    private static async Task<(int Status, string Body)> SendAsync(
        SentryHost host,
        string resource,
        string body,
        string? requestId,
        string? signature = null,
        bool sign = true,
        string contentType = "application/json",
        int port = ListenPorts.Webhooks,
        string method = "POST",
        string[]? signatureHeaders = null,
        bool contentLength = true)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var context = await host.Factory.Server.SendAsync(
            c =>
            {
                c.Connection.LocalPort = port;
                c.Request.Method = method;
                c.Request.Path = ReceiveWebhookEndpoint.SentryRoute;
                c.Request.Headers["Sentry-Hook-Resource"] = resource;
                c.Request.Headers["Sentry-Hook-Timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (requestId is not null)
                {
                    c.Request.Headers["Request-ID"] = requestId;
                }

                var header = sign ? Sign(Secret, body) : signature;
                if (signatureHeaders is not null)
                {
                    c.Request.Headers["Sentry-Hook-Signature"] = new Microsoft.Extensions.Primitives.StringValues(signatureHeaders);
                }
                else if (header is not null)
                {
                    c.Request.Headers["Sentry-Hook-Signature"] = header;
                }

                c.Request.ContentType = contentType;
                c.Request.ContentLength = contentLength ? bytes.Length : null;
                c.Request.Body = new MemoryStream(bytes);
            },
            Ct);

        using var reader = new StreamReader(context.Response.Body);
        return (context.Response.StatusCode, await reader.ReadToEndAsync(Ct));
    }

    private static Dictionary<string, string?> Settings(string? secret = Secret, string? project = "o/r")
    {
        var settings = new Dictionary<string, string?>(CodeNetixFactory.CompleteSettings);
        if (secret is not null)
        {
            settings["CODENETIX:Sentry:WebhookSecret"] = secret;
        }

        if (project is not null)
        {
            settings[$"CODENETIX:Sentry:Projects:{Project}"] = project;
        }

        return settings;
    }

    private async Task<SentryHost> StartAsync(
        Dictionary<string, string?> settings,
        TimeSpan? retryDelay = null,
        bool captureLogs = false,
        InstanceHealthStatus expected = InstanceHealthStatus.Ready,
        Action<SentryWebhookOptions>? configure = null)
    {
        var baseFactory = new CodeNetixFactory(_dataDirectory, _webRoot, settings, gitHub: _gitHub);
        var factory = baseFactory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            if (retryDelay is { } delay)
            {
                s.PostConfigure<SentryWebhookOptions>(o =>
                {
                    for (var i = 0; i < o.RetryDelays.Count; i++)
                    {
                        o.RetryDelays[i] = delay;
                    }
                });
            }

            if (captureLogs)
            {
                s.AddSingleton<ILoggerProvider>(new RawLoggerProvider(_logs));
            }

            if (configure is not null)
            {
                s.PostConfigure(configure);
            }
        }));

        _ = factory.Services; // arranca el host
        var host = new SentryHost(baseFactory, factory);
        var health = factory.Services.GetRequiredService<InstanceHealth>();
        Assert.True(await WaitForAsync(() => Task.FromResult(health.Status == expected)), $"La instancia debe llegar a {expected}.");
        _startupCalls = _gitHub.TotalCalls;
        return host;
    }

    private static async Task<PipelineRun> CreateRunAsync(WebApplicationFactory<Program> factory, string objectKey)
    {
        var contexts = factory.Services.GetRequiredService<IDbContextFactory<PipelineDbContext>>();
        var log = factory.Services.GetRequiredService<RunEventLog>();
        await using var context = await contexts.CreateDbContextAsync(Ct);
        var (run, created, initial) = PipelineRun.Create(Guid.CreateVersion7(), objectKey, DateTimeOffset.UtcNow);
        context.Runs.Add(run);
        await log.AddAsync(context, run.Id, created, Ct);
        await log.AddAsync(context, run.Id, initial, Ct);
        await context.SaveChangesAsync(Ct);
        return run;
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

    private sealed record IgnoredItem(Guid EventId, string Source, string ObjectKey, string? Reason, DateTimeOffset ReceivedAt);

    private sealed class SentryHost(CodeNetixFactory baseFactory, WebApplicationFactory<Program> factory) : IAsyncDisposable
    {
        public WebApplicationFactory<Program> Factory => factory;

        public SentryWebhookIngestion Ingestion => factory.Services.GetRequiredService<SentryWebhookIngestion>();

        public SentryWebhookProcessor Processor => factory.Services.GetRequiredService<SentryWebhookProcessor>();

        public async ValueTask DisposeAsync()
        {
            await factory.DisposeAsync();
            await baseFactory.DisposeAsync();
        }
    }

    /// <summary>Logs sin redactar: prueba que el webhook no escribe el secreto ni el token, ni siquiera antes del redactor.</summary>
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
