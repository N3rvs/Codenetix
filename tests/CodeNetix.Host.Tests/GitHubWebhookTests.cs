using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CodeNetix.Contracts;
using CodeNetix.Host.Configuration;
using CodeNetix.Host.Features.ReceiveWebhook;
using CodeNetix.Host.Features.SubmitManualEvent;
using CodeNetix.Ingestion.Features.PollGitHub;
using CodeNetix.Ingestion.Features.ReceiveGitHubWebhook;
using CodeNetix.Ingestion.Features.SubmitManualEvent;
using CodeNetix.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CodeNetix.Host.Tests;

/// <summary>
/// Historia 7.2 (FR-7, FR-26, AD-4, AD-17, AD-18): <c>POST :5001/webhooks/github</c> en <c>serve</c> contra un GitHub falso.
/// Las peticiones van por <c>TestServer</c> con <c>LocalPort</c> = 5001 (nunca se enlaza :5000/:5001) y con firmas HMAC
/// SHA-256 reales. Una prueba por fila de la matriz de la spec.
/// </summary>
public sealed class GitHubWebhookTests : IAsyncLifetime
{
    private const string Secret = "webhook-secret-0123456789abcdef";
    private const string Alice = "alice";

    /// <summary>Momento del etiquetado: el mismo en el evento `labeled` falso y en `issue.updated_at` del payload.</summary>
    private static readonly DateTimeOffset LabeledAt = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    private static readonly string Token = CodeNetixFactory.CompleteSettings["CODENETIX:GitHub:Token"]!;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "codenetix-webhook-" + Guid.NewGuid().ToString("N"));
    private readonly string _dataDirectory;
    private readonly string _webRoot;
    private readonly ConcurrentQueue<string> _logs = new();
    private FakeGitHub _gitHub = null!;
    private int _startupCalls;

    public GitHubWebhookTests()
    {
        _dataDirectory = Path.Combine(_root, "data");
        _webRoot = Path.Combine(_root, "wwwroot");
        Directory.CreateDirectory(_webRoot);
        File.WriteAllText(Path.Combine(_webRoot, "index.html"), "<!doctype html><html><body>Panel</body></html>");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string DatabasePath => Path.Combine(_dataDirectory, PersistenceOptions.DatabaseFileName);

    public async ValueTask InitializeAsync() => _gitHub = await FakeGitHub.StartAsync(Ct);

    // --- Fila 1: issue etiquetada → 202 en < 1 s y, después, Evento Accepted con la clave del sondeo ---

    [Fact]
    public async Task Labeled_issue_is_acknowledged_in_under_a_second_and_becomes_an_Accepted_event_with_the_polling_key()
    {
        _gitHub.AddIssueEvent("o", "r", 12, 899, "labeled", "bug", Alice, LabeledAt);
        _gitHub.AddIssueEvent("o", "r", 12, 900, "labeled", "codenetix", Alice, LabeledAt);
        await using var host = await StartAsync(Settings());
        _gitHub.Delay = TimeSpan.FromSeconds(2); // permiso lento: la respuesta no lo espera

        var delivery = Guid.NewGuid().ToString();
        var watch = Stopwatch.StartNew();
        var response = await SendAsync(host, "issues", LabeledPayload(), delivery);
        watch.Stop();

        Assert.Equal(StatusCodes.Status202Accepted, response.Status);
        Assert.Contains("\"queued\"", response.Body, StringComparison.Ordinal);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"El webhook tardó {watch.Elapsed} en responder.");

        Assert.True(await WaitForAsync(async () => (await ReadEventsAsync()).Count == 1), "El procesado asíncrono debe crear el Evento.");
        var row = Assert.Single(await ReadEventsAsync());
        Assert.Equal(("GitHub", "gh:o/r#12:labeled:900", "github:o/r#12"), (row.Source, row.DedupKey, row.ObjectKey));
        Assert.Equal((Alice, "Accepted", (string?)null), (row.Actor, row.Status, row.IgnoredReason));
        Assert.Contains($"\"deliveryId\":\"{delivery}\"", row.Payload, StringComparison.Ordinal);
        Assert.Contains("\"labelEventId\":900", row.Payload, StringComparison.Ordinal);
        Assert.DoesNotContain("sender", row.Payload, StringComparison.Ordinal);
        Assert.Contains(_gitHub.Requests, r => r.Kind == $"permission:o/r:{Alice}");

        _gitHub.Delay = TimeSpan.Zero;
        Assert.True(await WaitForAsync(async () => (await ReadEventsAsync()).Single().RunId is not null), "El Dispatcher debe crear la Ejecución.");
    }

    // --- Fila 2: firma inválida → 401 sin detalle, nada procesado ---

    [Theory]
    [InlineData("missing")]
    [InlineData("sha1")]
    [InlineData("short")]
    [InlineData("not-hex")]
    [InlineData("other-secret")]
    [InlineData("tampered-body")]
    [InlineData("uppercase-prefix")]
    [InlineData("repeated")]
    public async Task Invalid_signature_is_401_without_details_and_nothing_is_processed(string variant)
    {
        _gitHub.AddIssueEvent("o", "r", 12, 900, "labeled", "codenetix", Alice, LabeledAt);
        await using var host = await StartAsync(Settings(), captureLogs: true);
        var body = LabeledPayload();
        var valid = Sign(Secret, body);
        var signature = variant switch
        {
            "missing" => null,
            "sha1" => "sha1=" + valid["sha256=".Length..][..40],
            "short" => valid[..^2],
            "not-hex" => "sha256=" + new string('z', 64),
            "other-secret" => Sign("another-secret-0123456789", body),
            "uppercase-prefix" => "SHA256=" + valid["sha256=".Length..],
            _ => valid,
        };
        string[]? repeated = variant == "repeated" ? [valid, valid] : null;
        var sent = variant == "tampered-body" ? body.Replace("\"number\":12", "\"number\":13", StringComparison.Ordinal) : body;

        var response = await SendAsync(host, "issues", sent, Guid.NewGuid().ToString(), signature: signature, sign: false, signatureHeaders: repeated);

        Assert.Equal(StatusCodes.Status401Unauthorized, response.Status);
        Assert.Contains(ReceiveWebhookEndpoint.InvalidSignatureTitle, response.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"detail\"", response.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, response.Body, StringComparison.Ordinal);
        Assert.Equal(0, host.Ingestion.Reader.Count);
        Assert.Equal(0, _gitHub.IssueEventCalls("o", "r", 12));
        Assert.Empty(await ReadEventsAsync());
        Assert.DoesNotContain(_logs, l => l.Contains(Secret, StringComparison.Ordinal));
    }

    // --- Fila 3: sin secreto → 404 "no configurado" (nunca 401) ---

    [Fact]
    public async Task Without_a_secret_the_GitHub_webhook_is_404_not_configured_even_when_signed()
    {
        await using var host = await StartAsync(Settings(secret: null));

        var response = await SendAsync(host, "issues", LabeledPayload(), Guid.NewGuid().ToString());

        Assert.Equal(StatusCodes.Status404NotFound, response.Status);
        Assert.Contains(ReceiveWebhookEndpoint.GitHubNotConfiguredTitle, response.Body, StringComparison.Ordinal);
        Assert.False(host.Factory.Services.GetRequiredService<IGitHubWebhookIngestion>().Enabled);
    }

    // --- Fila 4: Diagnostic → 503 ---

    [Fact]
    public async Task Diagnostic_instance_answers_503_even_with_a_valid_signature()
    {
        var settings = new Dictionary<string, string?> { ["CODENETIX:GitHub:Token"] = Token, ["CODENETIX:GitHub:WebhookSecret"] = Secret };
        await using var host = await StartAsync(settings, expected: InstanceHealthStatus.Diagnostic);

        var response = await SendAsync(host, "issues", LabeledPayload(), Guid.NewGuid().ToString());

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, response.Status);
        Assert.Equal(0, host.Ingestion.Reader.Count);
    }

    // --- Fila 5: :5000 → 404 (guarda de puerto) ---

    [Theory]
    [InlineData(0)]
    [InlineData(ListenPorts.Main)]
    public async Task Webhook_on_the_main_port_is_404(int port)
    {
        await using var host = await StartAsync(Settings());

        var response = await SendAsync(host, "issues", LabeledPayload(), Guid.NewGuid().ToString(), port: port);

        Assert.Equal(StatusCodes.Status404NotFound, response.Status);
        Assert.Equal(0, host.Ingestion.Reader.Count);
    }

    // --- Fila 6: ping → 200 pong ---

    [Fact]
    public async Task Signed_ping_answers_200_pong()
    {
        await using var host = await StartAsync(Settings());

        var response = await SendAsync(host, "ping", """{"zen":"Keep it logically awesome.","hook_id":1}""", Guid.NewGuid().ToString());

        Assert.Equal(StatusCodes.Status200OK, response.Status);
        Assert.Contains("\"pong\"", response.Body, StringComparison.Ordinal);
        Assert.Equal(_startupCalls, _gitHub.TotalCalls);
    }

    // --- Fila 7: no aplica → 200 ignored con motivo; nada persistido ---

    [Theory]
    [InlineData("push", "labeled", "codenetix", false, "open", "el evento 'push'")]
    [InlineData("issues", "opened", "codenetix", false, "open", "la acción 'opened'")]
    [InlineData("issues", "labeled", "bug", false, "open", "la etiqueta 'bug'")]
    [InlineData("issues", "labeled", "codenetix", true, "open", "pull request")]
    [InlineData("issues", "labeled", "codenetix", false, "closed", "cerrada")]
    public async Task Not_applicable_deliveries_are_200_ignored_with_a_reason_and_persist_nothing(
        string eventName, string action, string label, bool pullRequest, string state, string reason)
    {
        await using var host = await StartAsync(Settings());

        var response = await SendAsync(host, eventName, LabeledPayload(action: action, label: label, pullRequest: pullRequest, state: state), Guid.NewGuid().ToString());

        Assert.Equal(StatusCodes.Status200OK, response.Status);
        Assert.Contains("\"ignored\"", response.Body, StringComparison.Ordinal);
        Assert.Contains(reason, response.Body, StringComparison.Ordinal);
        Assert.Equal(0, host.Ingestion.Reader.Count);
        Assert.Equal(0, _gitHub.IssueEventCalls("o", "r", 12));
        Assert.Empty(await ReadEventsAsync());
    }

    // --- Fila 8: repositorio fuera de la lista → ignored, sin llamadas ---

    [Fact]
    public async Task Repository_outside_the_allowlist_is_ignored_and_an_allowed_one_is_processed()
    {
        _gitHub.AddIssueEvent("o", "s", 7, 700, "labeled", "codenetix", Alice, LabeledAt);
        var settings = Settings();
        settings["CODENETIX:GitHub:WebhookRepositories"] = "O/S, acme/web";
        await using var host = await StartAsync(settings);

        var ignored = await SendAsync(host, "issues", LabeledPayload(repository: "o/r"), Guid.NewGuid().ToString());
        var queued = await SendAsync(host, "issues", LabeledPayload(repository: "O/S", number: 7), Guid.NewGuid().ToString());

        Assert.Equal(StatusCodes.Status200OK, ignored.Status);
        Assert.Contains("CODENETIX__GitHub__WebhookRepositories", ignored.Body, StringComparison.Ordinal);
        Assert.Equal(StatusCodes.Status202Accepted, queued.Status);
        Assert.True(await WaitForAsync(() => Task.FromResult(host.Processor.CompletedDeliveries >= 1)));
        var row = Assert.Single(await ReadEventsAsync());
        Assert.Equal("gh:o/s#7:labeled:700", row.DedupKey);
        Assert.Equal(0, _gitHub.IssueEventCalls("o", "r", 12));
    }

    // --- Fila 9: sin permiso → Ignored "no autorizado" ---

    [Fact]
    public async Task Labeler_without_write_access_is_ignored_as_not_authorized()
    {
        _gitHub.PermissionsByLogin["mallory"] = "read";
        _gitHub.AddIssueEvent("o", "r", 12, 900, "labeled", "codenetix", "mallory", LabeledAt);
        await using var host = await StartAsync(Settings());

        await SendAsync(host, "issues", LabeledPayload(sender: "mallory"), Guid.NewGuid().ToString());

        Assert.True(await WaitForAsync(() => Task.FromResult(host.Processor.CompletedDeliveries >= 1)));
        var row = Assert.Single(await ReadEventsAsync());
        Assert.Equal(("mallory", "Ignored", ManualEventIngestion.UnauthorizedReason), (row.Actor, row.Status, row.IgnoredReason));
        Assert.Null(row.RunId);
    }

    // --- Fila 10: webhook + sondeo del mismo etiquetado → un único Evento, en cualquier orden ---

    [Fact]
    public async Task Webhook_then_poll_of_the_same_labeling_create_a_single_event()
    {
        _gitHub.AddIssueEvent("o", "r", 12, 900, "labeled", "codenetix", Alice, LabeledAt);
        await using var host = await StartAsync(Settings(polling: "o/r"));
        Assert.True(await WaitForAsync(() => Task.FromResult(host.Poller.CompletedCycles >= 1)));

        await SendAsync(host, "issues", LabeledPayload(), Guid.NewGuid().ToString());
        Assert.True(await WaitForAsync(() => Task.FromResult(host.Processor.CompletedDeliveries >= 1)));
        var permissionCalls = _gitHub.PermissionCalls;

        _gitHub.SetListedIssue("o", "r", 12, DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture));
        await host.Poller.RunCycleAsync(Ct);

        var row = Assert.Single(await ReadEventsAsync());
        Assert.Equal("gh:o/r#12:labeled:900", row.DedupKey);
        Assert.Contains("deliveryId", row.Payload, StringComparison.Ordinal);
        Assert.True(_gitHub.IssueEventCalls("o", "r", 12) >= 2, "El sondeo también resolvió el etiquetado.");
        Assert.Equal(permissionCalls, _gitHub.PermissionCalls);
    }

    [Fact]
    public async Task Poll_then_webhook_of_the_same_labeling_create_a_single_event()
    {
        _gitHub.SetListedIssue("o", "r", 12, "2026-11-22T10:00:00Z");
        _gitHub.AddIssueEvent("o", "r", 12, 900, "labeled", "codenetix", Alice, LabeledAt);
        await using var host = await StartAsync(Settings(polling: "o/r"), captureLogs: true);
        Assert.True(await WaitForAsync(async () => (await ReadEventsAsync()).Count == 1), "El primer ciclo del sondeo crea el Evento.");
        var permissionCalls = _gitHub.PermissionCalls;

        var delivery = Guid.NewGuid().ToString();
        await SendAsync(host, "issues", LabeledPayload(), delivery);
        Assert.True(await WaitForAsync(() => Task.FromResult(host.Processor.CompletedDeliveries >= 1)));

        var row = Assert.Single(await ReadEventsAsync());
        Assert.DoesNotContain("deliveryId", row.Payload, StringComparison.Ordinal);
        Assert.Equal(permissionCalls, _gitHub.PermissionCalls);
        Assert.Contains(_logs, l => l.Contains(delivery, StringComparison.Ordinal) && l.Contains("ya estaba registrado", StringComparison.Ordinal));
    }

    // --- Fila 11: lanzada por otra Fuente después de etiquetarla → Ignored sin permiso ---

    [Fact]
    public async Task Issue_launched_by_another_source_after_labeling_is_ignored_without_asking_permission()
    {
        var labeledAt = DateTimeOffset.UtcNow.AddHours(-1);
        _gitHub.AddIssueEvent("o", "r", 12, 900, "labeled", "codenetix", Alice, labeledAt);
        await using var host = await StartAsync(Settings());
        using (var client = host.Factory.CreateClient())
        {
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", CodeNetixFactory.TestAccessToken);
            using var manual = await client.PostAsJsonAsync(SubmitManualEventEndpoint.Route, new { url = "https://github.com/o/r/issues/12", requestId = Guid.NewGuid() }, Ct);
            manual.EnsureSuccessStatusCode();
        }

        var permissionCalls = _gitHub.PermissionCalls;
        await SendAsync(host, "issues", LabeledPayload(updatedAt: labeledAt), Guid.NewGuid().ToString());
        Assert.True(await WaitForAsync(() => Task.FromResult(host.Processor.CompletedDeliveries >= 1)));

        var webhook = Assert.Single(await ReadEventsAsync(), r => r.Source == "GitHub");
        Assert.Equal(("Ignored", GitHubPoller.AlreadyLaunchedReason), (webhook.Status, webhook.IgnoredReason));
        Assert.Equal(permissionCalls, _gitHub.PermissionCalls);
    }

    // --- Fila 12: reentrega → idempotente (duplicate si aún está en cola) ---

    [Fact]
    public async Task Redelivery_is_idempotent_and_a_pending_delivery_is_reported_as_duplicate()
    {
        _gitHub.AddIssueEvent("o", "r", 12, 900, "labeled", "codenetix", Alice, LabeledAt);
        _gitHub.IssueEventsStatus = 502; // la primera entrega queda pendiente (reintentando)
        await using var host = await StartAsync(Settings(), retryDelay: TimeSpan.FromMilliseconds(300));
        var delivery = Guid.NewGuid().ToString();

        var first = await SendAsync(host, "issues", LabeledPayload(), delivery);
        var second = await SendAsync(host, "issues", LabeledPayload(), delivery.ToUpperInvariant());

        Assert.Equal(StatusCodes.Status202Accepted, first.Status);
        Assert.Contains("\"queued\"", first.Body, StringComparison.Ordinal);
        Assert.Equal(StatusCodes.Status202Accepted, second.Status);
        Assert.Contains("\"duplicate\"", second.Body, StringComparison.Ordinal);

        _gitHub.IssueEventsStatus = 200;
        Assert.True(await WaitForAsync(() => Task.FromResult(host.Processor.CompletedDeliveries >= 1)));
        Assert.Equal("Accepted", Assert.Single(await ReadEventsAsync()).Status);

        // Reentrega ("Redeliver") ya procesada: se vuelve a encolar, pero la clave ya existe.
        var third = await SendAsync(host, "issues", LabeledPayload(), delivery);
        Assert.Contains("\"queued\"", third.Body, StringComparison.Ordinal);
        Assert.True(await WaitForAsync(() => Task.FromResult(host.Processor.CompletedDeliveries >= 2)));
        Assert.Single(await ReadEventsAsync());
    }

    // --- Fila 13: evento aún no visible → reintenta hasta verlo ---

    [Fact]
    public async Task Label_event_not_yet_visible_is_retried_until_it_appears()
    {
        await using var host = await StartAsync(Settings(), retryDelay: TimeSpan.FromMilliseconds(200));

        await SendAsync(host, "issues", LabeledPayload(), Guid.NewGuid().ToString());
        Assert.True(await WaitForAsync(() => Task.FromResult(_gitHub.IssueEventCalls("o", "r", 12) >= 1)));
        _gitHub.AddIssueEvent("o", "r", 12, 900, "labeled", "codenetix", Alice, LabeledAt);

        Assert.True(await WaitForAsync(() => Task.FromResult(host.Processor.CompletedDeliveries >= 1)));
        var row = Assert.Single(await ReadEventsAsync());
        Assert.Equal(("gh:o/r#12:labeled:900", "Accepted"), (row.DedupKey, row.Status));
        Assert.True(_gitHub.IssueEventCalls("o", "r", 12) >= 2);
    }

    [Fact]
    public async Task Only_an_older_labeling_visible_is_not_recorded_and_the_new_one_is_awaited()
    {
        _gitHub.AddIssueEvent("o", "r", 12, 800, "labeled", "codenetix", Alice, DateTimeOffset.UtcNow.AddHours(-2));
        _gitHub.AddIssueEvent("o", "r", 12, 810, "unlabeled", "codenetix", Alice, DateTimeOffset.UtcNow.AddHours(-2));
        await using var host = await StartAsync(Settings(), retryDelay: TimeSpan.FromMilliseconds(200));

        await SendAsync(host, "issues", LabeledPayload(), Guid.NewGuid().ToString());
        Assert.True(await WaitForAsync(() => Task.FromResult(_gitHub.IssueEventCalls("o", "r", 12) >= 1)));
        _gitHub.AddIssueEvent("o", "r", 12, 950, "labeled", "codenetix", Alice, LabeledAt);

        Assert.True(await WaitForAsync(() => Task.FromResult(host.Processor.CompletedDeliveries >= 1)));
        Assert.Equal("gh:o/r#12:labeled:950", Assert.Single(await ReadEventsAsync()).DedupKey);
    }

    [Fact]
    public async Task Label_event_never_visible_gives_up_after_the_bounded_retries_without_persisting()
    {
        await using var host = await StartAsync(Settings(), retryDelay: TimeSpan.FromMilliseconds(50), captureLogs: true);
        var delivery = Guid.NewGuid().ToString();

        await SendAsync(host, "issues", LabeledPayload(), delivery);

        Assert.True(await WaitForAsync(() => Task.FromResult(host.Processor.CompletedDeliveries >= 1)));
        Assert.Equal(GitHubWebhookOptions.DefaultRetryDelays.Count + 1, _gitHub.IssueEventCalls("o", "r", 12));
        Assert.Empty(await ReadEventsAsync());
        Assert.Contains(_logs, l => l.Contains(delivery, StringComparison.Ordinal) && l.Contains("descartada", StringComparison.Ordinal));
    }

    // --- Fila 14: GitHub no responde → reintento acotado; nada persistido mientras falle ---

    [Theory]
    [InlineData("events")]
    [InlineData("permission")]
    public async Task GitHub_unavailable_is_retried_and_never_logs_the_secret_or_the_token(string stage)
    {
        _gitHub.AddIssueEvent("o", "r", 12, 900, "labeled", "codenetix", Alice, LabeledAt);
        await using var host = await StartAsync(Settings(), retryDelay: TimeSpan.FromMilliseconds(300), captureLogs: true);
        if (stage == "events")
        {
            _gitHub.IssueEventsStatus = 502;
        }
        else
        {
            _gitHub.PermissionStatus = 502;
        }

        var delivery = Guid.NewGuid().ToString();
        await SendAsync(host, "issues", LabeledPayload(), delivery);
        Assert.True(await WaitForAsync(() => Task.FromResult(_logs.Any(l => l.Contains("se reintenta", StringComparison.Ordinal)))));
        Assert.Empty(await ReadEventsAsync());

        _gitHub.IssueEventsStatus = 200;
        _gitHub.PermissionStatus = 200;
        Assert.True(await WaitForAsync(() => Task.FromResult(host.Processor.CompletedDeliveries >= 1)));
        Assert.Equal("Accepted", Assert.Single(await ReadEventsAsync()).Status);
        Assert.Contains(_logs, l => l.Contains(delivery, StringComparison.Ordinal));
        Assert.DoesNotContain(_logs, l => l.Contains(Secret, StringComparison.Ordinal) || l.Contains(Token, StringComparison.Ordinal));
    }

    // --- Filas 15–17 y 19: cuerpo grande, formulario, JSON inválido, método ---

    [Fact]
    public async Task Body_over_one_mebibyte_is_413()
    {
        await using var host = await StartAsync(Settings());
        var body = LabeledPayload(issueBody: new string('x', GitHubWebhookOptions.DefaultMaxBodyBytes));

        var response = await SendAsync(host, "issues", body, Guid.NewGuid().ToString());

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, response.Status);
        Assert.Equal(0, host.Ingestion.Reader.Count);
    }

    [Fact]
    public async Task Form_encoded_delivery_is_415_with_the_content_type_hint()
    {
        await using var host = await StartAsync(Settings());

        var response = await SendAsync(host, "issues", "payload=" + Uri.EscapeDataString(LabeledPayload()), Guid.NewGuid().ToString(), contentType: "application/x-www-form-urlencoded");

        Assert.Equal(StatusCodes.Status415UnsupportedMediaType, response.Status);
        Assert.Contains("application/json", response.Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("""{"action":"labeled","label":{"name":"codenetix"},"issue":{},"repository":{"full_name":"o/r"},"sender":{"login":"alice"}}""")]
    [InlineData("""{"action":"labeled","label":{"name":"codenetix"},"issue":{"number":12},"repository":{"full_name":"o/r"},"sender":{}}""")]
    public async Task Signed_but_invalid_payload_is_400(string body)
    {
        await using var host = await StartAsync(Settings());

        var response = await SendAsync(host, "issues", body, Guid.NewGuid().ToString());

        Assert.Equal(StatusCodes.Status400BadRequest, response.Status);
        Assert.Equal(0, host.Ingestion.Reader.Count);
    }

    [Fact]
    public async Task Get_on_the_GitHub_webhook_is_405()
    {
        await using var host = await StartAsync(Settings());

        var response = await SendAsync(host, "issues", string.Empty, null, method: HttpMethods.Get);

        Assert.Equal(StatusCodes.Status405MethodNotAllowed, response.Status);
    }

    // --- Fila 18: cola llena → QueueFull (503 en el endpoint) ---

    [Fact]
    public void Full_queue_rejects_the_delivery_and_frees_its_id()
    {
        var options = new GitHubWebhookOptions { Secret = Secret, QueueCapacity = 1 };
        var ingestion = new GitHubWebhookIngestion(Options.Create(options), NullLogger<GitHubWebhookIngestion>.Instance);
        var first = Delivery(LabeledPayload(number: 1), Guid.NewGuid().ToString());
        var secondId = Guid.NewGuid().ToString();

        Assert.Equal(GitHubWebhookOutcome.Queued, ingestion.Receive(first).Outcome);
        Assert.Equal(GitHubWebhookOutcome.QueueFull, ingestion.Receive(Delivery(LabeledPayload(number: 2), secondId)).Outcome);
        Assert.True(ingestion.Reader.TryRead(out _));
        Assert.Equal(GitHubWebhookOutcome.Queued, ingestion.Receive(Delivery(LabeledPayload(number: 2), secondId)).Outcome);
    }

    // --- Firma: vector de la documentación de GitHub ---

    [Fact]
    public void Signature_matches_the_GitHub_documentation_example_with_a_lowercase_prefix_and_any_hex_case()
    {
        const string DocsSecret = "It's a Secret to Everybody";
        var body = Encoding.UTF8.GetBytes("Hello, World!");
        const string Expected = "sha256=757107ea0eb2509fc211221cce984b8a37570b6d7586c22c46f4379c8b043e17";

        Assert.Equal(Expected, GitHubWebhookSignature.Compute(DocsSecret, body));
        Assert.True(GitHubWebhookSignature.IsValid(DocsSecret, body, Expected));
        Assert.True(GitHubWebhookSignature.IsValid(DocsSecret, body, "sha256=" + Expected["sha256=".Length..].ToUpperInvariant()));
        Assert.False(GitHubWebhookSignature.IsValid(DocsSecret, body, "SHA256=" + Expected["sha256=".Length..]));
        Assert.False(GitHubWebhookSignature.IsValid(DocsSecret, Encoding.UTF8.GetBytes("Hello, World?"), Expected));
    }

    // --- Fila 20: configuración inválida → Diagnostic; mapeo a GitHubWebhookOptions ---

    [Theory]
    [InlineData("short-secret", null, InstanceOptionsValidator.InvalidWebhookSecret)]
    [InlineData("secret-with-a-tab\t-0123456789", null, InstanceOptionsValidator.InvalidWebhookSecret)]
    [InlineData(null, "foo", InstanceOptionsValidator.InvalidWebhookRepository)]
    [InlineData(null, "o/r,o/r/x", InstanceOptionsValidator.InvalidWebhookRepository)]
    public async Task Invalid_webhook_configuration_puts_the_instance_in_Diagnostic(string? secret, string? repositories, string reason)
    {
        var settings = Settings(secret: secret);
        if (repositories is not null)
        {
            settings["CODENETIX:GitHub:WebhookRepositories"] = repositories;
        }

        await using var host = await StartAsync(settings, expected: InstanceHealthStatus.Diagnostic);

        var health = host.Factory.Services.GetRequiredService<InstanceHealth>();
        Assert.Contains(reason, health.Reasons);
        Assert.All(health.Reasons, r => Assert.False(secret is not null && r.Contains(secret, StringComparison.Ordinal)));
    }

    [Fact]
    public void Too_many_webhook_repositories_is_a_Diagnostic_reason()
    {
        var options = new InstanceOptions();
        options.GitHub.WebhookRepositories = [.. Enumerable.Range(0, 101).Select(i => $"o/r{i}")];

        Assert.Contains(InstanceOptionsValidator.TooManyWebhookRepositories, InstanceOptionsValidator.Validate(options));
    }

    [Fact]
    public async Task Configuration_maps_the_trimmed_secret_the_shared_label_and_lowercase_repositories()
    {
        var settings = Settings(secret: "  " + Secret + "  ");
        settings["CODENETIX:GitHub:WebhookRepositories"] = "O/R; acme/Web";
        settings["CODENETIX:Polling:Label"] = "auto-fix";
        await using var host = await StartAsync(settings);

        var options = host.Factory.Services.GetRequiredService<IOptions<GitHubWebhookOptions>>().Value;
        Assert.Equal(Secret, options.Secret);
        Assert.Equal("auto-fix", options.Label);
        Assert.Equal(["acme/web", "o/r"], options.Repositories.Order(StringComparer.Ordinal));

        // La etiqueta compartida es la de disparo también para el webhook.
        var response = await SendAsync(host, "issues", LabeledPayload(label: "codenetix"), Guid.NewGuid().ToString());
        Assert.Contains("auto-fix", response.Body, StringComparison.Ordinal);
    }

    // --- Revisión 1 -----------------------------------------------------------------

    // Patch 1: margen de frescura de 5 s (las dos fechas son de GitHub).
    [Theory]
    [InlineData(-4, true)]
    [InlineData(-6, false)]
    public async Task Freshness_tolerance_accepts_an_event_4_seconds_older_and_rejects_one_6_seconds_older(int offsetSeconds, bool accepted)
    {
        _gitHub.AddIssueEvent("o", "r", 12, 900, "labeled", "codenetix", Alice, LabeledAt.AddSeconds(offsetSeconds));
        await using var host = await StartAsync(Settings(), retryDelay: TimeSpan.FromMilliseconds(50));

        await SendAsync(host, "issues", LabeledPayload(), Guid.NewGuid().ToString());

        Assert.True(await WaitForAsync(() => Task.FromResult(host.Processor.CompletedDeliveries >= 1)));
        if (accepted)
        {
            Assert.Equal("gh:o/r#12:labeled:900", Assert.Single(await ReadEventsAsync()).DedupKey);
            Assert.Equal(1, _gitHub.IssueEventCalls("o", "r", 12));
        }
        else
        {
            Assert.Empty(await ReadEventsAsync());
            Assert.Equal(GitHubWebhookOptions.DefaultRetryDelays.Count + 1, _gitHub.IssueEventCalls("o", "r", 12));
        }
    }

    [Fact]
    public async Task Quick_relabel_within_the_old_60_second_window_is_not_lost_as_the_previous_labeling()
    {
        var first = LabeledAt.AddSeconds(-30);
        _gitHub.AddIssueEvent("o", "r", 12, 900, "labeled", "codenetix", Alice, first);
        await using var host = await StartAsync(Settings(), retryDelay: TimeSpan.FromMilliseconds(200));
        await SendAsync(host, "issues", LabeledPayload(updatedAt: first), Guid.NewGuid().ToString());
        Assert.True(await WaitForAsync(() => Task.FromResult(host.Processor.CompletedDeliveries >= 1)));

        // Quitar y volver a poner a los 30 s: la API todavía no muestra el evento nuevo en el primer intento.
        _gitHub.AddIssueEvent("o", "r", 12, 940, "unlabeled", "codenetix", Alice, LabeledAt.AddSeconds(-1));
        var callsBefore = _gitHub.IssueEventCalls("o", "r", 12);
        await SendAsync(host, "issues", LabeledPayload(), Guid.NewGuid().ToString());
        Assert.True(await WaitForAsync(() => Task.FromResult(_gitHub.IssueEventCalls("o", "r", 12) > callsBefore)));
        _gitHub.AddIssueEvent("o", "r", 12, 950, "labeled", "codenetix", Alice, LabeledAt);

        Assert.True(await WaitForAsync(() => Task.FromResult(host.Processor.CompletedDeliveries >= 2)));
        Assert.Equal(["gh:o/r#12:labeled:900", "gh:o/r#12:labeled:950"], (await ReadEventsAsync()).Select(r => r.DedupKey).Order(StringComparer.Ordinal));
    }

    // Patch 2: cuerpo sin Content-Length y límite exacto.
    [Fact]
    public async Task Oversized_streamed_body_without_Content_Length_is_413_and_nothing_is_queued()
    {
        await using var host = await StartAsync(Settings());

        var response = await SendAsync(host, "issues", PayloadOfSize(GitHubWebhookOptions.DefaultMaxBodyBytes + 10), Guid.NewGuid().ToString(), contentLength: false);

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, response.Status);
        Assert.Equal(0, host.Ingestion.Reader.Count);
    }

    [Theory]
    [InlineData(0, true, StatusCodes.Status202Accepted)]
    [InlineData(1, true, StatusCodes.Status413PayloadTooLarge)]
    [InlineData(0, false, StatusCodes.Status202Accepted)]
    [InlineData(1, false, StatusCodes.Status413PayloadTooLarge)]
    public async Task Exactly_one_mebibyte_is_accepted_and_one_byte_more_is_413(int extra, bool contentLength, int expected)
    {
        await using var host = await StartAsync(Settings(), configure: o => o.RetryDelays.Clear());
        var body = PayloadOfSize(GitHubWebhookOptions.DefaultMaxBodyBytes + extra);
        Assert.Equal(GitHubWebhookOptions.DefaultMaxBodyBytes + extra, Encoding.UTF8.GetByteCount(body));

        var response = await SendAsync(host, "issues", body, Guid.NewGuid().ToString(), contentLength: contentLength);

        Assert.Equal(expected, response.Status);
    }

    // Patch 3: 404/410 (issue borrada o repositorio invisible para el token) es definitivo; los reintentos no ocupan huecos.
    [Theory]
    [InlineData(404)]
    [InlineData(410)]
    public async Task Issue_or_repository_not_found_is_final_and_not_retried(int status)
    {
        _gitHub.IssueEventsStatuses["o/r#12"] = status;
        await using var host = await StartAsync(Settings(), retryDelay: TimeSpan.FromMilliseconds(50), captureLogs: true);

        await SendAsync(host, "issues", LabeledPayload(), Guid.NewGuid().ToString());

        Assert.True(await WaitForAsync(() => Task.FromResult(host.Processor.CompletedDeliveries >= 1)));
        Assert.Equal(1, _gitHub.IssueEventCalls("o", "r", 12));
        Assert.Contains(_logs, l => l.Contains("descartada", StringComparison.Ordinal) && l.Contains("no puede ver el repositorio", StringComparison.Ordinal));
        Assert.Empty(await ReadEventsAsync());
    }

    [Fact]
    public async Task A_delivery_waiting_to_retry_does_not_block_the_others()
    {
        _gitHub.IssueEventsStatuses["o/r#12"] = 502;
        _gitHub.AddIssueEvent("o", "r", 13, 913, "labeled", "codenetix", Alice, LabeledAt);
        await using var host = await StartAsync(Settings(), retryDelay: TimeSpan.FromSeconds(30), configure: o => o.MaxConcurrency = 1);

        await SendAsync(host, "issues", LabeledPayload(number: 12), Guid.NewGuid().ToString());
        Assert.True(await WaitForAsync(() => Task.FromResult(_gitHub.IssueEventCalls("o", "r", 12) >= 1)));
        await SendAsync(host, "issues", LabeledPayload(number: 13), Guid.NewGuid().ToString());

        Assert.True(await WaitForAsync(async () => (await ReadEventsAsync()).Count == 1, timeoutSeconds: 5), "La #13 no debe esperar a los reintentos de la #12.");
        Assert.Equal("gh:o/r#13:labeled:913", Assert.Single(await ReadEventsAsync()).DedupKey);
        Assert.Equal(1, _gitHub.IssueEventCalls("o", "r", 12));
    }

    // Patches 4 y 8: rate limit (eventos o permiso) → un solo intento y descarte.
    [Theory]
    [InlineData("events-429")]
    [InlineData("events-quota")]
    [InlineData("permission-quota")]
    public async Task Rate_limit_stops_the_retries_after_one_call(string variant)
    {
        _gitHub.AddIssueEvent("o", "r", 12, 900, "labeled", "codenetix", Alice, LabeledAt);
        await using var host = await StartAsync(Settings(), retryDelay: TimeSpan.FromMilliseconds(50), captureLogs: true);
        switch (variant)
        {
            case "events-429":
                _gitHub.IssueEventsStatus = 429;
                break;
            case "events-quota":
                _gitHub.RateLimitExhausted = true;
                _gitHub.IssueEventsStatus = 403;
                break;
            default:
                _gitHub.RateLimitExhausted = true;
                _gitHub.PermissionStatus = 403;
                break;
        }

        await SendAsync(host, "issues", LabeledPayload(), Guid.NewGuid().ToString());

        Assert.True(await WaitForAsync(() => Task.FromResult(host.Processor.CompletedDeliveries >= 1)));
        Assert.Equal(1, _gitHub.IssueEventCalls("o", "r", 12));
        Assert.Equal(variant.StartsWith("permission", StringComparison.Ordinal) ? 1 : 0, _gitHub.PermissionCalls);
        Assert.Contains(_logs, l => l.Contains("descartada", StringComparison.Ordinal) && l.Contains("1 intento", StringComparison.Ordinal));
        Assert.Empty(await ReadEventsAsync());
    }

    // Patch 5: evento sin created_at con updated_at en el payload → aún no visible; sin updated_at y existente → aviso.
    [Fact]
    public async Task Label_event_without_created_at_is_treated_as_not_yet_visible()
    {
        _gitHub.AddIssueEvent("o", "r", 12, 900, "labeled", "codenetix", Alice, omitCreatedAt: true);
        await using var host = await StartAsync(Settings(), retryDelay: TimeSpan.FromMilliseconds(50));

        await SendAsync(host, "issues", LabeledPayload(), Guid.NewGuid().ToString());

        Assert.True(await WaitForAsync(() => Task.FromResult(host.Processor.CompletedDeliveries >= 1)));
        Assert.Equal(GitHubWebhookOptions.DefaultRetryDelays.Count + 1, _gitHub.IssueEventCalls("o", "r", 12));
        Assert.Empty(await ReadEventsAsync());
    }

    [Fact]
    public async Task Existing_event_without_updated_at_in_the_payload_logs_a_warning()
    {
        _gitHub.AddIssueEvent("o", "r", 12, 900, "labeled", "codenetix", Alice, LabeledAt);
        await using var host = await StartAsync(Settings(), captureLogs: true);

        await SendAsync(host, "issues", LabeledPayload(omitUpdatedAt: true), Guid.NewGuid().ToString());
        Assert.True(await WaitForAsync(() => Task.FromResult(host.Processor.CompletedDeliveries >= 1)));
        var delivery = Guid.NewGuid().ToString();
        await SendAsync(host, "issues", LabeledPayload(omitUpdatedAt: true), delivery);
        Assert.True(await WaitForAsync(() => Task.FromResult(host.Processor.CompletedDeliveries >= 2)));

        Assert.Single(await ReadEventsAsync());
        Assert.Contains(_logs, l => l.Contains(delivery, StringComparison.Ordinal) && l.Contains("no se ha podido comprobar", StringComparison.Ordinal));
    }

    // Patch 9: se autoriza a quien de verdad etiquetó (actor), nunca al sender.
    [Fact]
    public async Task The_resolved_actor_is_authorized_and_the_sender_never_is()
    {
        _gitHub.PermissionsByLogin["mallory"] = "read";
        _gitHub.AddIssueEvent("o", "r", 12, 900, "labeled", "codenetix", "mallory", LabeledAt);
        await using var host = await StartAsync(Settings());

        await SendAsync(host, "issues", LabeledPayload(sender: "bob"), Guid.NewGuid().ToString());

        Assert.True(await WaitForAsync(() => Task.FromResult(host.Processor.CompletedDeliveries >= 1)));
        var row = Assert.Single(await ReadEventsAsync());
        Assert.Equal(("mallory", "Ignored", ManualEventIngestion.UnauthorizedReason), (row.Actor, row.Status, row.IgnoredReason));
        Assert.Contains(_gitHub.Requests, r => r.Kind == "permission:o/r:mallory");
        Assert.DoesNotContain(_gitHub.Requests, r => r.Kind == "permission:o/r:bob");
    }

    // Patch 10: dos entregas del mismo etiquetado en paralelo → un único Evento, sin errores.
    [Fact]
    public async Task Two_parallel_deliveries_of_the_same_labeling_create_exactly_one_event()
    {
        _gitHub.AddIssueEvent("o", "r", 12, 900, "labeled", "codenetix", Alice, LabeledAt);
        await using var host = await StartAsync(Settings(), captureLogs: true);

        var responses = await Task.WhenAll(
            SendAsync(host, "issues", LabeledPayload(), Guid.NewGuid().ToString()),
            SendAsync(host, "issues", LabeledPayload(), Guid.NewGuid().ToString()));

        Assert.All(responses, r => Assert.Equal(StatusCodes.Status202Accepted, r.Status));
        Assert.True(await WaitForAsync(() => Task.FromResult(host.Processor.CompletedDeliveries >= 2)));
        Assert.Equal("Accepted", Assert.Single(await ReadEventsAsync()).Status);
        Assert.DoesNotContain(_logs, l => l.Contains("fallo inesperado", StringComparison.Ordinal));
    }

    // --- Utilidades ----------------------------------------------------------------

    /// <summary>Payload aplicable de exactamente <paramref name="size"/> bytes (relleno ASCII en el cuerpo de la issue).</summary>
    private static string PayloadOfSize(int size)
    {
        var empty = Encoding.UTF8.GetByteCount(LabeledPayload(issueBody: string.Empty));
        return LabeledPayload(issueBody: new string('x', size - empty));
    }

    private static string LabeledPayload(
        long number = 12,
        string action = "labeled",
        string label = "codenetix",
        string repository = "o/r",
        string sender = Alice,
        bool pullRequest = false,
        string state = "open",
        DateTimeOffset? updatedAt = null,
        string issueBody = "El botón Guardar no hace nada",
        bool omitUpdatedAt = false)
    {
        var issue = new Dictionary<string, object?>
        {
            ["number"] = number,
            ["title"] = "Guardar no funciona",
            ["body"] = issueBody,
            ["state"] = state,
            ["updated_at"] = (updatedAt ?? LabeledAt).ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
        };
        if (omitUpdatedAt)
        {
            issue.Remove("updated_at");
        }

        if (pullRequest)
        {
            issue["pull_request"] = new { url = "https://api.github.com/repos/o/r/pulls/12" };
        }

        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["action"] = action,
            ["label"] = new { name = label },
            ["issue"] = issue,
            ["repository"] = new { full_name = repository },
            ["sender"] = new { login = sender },
        });
    }

    private static string Sign(string secret, string body) => GitHubWebhookSignature.Compute(secret, Encoding.UTF8.GetBytes(body));

    private static GitHubWebhookDelivery Delivery(string body, string deliveryId) =>
        new("issues", deliveryId, Sign(Secret, body), "application/json", Encoding.UTF8.GetBytes(body));

    private static async Task<(int Status, string Body)> SendAsync(
        WebhookHost host,
        string eventName,
        string body,
        string? deliveryId,
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
                c.Request.Path = ReceiveWebhookEndpoint.GitHubRoute;
                c.Request.Headers["X-GitHub-Event"] = eventName;
                if (deliveryId is not null)
                {
                    c.Request.Headers["X-GitHub-Delivery"] = deliveryId;
                }

                var header = sign ? GitHubWebhookSignature.Compute(Secret, bytes) : signature;
                if (signatureHeaders is not null)
                {
                    c.Request.Headers["X-Hub-Signature-256"] = new Microsoft.Extensions.Primitives.StringValues(signatureHeaders);
                }
                else if (header is not null)
                {
                    c.Request.Headers["X-Hub-Signature-256"] = header;
                }

                c.Request.ContentType = contentType;
                c.Request.ContentLength = contentLength ? bytes.Length : null;
                c.Request.Body = new MemoryStream(bytes);
            },
            Ct);

        using var reader = new StreamReader(context.Response.Body);
        return (context.Response.StatusCode, await reader.ReadToEndAsync(Ct));
    }

    private static Dictionary<string, string?> Settings(string? secret = Secret, string? polling = null)
    {
        var settings = new Dictionary<string, string?>(CodeNetixFactory.CompleteSettings);
        if (secret is not null)
        {
            settings["CODENETIX:GitHub:WebhookSecret"] = secret;
        }

        if (polling is not null)
        {
            settings["CODENETIX:Polling:Repositories"] = polling;
        }

        return settings;
    }

    private async Task<WebhookHost> StartAsync(
        Dictionary<string, string?> settings,
        TimeSpan? retryDelay = null,
        bool captureLogs = false,
        InstanceHealthStatus expected = InstanceHealthStatus.Ready,
        Action<GitHubWebhookOptions>? configure = null)
    {
        var baseFactory = new CodeNetixFactory(_dataDirectory, _webRoot, settings, gitHub: _gitHub);
        var factory = baseFactory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            if (retryDelay is { } delay)
            {
                s.PostConfigure<GitHubWebhookOptions>(o =>
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
        var host = new WebhookHost(baseFactory, factory);
        var health = factory.Services.GetRequiredService<InstanceHealth>();
        Assert.True(await WaitForAsync(() => Task.FromResult(health.Status == expected)), $"La instancia debe llegar a {expected}.");
        _startupCalls = _gitHub.TotalCalls;
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

    private sealed class WebhookHost(CodeNetixFactory baseFactory, WebApplicationFactory<Program> factory) : IAsyncDisposable
    {
        public WebApplicationFactory<Program> Factory => factory;

        public GitHubWebhookIngestion Ingestion => factory.Services.GetRequiredService<GitHubWebhookIngestion>();

        public GitHubWebhookProcessor Processor => factory.Services.GetRequiredService<GitHubWebhookProcessor>();

        public GitHubPoller Poller => factory.Services.GetRequiredService<GitHubPoller>();

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
