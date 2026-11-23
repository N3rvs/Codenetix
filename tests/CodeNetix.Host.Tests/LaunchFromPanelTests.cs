using System.Net;
using System.Net.Http.Json;
using CodeNetix.Host.Features.GetEvent;
using CodeNetix.Host.Features.GetRun;
using CodeNetix.Host.Features.SubmitManualEvent;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace CodeNetix.Host.Tests;

/// <summary>
/// Pruebas de integración para la Historia 4.5 (Lanzar trabajo desde el Panel):
/// flujo completo <c>POST /api/events/manual</c> → <c>GET /api/events/{eventId}</c> → <c>GET /api/runs/{runId}</c>,
/// paridad exacta de errores con el CLI (<see cref="FixMode"/>), Sentry sin configurar (422) y autenticación 401.
/// </summary>
public sealed class LaunchFromPanelTests : IDisposable
{
    private const string IssueUrl = "https://github.com/o/r/issues/12";
    private const string ExpectedObjectKey = "github:o/r#12";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "codenetix-launch-" + Guid.NewGuid().ToString("N"));
    private readonly string _dataDirectory;
    private readonly string _webRoot;

    public LaunchFromPanelTests()
    {
        _dataDirectory = Path.Combine(_root, "data");
        _webRoot = Path.Combine(_root, "wwwroot");
        Directory.CreateDirectory(_webRoot);
        File.WriteAllText(Path.Combine(_webRoot, "index.html"), "<!doctype html><html><body>Panel</body></html>");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Full_launch_flow_creates_event_dispatches_run_and_returns_run_detail()
    {
        await using var factory = new CodeNetixFactory(
            _dataDirectory,
            _webRoot,
            new Dictionary<string, string?>(CodeNetixFactory.CompleteSettings));
        using var client = factory.CreateAuthorizedClient();

        var requestId = Guid.NewGuid();
        using var submitResponse = await client.PostAsJsonAsync(
            SubmitManualEventEndpoint.Route,
            new SubmitManualEventRequest(IssueUrl, requestId.ToString("D")),
            Ct);

        Assert.Equal(HttpStatusCode.Created, submitResponse.StatusCode);
        var submitted = await submitResponse.Content.ReadFromJsonAsync<SubmitManualEventResponse>(Ct);
        Assert.NotNull(submitted);
        Assert.True(submitted.Created);
        Assert.Equal("Accepted", submitted.Status);
        Assert.Null(submitted.Reason);
        Assert.Equal("o/r", submitted.Repository);

        // Esperar a que el Dispatcher cree la Ejecución y aparezca runId en GET /api/events/{eventId}.
        EventResponse? eventState = null;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var eventResponse = await client.GetAsync($"/api/events/{submitted.EventId:D}", Ct);
            Assert.Equal(HttpStatusCode.OK, eventResponse.StatusCode);
            eventState = await eventResponse.Content.ReadFromJsonAsync<EventResponse>(Ct);
            if (eventState?.RunId is not null)
            {
                break;
            }

            await Task.Delay(50, Ct);
        }

        Assert.NotNull(eventState);
        Assert.Equal(submitted.EventId, eventState.EventId);
        Assert.Equal("Accepted", eventState.Status);
        Assert.NotNull(eventState.RunId);

        using var runResponse = await client.GetAsync($"/api/runs/{eventState.RunId.Value:D}", Ct);
        Assert.Equal(HttpStatusCode.OK, runResponse.StatusCode);
        var run = await runResponse.Content.ReadFromJsonAsync<RunResponse>(Ct);
        Assert.NotNull(run);
        Assert.Equal(eventState.RunId.Value, run.RunId);
        Assert.Equal(submitted.EventId, run.EventId);
        Assert.Equal(ExpectedObjectKey, run.ObjectKey);
    }

    [Theory]
    [InlineData("no-es-una-url", HttpStatusCode.BadRequest)]
    [InlineData("https://github.com/acme/web/pull/10", HttpStatusCode.BadRequest)]
    [InlineData("https://acme.sentry.io/issues/123/", HttpStatusCode.UnprocessableEntity)]
    public async Task Invalid_or_unsupported_url_returns_exact_same_error_detail_as_cli_fix(
        string candidateUrl,
        HttpStatusCode expectedStatus)
    {
        await using var factory = new CodeNetixFactory(
            _dataDirectory,
            _webRoot,
            new Dictionary<string, string?>(CodeNetixFactory.CompleteSettings));
        using var client = factory.CreateAuthorizedClient();

        using var apiResponse = await client.PostAsJsonAsync(
            SubmitManualEventEndpoint.Route,
            new SubmitManualEventRequest(candidateUrl, Guid.NewGuid().ToString("D")),
            Ct);

        Assert.Equal(expectedStatus, apiResponse.StatusCode);
        var problem = await apiResponse.Content.ReadFromJsonAsync<ProblemDetails>(Ct);
        Assert.NotNull(problem);
        Assert.False(string.IsNullOrWhiteSpace(problem.Detail));

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        using var handler = factory.Server.CreateHandler();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CODENETIX:Host:Url"] = factory.Server.BaseAddress.ToString(),
                ["CODENETIX:Host:AccessToken"] = CodeNetixFactory.TestAccessToken,
            })
            .Build();

        var exitCode = await FixMode.RunAsync(
            [candidateUrl],
            configuration,
            stdout,
            stderr,
            handler,
            [TimeSpan.Zero],
            Ct);

        Assert.Equal(CodeNetixCommandLine.ExitFailure, exitCode);
        Assert.Empty(stdout.ToString());
        Assert.Equal(problem.Detail, stderr.ToString().Trim());
    }

    [Fact]
    public async Task Ignored_event_returns_reason_and_null_runId_on_event_lookup()
    {
        await using var gitHub = await FakeGitHub.StartAsync(Ct);
        gitHub.Permission = "read";

        await using var factory = new CodeNetixFactory(
            _dataDirectory,
            _webRoot,
            new Dictionary<string, string?>(CodeNetixFactory.CompleteSettings),
            gitHub: gitHub);
        using var client = factory.CreateAuthorizedClient();

        using var submitResponse = await client.PostAsJsonAsync(
            SubmitManualEventEndpoint.Route,
            new SubmitManualEventRequest(IssueUrl, Guid.NewGuid().ToString("D")),
            Ct);

        Assert.Equal(HttpStatusCode.Created, submitResponse.StatusCode);
        var submitted = await submitResponse.Content.ReadFromJsonAsync<SubmitManualEventResponse>(Ct);
        Assert.NotNull(submitted);
        Assert.Equal("Ignored", submitted.Status);
        Assert.False(string.IsNullOrWhiteSpace(submitted.Reason));

        using var eventResponse = await client.GetAsync($"/api/events/{submitted.EventId:D}", Ct);
        Assert.Equal(HttpStatusCode.OK, eventResponse.StatusCode);
        var eventState = await eventResponse.Content.ReadFromJsonAsync<EventResponse>(Ct);
        Assert.NotNull(eventState);
        Assert.Equal(submitted.EventId, eventState.EventId);
        Assert.Equal("Ignored", eventState.Status);
        Assert.Equal(submitted.Reason, eventState.Reason);
        Assert.Null(eventState.RunId);
    }

    [Fact]
    public async Task Sentry_url_without_Sentry_configured_returns_422_and_unauthenticated_request_returns_401()
    {
        await using var factory = new CodeNetixFactory(
            _dataDirectory,
            _webRoot,
            new Dictionary<string, string?>(CodeNetixFactory.CompleteSettings));
        using var authorized = factory.CreateAuthorizedClient();
        using var anonymous = factory.CreateClient();

        using var sentryResponse = await authorized.PostAsJsonAsync(
            SubmitManualEventEndpoint.Route,
            new SubmitManualEventRequest("https://acme.sentry.io/issues/123/", Guid.NewGuid().ToString("D")),
            Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, sentryResponse.StatusCode);
        var problem = await sentryResponse.Content.ReadFromJsonAsync<ProblemDetails>(Ct);
        Assert.NotNull(problem);
        Assert.Equal(CodeNetix.Ingestion.Features.ReadSentryContext.SentryApiOptions.NotConfiguredReason, problem.Detail);

        using var unauthSubmit = await anonymous.PostAsJsonAsync(
            SubmitManualEventEndpoint.Route,
            new SubmitManualEventRequest(IssueUrl, Guid.NewGuid().ToString("D")),
            Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthSubmit.StatusCode);

        using var unauthGet = await anonymous.GetAsync($"/api/events/{Guid.NewGuid():D}", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthGet.StatusCode);
    }

    public void Dispose()
    {
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
}
