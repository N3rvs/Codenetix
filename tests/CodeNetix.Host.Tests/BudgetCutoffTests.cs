using System.Collections.Concurrent;
using System.Formats.Tar;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CodeNetix.Agents;
using CodeNetix.Agents.Features.ImplementSpec;
using CodeNetix.Contracts;
using CodeNetix.Host.Features.GetRun;
using CodeNetix.Pipeline;
using CodeNetix.Pipeline.Data;
using CodeNetix.Pipeline.Domain;
using CodeNetix.Pipeline.Events;
using CodeNetix.Pipeline.Features.CancelRun;
using CodeNetix.Pipeline.Features.ResolveConfig;
using CodeNetix.Pipeline.Features.RunPipeline;
using CodeNetix.Sandbox;
using CodeNetix.Sandbox.Gateway;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Forwarder;

namespace CodeNetix.Host.Tests;

/// <summary>
/// Pruebas de la Historia 5.2 (Corte automático por presupuesto, FR-13, AD-2, AD-15):
/// <list type="number">
/// <item><see cref="IBudgetLedger.ChargeAsync"/> devuelve <see cref="BudgetChargeOutcome.Exhausted"/> y dispara cancelación cooperativa al alcanzar <c>budget_usd</c>, sin falsos positivos sin límite o con modelos sin precio.</item>
/// <item><see cref="LlmReverseProxy"/> responde <c>402 Payment Required</c> con <c>codenetix:budget_exhausted</c> y 0 llamadas al Proveedor upstream cuando el presupuesto está agotado.</item>
/// <item><see cref="ClaudeCodeAgent"/> añade <c>--max-budget-usd &lt;restante&gt;</c> (<c>InvariantCulture</c>), devuelve <see cref="CodingResult.BudgetExhausted"/> sin ejecutar el Sandbox si ya está agotado y traduce <c>codenetix:budget_exhausted</c> en <c>stdout</c>/<c>stderr</c>.</item>
/// <item>Corte end-to-end en <see cref="RunOrchestrator"/> durante <c>Coding</c> (vía <see cref="CodingResult.BudgetExhausted"/> y vía cancelación cooperativa disparada por <see cref="IBudgetLedger.ChargeAsync"/>).</item>
/// <item>Corte end-to-end en <see cref="RunOrchestrator"/> durante <c>Analyzing</c> y <c>Reviewing</c> vía <c>BudgetTrackingChatClient</c>.</item>
/// <item>Precedencia: <c>stoppingToken</c> sin agotamiento de presupuesto ni cancelación del usuario sigue lanzando <see cref="OperationCanceledException"/>.</item>
/// </list>
/// </summary>
public sealed class BudgetCutoffTests : IDisposable
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "codenetix-budget-cutoff-" + Guid.NewGuid().ToString("N"));
    private readonly string _dataDirectory;
    private readonly string _webRoot;

    public BudgetCutoffTests()
    {
        _dataDirectory = Path.Combine(_root, "data");
        _webRoot = Path.Combine(_root, "wwwroot");
        Directory.CreateDirectory(_webRoot);
        File.WriteAllText(Path.Combine(_webRoot, "index.html"), "<!doctype html><html><body>Panel</body></html>");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task BudgetLedger_ChargeAsync_returns_Exhausted_at_budget_limit_and_Recorded_when_unlimited_or_unpriced()
    {
        await using var factory = new CodeNetixFactory(_dataDirectory, _webRoot, CodeNetixFactory.CompleteSettings);
        _ = factory.Server;

        var ledger = factory.Services.GetRequiredService<IBudgetLedger>();
        var registry = factory.Services.GetRequiredService<RunCancellationRegistry>();

        // 1. Con budget_usd = 0.01m y modelo valorado (claude-sonnet-5-5: $3/1M input, $15/1M output):
        //    1000 input ($0.003) + 500 output ($0.0075) = $0.0105 >= $0.01 -> Exhausted + RemainingUsd == 0m + cancelación cooperativa.
        var limitedRunId = await SeedRunAsync(factory, budgetLimitUsd: 0.01m);
        using var activeHandle = registry.BeginRun(limitedRunId, Ct);

        var exhaustedResult = await ledger.ChargeAsync(
            new BudgetChargeRequest(limitedRunId, null, "Coder", "Anthropic", "claude-sonnet-5-5", 1000, 500),
            Ct);

        Assert.Equal(BudgetChargeOutcome.Exhausted, exhaustedResult.Outcome);
        Assert.Equal(0m, exhaustedResult.RemainingUsd);
        Assert.Equal(0.0105m, exhaustedResult.TotalEstimatedCostUsd);
        Assert.True(activeHandle.IsBudgetExhausted);
        Assert.True(activeHandle.Token.IsCancellationRequested);
        Assert.True(registry.IsBudgetExhausted(limitedRunId, out var detail));
        Assert.False(string.IsNullOrWhiteSpace(detail));

        var costSummary = await ledger.GetRunCostAsync(limitedRunId, Ct);
        Assert.True(costSummary.IsExhausted);
        Assert.Equal(0m, costSummary.RemainingUsd);

        // 2. Sin límite (BudgetUsd == null): devuelve Recorded y RemainingUsd == null.
        var unlimitedRunId = await SeedRunAsync(factory, budgetLimitUsd: null);
        var unlimitedResult = await ledger.ChargeAsync(
            new BudgetChargeRequest(unlimitedRunId, null, "Coder", "Anthropic", "claude-sonnet-5-5", 100_000, 50_000),
            Ct);

        Assert.Equal(BudgetChargeOutcome.Recorded, unlimitedResult.Outcome);
        Assert.Null(unlimitedResult.RemainingUsd);

        // 3. Modelo local sin precio conocido (HasUnpricedModels == true): nunca dispara falso Exhausted.
        var unpricedRunId = await SeedRunAsync(factory, budgetLimitUsd: 0.01m);
        using var unpricedHandle = registry.BeginRun(unpricedRunId, Ct);

        var unpricedResult = await ledger.ChargeAsync(
            new BudgetChargeRequest(unpricedRunId, null, "Analyst", "Ollama", "qwen2.5-coder:32b", 100_000, 50_000),
            Ct);

        Assert.Equal(BudgetChargeOutcome.Recorded, unpricedResult.Outcome);
        Assert.True(unpricedResult.HasUnpricedModels);
        Assert.Null(unpricedResult.RemainingUsd);
        Assert.False(unpricedHandle.IsBudgetExhausted);
        Assert.False(unpricedHandle.Token.IsCancellationRequested);

        // 4. Igualdad exacta (EstimatedCostUsd == BudgetLimitUsd): 1000 input ($0.003) + 500 output ($0.0075) == $0.0105.
        var exactRunId = await SeedRunAsync(factory, budgetLimitUsd: 0.0105m);
        var exactResult = await ledger.ChargeAsync(
            new BudgetChargeRequest(exactRunId, null, "Coder", "Anthropic", "claude-sonnet-5-5", 1000, 500),
            Ct);
        Assert.Equal(BudgetChargeOutcome.Exhausted, exactResult.Outcome);
        Assert.Equal(0m, exactResult.RemainingUsd);
        Assert.Equal(0.0105m, exactResult.TotalEstimatedCostUsd);

        // 5. Acumulación en 2 cargos: primero Recorded, segundo Exhausted.
        var twoStepRunId = await SeedRunAsync(factory, budgetLimitUsd: 0.015m);
        var step1 = await ledger.ChargeAsync(
            new BudgetChargeRequest(twoStepRunId, null, "Analyst", "Anthropic", "claude-sonnet-5-5", 1000, 500),
            Ct);
        Assert.Equal(BudgetChargeOutcome.Recorded, step1.Outcome);
        Assert.Equal(0.0045m, step1.RemainingUsd);

        var step2 = await ledger.ChargeAsync(
            new BudgetChargeRequest(twoStepRunId, null, "Coder", "Anthropic", "claude-sonnet-5-5", 1000, 500),
            Ct);
        Assert.Equal(BudgetChargeOutcome.Exhausted, step2.Outcome);
        Assert.Equal(0m, step2.RemainingUsd);

        // 6. Combinación modelo valorado + modelo sin precio: HasUnpricedModels == true -> Recorded (nunca Exhausted).
        var mixedRunId = await SeedRunAsync(factory, budgetLimitUsd: 0.01m);
        var mixedStep1 = await ledger.ChargeAsync(
            new BudgetChargeRequest(mixedRunId, null, "Analyst", "Ollama", "qwen2.5-coder:32b", 1000, 500),
            Ct);
        Assert.Equal(BudgetChargeOutcome.Recorded, mixedStep1.Outcome);
        Assert.True(mixedStep1.HasUnpricedModels);

        var mixedStep2 = await ledger.ChargeAsync(
            new BudgetChargeRequest(mixedRunId, null, "Coder", "Anthropic", "claude-sonnet-5-5", 10_000, 5_000),
            Ct);
        Assert.Equal(BudgetChargeOutcome.Recorded, mixedStep2.Outcome);
        Assert.True(mixedStep2.HasUnpricedModels);
        Assert.Null(mixedStep2.RemainingUsd);
    }

    [Fact]
    public void RunCancellationRegistry_blocks_Deciding_and_user_cancel_after_budget_exhausted()
    {
        var registry = new RunCancellationRegistry();
        var runId = Guid.CreateVersion7();
        using var handle = registry.BeginRun(runId, Ct);

        Assert.True(registry.TryRequestBudgetExhausted(runId, "presupuesto agotado"));
        Assert.True(handle.IsBudgetExhausted);
        Assert.True(handle.Token.IsCancellationRequested);
        Assert.False(registry.TryEnterDeciding(runId));
        Assert.False(registry.TryRequestCancel(runId, out var inDeciding));
        Assert.False(inDeciding);
        Assert.False(handle.IsUserCancelRequested);
    }

    [Fact]
    public async Task LlmReverseProxy_returns_402_with_budget_exhausted_code_and_zero_upstream_calls_when_exhausted()
    {
        await using var factory = new CodeNetixFactory(_dataDirectory, _webRoot, CodeNetixFactory.CompleteSettings);
        _ = factory.Server;

        var runId = await SeedRunAsync(factory, budgetLimitUsd: 0.01m);
        var ledger = factory.Services.GetRequiredService<IBudgetLedger>();
        var charge = await ledger.ChargeAsync(
            new BudgetChargeRequest(runId, null, "Coder", "Anthropic", "claude-sonnet-5-5", 1000, 500),
            Ct);
        Assert.Equal(BudgetChargeOutcome.Exhausted, charge.Outcome);

        var gateway = factory.Services.GetRequiredService<ISandboxGateway>();
        var credential = gateway.Register(runId);

        var upstreamCalls = 0;
        using var transport = new StubHttpMessageHandler(_ =>
        {
            Interlocked.Increment(ref upstreamCalls);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"id":"msg_x","model":"claude-sonnet-5-5","usage":{"input_tokens":10,"output_tokens":10}}""", Encoding.UTF8, "application/json"),
            };
        });

        using var proxy = new LlmReverseProxy(
            gateway,
            factory.Services.GetRequiredService<IRunEventSink>(),
            factory.Services.GetRequiredService<IOptionsMonitor<SandboxOptions>>(),
            factory.Services.GetRequiredService<IHttpForwarder>(),
            NullLogger<LlmReverseProxy>.Instance,
            ledger,
            transport);

        var httpContext = new DefaultHttpContext { RequestServices = factory.Services };
        httpContext.Connection.LocalPort = SandboxOptions.DefaultLlmGatewayPort;
        httpContext.Request.Method = HttpMethods.Post;
        httpContext.Request.Path = "/llm/v1/messages";
        httpContext.Request.ContentType = "application/json";
        httpContext.Request.Headers[LlmReverseProxy.ApiKeyHeader] = credential.Token;
        var reqBytes = Encoding.UTF8.GetBytes("""{"model":"claude-sonnet-5-5","messages":[]}""");
        httpContext.Request.Body = new MemoryStream(reqBytes);
        httpContext.Request.ContentLength = reqBytes.Length;
        var responseBody = new MemoryStream();
        httpContext.Response.Body = responseBody;

        await proxy.HandleAsync(httpContext, "v1/messages");

        Assert.Equal(StatusCodes.Status402PaymentRequired, httpContext.Response.StatusCode);
        Assert.Equal(0, upstreamCalls);

        responseBody.Position = 0;
        using var json = await JsonDocument.ParseAsync(responseBody, cancellationToken: Ct);
        var root = json.RootElement;
        Assert.Equal(BudgetCodes.BudgetExhausted, root.GetProperty("type").GetString());
        Assert.Equal(StatusCodes.Status402PaymentRequired, root.GetProperty("status").GetInt32());
        Assert.Equal(BudgetCodes.BudgetExhausted, root.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(BudgetCodes.BudgetExhausted, root.GetProperty("error").GetProperty("type").GetString());

        // No debe haberse persistido ningún cargo adicional.
        var contexts = factory.Services.GetRequiredService<IDbContextFactory<PipelineDbContext>>();
        await using var db = await contexts.CreateDbContextAsync(Ct);
        Assert.Single(await db.BudgetCharges.Where(b => b.RunId == runId).ToListAsync(Ct));

        // Fail-closed: si IBudgetLedger.GetRunCostAsync falla, el Gateway responde 503 y hace 0 llamadas al upstream.
        var failingLedger = new ThrowingBudgetLedger();
        using var failClosedProxy = new LlmReverseProxy(
            gateway,
            factory.Services.GetRequiredService<IRunEventSink>(),
            factory.Services.GetRequiredService<IOptionsMonitor<SandboxOptions>>(),
            factory.Services.GetRequiredService<IHttpForwarder>(),
            NullLogger<LlmReverseProxy>.Instance,
            failingLedger,
            transport);

        var failClosedContext = new DefaultHttpContext { RequestServices = factory.Services };
        failClosedContext.Connection.LocalPort = SandboxOptions.DefaultLlmGatewayPort;
        failClosedContext.Request.Method = HttpMethods.Post;
        failClosedContext.Request.Path = "/llm/v1/messages";
        failClosedContext.Request.ContentType = "application/json";
        failClosedContext.Request.Headers[LlmReverseProxy.ApiKeyHeader] = credential.Token;
        failClosedContext.Request.Body = new MemoryStream(reqBytes);
        failClosedContext.Request.ContentLength = reqBytes.Length;
        failClosedContext.Response.Body = new MemoryStream();

        await failClosedProxy.HandleAsync(failClosedContext, "v1/messages");

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, failClosedContext.Response.StatusCode);
        Assert.Equal(0, upstreamCalls);
    }

    [Fact]
    public async Task ClaudeCodeAgent_passes_remaining_max_budget_usd_and_translates_exhausted_states()
    {
        // 1. BuildCommand con presupuesto restante bajo cultura con separador decimal ',' (debe formatear "1.75" y nunca "1,75") vs sin presupuesto.
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            var commaCulture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
            commaCulture.NumberFormat.NumberDecimalSeparator = ",";
            CultureInfo.CurrentCulture = commaCulture;
            var cmdWithBudget = ClaudeCodeAgent.BuildCommand("Implementar", "claude-sonnet-5-5", 1.75m);
            var flagIndex = cmdWithBudget.Arguments.IndexOf("--max-budget-usd");
            Assert.True(flagIndex >= 0);
            Assert.Equal("1.75", cmdWithBudget.Arguments[flagIndex + 1]);

            var cmdSmallBudget = ClaudeCodeAgent.BuildCommand("Implementar", "claude-sonnet-5-5", 0.00001234m);
            var smallIdx = cmdSmallBudget.Arguments.IndexOf("--max-budget-usd");
            Assert.True(smallIdx >= 0);
            Assert.Equal("0.00001234", cmdSmallBudget.Arguments[smallIdx + 1]);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }

        var cmdWithoutBudget = ClaudeCodeAgent.BuildCommand("Implementar", "claude-sonnet-5-5", maxBudgetUsd: null);
        Assert.DoesNotContain("--max-budget-usd", cmdWithoutBudget.Arguments);

        var runId = Guid.CreateVersion7();
        var spec = new Spec("Problema", "Causa", "Solución", ["Criterio"], ["src/App.cs"]);
        var options = Options.Create(new AgentsOptions());
        var tools = new NoOpToolsProvisioner();
        var events = new NoOpRunEventSink();

        // 2. BudgetUsd = 2.50m, gastado 0.75m -> restante 1.75m pasado al CLI.
        var partialLedger = new StubBudgetLedger(new RunCostSummary(0.75m, IsEstimable: true, HasUnpricedModels: false, 1000, 500, 2.50m));
        var capturingSandbox = new RecordingSandboxSession(
            runId,
            new SandboxExecResult(0, """{"is_error":false,"result":"listo"}""", string.Empty, TimedOut: false, Truncated: false),
            hasChanges: true);
        var agent = new ClaudeCodeAgent(tools, events, options, NullLogger<ClaudeCodeAgent>.Instance, partialLedger);
        var configWithBudget = EffectiveConfigCalculator.Calculate(new InstanceLimits { BudgetUsd = 2.50m }, null);

        var okResult = await agent.RunAsync(
            new CodingInput(runId, 1, UntrustedText.From("Título"), UntrustedText.From("Cuerpo"), spec, configWithBudget, capturingSandbox),
            Ct);

        Assert.IsType<CodingResult.Completed>(okResult);
        var executedCommand = Assert.Single(capturingSandbox.ExecutedCommands);
        var idx = executedCommand.Arguments.IndexOf("--max-budget-usd");
        Assert.True(idx >= 0);
        Assert.Equal("1.75", executedCommand.Arguments[idx + 1]);

        // 3. Si el presupuesto ya está agotado antes de lanzar el proceso -> BudgetExhausted con 0 llamadas a ExecAsync.
        var exhaustedLedger = new StubBudgetLedger(new RunCostSummary(2.50m, IsEstimable: true, HasUnpricedModels: false, 5000, 2000, 2.50m));
        var unusedSandbox = new RecordingSandboxSession(
            runId,
            new SandboxExecResult(0, string.Empty, string.Empty, TimedOut: false, Truncated: false),
            hasChanges: false);
        var exhaustedAgent = new ClaudeCodeAgent(tools, events, options, NullLogger<ClaudeCodeAgent>.Instance, exhaustedLedger);

        var preExhausted = await exhaustedAgent.RunAsync(
            new CodingInput(runId, 1, UntrustedText.From("Título"), UntrustedText.From("Cuerpo"), spec, configWithBudget, unusedSandbox),
            Ct);

        Assert.IsType<CodingResult.BudgetExhausted>(preExhausted);
        Assert.Empty(unusedSandbox.ExecutedCommands);

        // 4. Si stdout/stderr contiene codenetix:budget_exhausted -> CodingResult.BudgetExhausted (no Failed).
        var stderrSandbox = new RecordingSandboxSession(
            runId,
            new SandboxExecResult(
                1,
                string.Empty,
                """Error 402: {"error":{"code":"codenetix:budget_exhausted","message":"Presupuesto agotado"}}""",
                TimedOut: false,
                Truncated: false),
            hasChanges: false);

        var translated = await agent.RunAsync(
            new CodingInput(runId, 1, UntrustedText.From("Título"), UntrustedText.From("Cuerpo"), spec, configWithBudget, stderrSandbox),
            Ct);

        Assert.IsType<CodingResult.BudgetExhausted>(translated);

        // 5. Detección en stdout con is_error: true (tanto por codenetix:budget_exhausted como por subtype error_max_budget_usd).
        var stdoutErrorSandbox = new RecordingSandboxSession(
            runId,
            new SandboxExecResult(
                0,
                """{"is_error":true,"result":"Error 402 codenetix:budget_exhausted"}""",
                string.Empty,
                TimedOut: false,
                Truncated: false),
            hasChanges: false);
        var translatedStdout = await agent.RunAsync(
            new CodingInput(runId, 1, UntrustedText.From("Título"), UntrustedText.From("Cuerpo"), spec, configWithBudget, stdoutErrorSandbox),
            Ct);
        Assert.IsType<CodingResult.BudgetExhausted>(translatedStdout);

        var maxBudgetSubtypeSandbox = new RecordingSandboxSession(
            runId,
            new SandboxExecResult(
                0,
                """{"is_error":true,"subtype":"error_max_budget_usd","result":"Reached max budget"}""",
                string.Empty,
                TimedOut: false,
                Truncated: false),
            hasChanges: false);
        var translatedSubtype = await agent.RunAsync(
            new CodingInput(runId, 1, UntrustedText.From("Título"), UntrustedText.From("Cuerpo"), spec, configWithBudget, maxBudgetSubtypeSandbox),
            Ct);
        Assert.IsType<CodingResult.BudgetExhausted>(translatedSubtype);

        // 6. Agotamiento post-ejecución detectado vía budgetLedger sin la cadena en stdout/stderr.
        var transitioningLedger = new SequencedBudgetLedger(
            new RunCostSummary(0.75m, IsEstimable: true, HasUnpricedModels: false, 1000, 500, 2.50m),
            new RunCostSummary(2.55m, IsEstimable: true, HasUnpricedModels: false, 4000, 2000, 2.50m));
        var postExhaustedAgent = new ClaudeCodeAgent(tools, events, options, NullLogger<ClaudeCodeAgent>.Instance, transitioningLedger);
        var cleanExitSandbox = new RecordingSandboxSession(
            runId,
            new SandboxExecResult(0, """{"is_error":false,"result":"cambios parciales"}""", string.Empty, TimedOut: false, Truncated: false),
            hasChanges: true);
        var postExhaustedResult = await postExhaustedAgent.RunAsync(
            new CodingInput(runId, 1, UntrustedText.From("Título"), UntrustedText.From("Cuerpo"), spec, configWithBudget, cleanExitSandbox),
            Ct);
        Assert.IsType<CodingResult.BudgetExhausted>(postExhaustedResult);

        // 7. Sin falso positivo: si el CLI termina con ExitCode == 0 && !isError citando "codenetix:budget_exhausted" en stdout -> Completed.
        var noFalsePositiveSandbox = new RecordingSandboxSession(
            runId,
            new SandboxExecResult(
                0,
                """{"is_error":false,"result":"Implementado soporte para codenetix:budget_exhausted en el gateway"}""",
                string.Empty,
                TimedOut: false,
                Truncated: false),
            hasChanges: true);
        var noFalsePositiveResult = await agent.RunAsync(
            new CodingInput(runId, 1, UntrustedText.From("Título"), UntrustedText.From("Cuerpo"), spec, configWithBudget, noFalsePositiveSandbox),
            Ct);
        Assert.IsType<CodingResult.Completed>(noFalsePositiveResult);
    }

    [Fact]
    public async Task RunOrchestrator_cuts_off_during_Coding_on_BudgetExhausted_result_with_zero_retries()
    {
        var codingCalls = 0;
        var budgetCoder = new DelegateCodingAgent((_, _) =>
        {
            Interlocked.Increment(ref codingCalls);
            return Task.FromResult<CodingResult>(new CodingResult.BudgetExhausted("presupuesto agotado durante la programación"));
        });
        var prPublisher = new RecordingPullRequestPublisher();
        var notifier = new RecordingIssueNotifier();

        await using var baseFactory = new CodeNetixFactory(_dataDirectory, _webRoot, CodeNetixFactory.CompleteSettings);
        await using var factory = baseFactory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            services.AddSingleton<ICodingAgent>(budgetCoder);
            services.AddSingleton<IPullRequestPublisher>(prPublisher);
            services.AddSingleton<IIssueNotifier>(notifier);
        }));

        using var client = CreateAuthorizedClient(factory);
        var runId = await SubmitManualIssueAndWaitForRunAsync(client, "https://github.com/o/r/issues/5201");
        var finalRun = await WaitForTerminalRunAsync(client, runId, TimeSpan.FromSeconds(15));

        Assert.Equal(RunState.Failed, finalRun.State);
        Assert.Equal(FailureReason.BudgetExceeded, finalRun.FailureReason);
        Assert.Equal(1, codingCalls);
        Assert.Contains(finalRun.Events, e => e is SandboxReleased);
        Assert.Null(finalRun.PullRequest);
        Assert.Equal(0, prPublisher.PublishCalls);

        var session = Assert.Single(baseFactory.Sandbox.Sessions, s => s.RunId == runId);
        Assert.True(session.Disposals >= 1);

        var notification = await notifier.WaitForKindAsync(runId, NotificationKind.RunFailed, TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(NotificationKind.RunFailed, notification.Kind);
        Assert.Contains("BudgetExceeded", notification.Body, StringComparison.Ordinal);
        await AssertWorkspaceDeletedAsync(runId);
    }

    [Fact]
    public async Task RunOrchestrator_cuts_off_during_Coding_via_cooperative_cancellation_from_BudgetLedger()
    {
        var enteredCoding = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        var codingCalls = 0;
        var blockingCoder = new DelegateCodingAgent(async (input, ct) =>
        {
            Interlocked.Increment(ref codingCalls);
            enteredCoding.TrySetResult(input.RunId);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new CodingResult.Completed("nunca", "fake");
        });
        var prPublisher = new RecordingPullRequestPublisher();
        var notifier = new RecordingIssueNotifier();

        var settings = new Dictionary<string, string?>(CodeNetixFactory.CompleteSettings)
        {
            ["CODENETIX:Limits:BudgetUsd"] = "0.01",
        };

        await using var baseFactory = new CodeNetixFactory(_dataDirectory, _webRoot, settings);
        await using var factory = baseFactory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            services.AddSingleton<ICodingAgent>(blockingCoder);
            services.AddSingleton<IPullRequestPublisher>(prPublisher);
            services.AddSingleton<IIssueNotifier>(notifier);
        }));

        using var client = CreateAuthorizedClient(factory);
        var runId = await SubmitManualIssueAndWaitForRunAsync(client, "https://github.com/o/r/issues/5202");
        var activeRunId = await enteredCoding.Task.WaitAsync(TimeSpan.FromSeconds(15), Ct);
        Assert.Equal(runId, activeRunId);

        // El libro de presupuesto registra un cargo que supera 0.01 USD y dispara la cancelación cooperativa.
        var ledger = factory.Services.GetRequiredService<IBudgetLedger>();
        var chargeResult = await ledger.ChargeAsync(
            new BudgetChargeRequest(runId, null, "Coder", "Anthropic", "claude-sonnet-5-5", 1000, 500),
            Ct);
        Assert.Equal(BudgetChargeOutcome.Exhausted, chargeResult.Outcome);

        var finalRun = await WaitForTerminalRunAsync(client, runId, TimeSpan.FromSeconds(15));
        Assert.Equal(RunState.Failed, finalRun.State);
        Assert.Equal(FailureReason.BudgetExceeded, finalRun.FailureReason);
        Assert.Equal(1, codingCalls);
        Assert.Contains(finalRun.Events, e => e is SandboxReleased);
        Assert.Null(finalRun.PullRequest);
        Assert.Equal(0, prPublisher.PublishCalls);

        var session = Assert.Single(baseFactory.Sandbox.Sessions, s => s.RunId == runId);
        Assert.True(session.Disposals >= 1);

        var notification = await notifier.WaitForKindAsync(runId, NotificationKind.RunFailed, TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(NotificationKind.RunFailed, notification.Kind);
        Assert.Contains("BudgetExceeded", notification.Body, StringComparison.Ordinal);
        await AssertWorkspaceDeletedAsync(runId);
    }

    [Fact]
    public async Task RunOrchestrator_cuts_off_during_Analyzing_when_BudgetTrackingChatClient_exhausts_budget()
    {
        var prPublisher = new RecordingPullRequestPublisher();
        var notifier = new RecordingIssueNotifier();

        var settings = new Dictionary<string, string?>(CodeNetixFactory.CompleteSettings)
        {
            ["CODENETIX:Limits:BudgetUsd"] = "0.005",
        };

        await using var baseFactory = new CodeNetixFactory(_dataDirectory, _webRoot, settings);
        // 2000 input ($0.006) + 500 output ($0.0075) = $0.0135 >= $0.005 -> agota en Analyzing.
        baseFactory.ChatClient.DefaultUsage = new UsageDetails
        {
            InputTokenCount = 2000,
            OutputTokenCount = 500,
        };

        await using var factory = baseFactory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            services.AddSingleton<IPullRequestPublisher>(prPublisher);
            services.AddSingleton<IIssueNotifier>(notifier);
        }));

        using var client = CreateAuthorizedClient(factory);
        var runId = await SubmitManualIssueAndWaitForRunAsync(client, "https://github.com/o/r/issues/5203");
        var finalRun = await WaitForTerminalRunAsync(client, runId, TimeSpan.FromSeconds(15));

        Assert.Equal(RunState.Failed, finalRun.State);
        Assert.Equal(FailureReason.BudgetExceeded, finalRun.FailureReason);
        Assert.Contains(finalRun.Events, e => e is StateChanged { From: RunState.Analyzing, To: RunState.Failed, FailureReason: FailureReason.BudgetExceeded });
        Assert.DoesNotContain(finalRun.Events, e => e is StateChanged { To: RunState.Coding });
        Assert.Contains(finalRun.Events, e => e is SandboxReleased);
        Assert.Null(finalRun.PullRequest);
        Assert.Equal(0, prPublisher.PublishCalls);

        var session = Assert.Single(baseFactory.Sandbox.Sessions, s => s.RunId == runId);
        Assert.True(session.Disposals >= 1);

        var notification = await notifier.WaitForKindAsync(runId, NotificationKind.RunFailed, TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(NotificationKind.RunFailed, notification.Kind);
        Assert.Contains("BudgetExceeded", notification.Body, StringComparison.Ordinal);
        await AssertWorkspaceDeletedAsync(runId);
    }

    [Fact]
    public async Task RunOrchestrator_cuts_off_during_Analyzing_with_invalid_schema_without_consuming_schema_retries()
    {
        var prPublisher = new RecordingPullRequestPublisher();
        var notifier = new RecordingIssueNotifier();

        var settings = new Dictionary<string, string?>(CodeNetixFactory.CompleteSettings)
        {
            ["CODENETIX:Limits:BudgetUsd"] = "0.005",
        };

        await using var baseFactory = new CodeNetixFactory(_dataDirectory, _webRoot, settings);
        baseFactory.ChatClient.DefaultUsage = new UsageDetails
        {
            InputTokenCount = 2000,
            OutputTokenCount = 500,
        };
        baseFactory.ChatClient.Respond("{}", "{}", "{}");

        await using var factory = baseFactory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            services.AddSingleton<IPullRequestPublisher>(prPublisher);
            services.AddSingleton<IIssueNotifier>(notifier);
        }));

        using var client = CreateAuthorizedClient(factory);
        var runId = await SubmitManualIssueAndWaitForRunAsync(client, "https://github.com/o/r/issues/5206");
        var finalRun = await WaitForTerminalRunAsync(client, runId, TimeSpan.FromSeconds(15));

        Assert.Equal(RunState.Failed, finalRun.State);
        Assert.Equal(FailureReason.BudgetExceeded, finalRun.FailureReason);
        Assert.Single(baseFactory.ChatClient.Calls);
        Assert.DoesNotContain(finalRun.Events, e => e is AnalystOutputRejected);
        Assert.Contains(finalRun.Events, e => e is StateChanged { From: RunState.Analyzing, To: RunState.Failed, FailureReason: FailureReason.BudgetExceeded });
        Assert.DoesNotContain(finalRun.Events, e => e is StateChanged { To: RunState.Coding });
        Assert.Contains(finalRun.Events, e => e is SandboxReleased);
        Assert.Equal(0, prPublisher.PublishCalls);

        var notification = await notifier.WaitForKindAsync(runId, NotificationKind.RunFailed, TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(NotificationKind.RunFailed, notification.Kind);
        Assert.Contains("BudgetExceeded", notification.Body, StringComparison.Ordinal);
        await AssertWorkspaceDeletedAsync(runId);
    }

    [Fact]
    public async Task RunOrchestrator_cuts_off_during_Reviewing_when_BudgetTrackingChatClient_exhausts_budget()
    {
        using var repos = new LocalGitRepos();
        repos.Create("acme", "reviewbudget", new Dictionary<string, string>
        {
            ["README.md"] = "# Review Budget\n",
            ["src/App.cs"] = "public class App { public int Value => 1; }\n",
        });

        var prPublisher = new RecordingPullRequestPublisher();
        var notifier = new RecordingIssueNotifier();

        // Con gpt-4o ($2.5/1M input, $10/1M output): cada llamada de 2000 in + 500 out cuesta $0.005 + $0.005 = $0.01.
        // BudgetUsd = 0.015: el Analista consume $0.01 (queda $0.005), y el primer Revisor lleva el total a >= $0.02 -> Exhausted en Reviewing.
        var settings = new Dictionary<string, string?>(CodeNetixFactory.CompleteSettings)
        {
            [CodeNetixFactory.CloneBaseUrlKey] = repos.BaseUrl,
            ["CODENETIX:Limits:BudgetUsd"] = "0.015",
        };

        await using var baseFactory = new CodeNetixFactory(_dataDirectory, _webRoot, settings);
        baseFactory.ChatClient.DefaultUsage = new UsageDetails
        {
            InputTokenCount = 2000,
            OutputTokenCount = 500,
        };

        await using var factory = baseFactory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            services.AddSingleton<IPullRequestPublisher>(prPublisher);
            services.AddSingleton<IIssueNotifier>(notifier);
        }));

        baseFactory.Sandbox.OnSessionCreated = session =>
        {
            session.ExportWorkspaceHandler = (dest, ct) => WriteTarAsync(
                dest,
                [
                    ("workspace/README.md", "# Review Budget\n"),
                    ("workspace/src/App.cs", "public class App { public int Value => 2; }\n"),
                ],
                ct);
        };

        using var client = CreateAuthorizedClient(factory);
        var runId = await SubmitManualIssueAndWaitForRunAsync(client, "https://github.com/acme/reviewbudget/issues/5204");
        var finalRun = await WaitForTerminalRunAsync(client, runId, TimeSpan.FromSeconds(15));

        Assert.Equal(RunState.Failed, finalRun.State);
        Assert.Equal(FailureReason.BudgetExceeded, finalRun.FailureReason);
        Assert.Contains(finalRun.Events, e => e is StateChanged { To: RunState.Reviewing });
        Assert.Contains(finalRun.Events, e => e is StateChanged { From: RunState.Reviewing, To: RunState.Failed, FailureReason: FailureReason.BudgetExceeded });
        Assert.Contains(finalRun.Events, e => e is SandboxReleased);
        Assert.Null(finalRun.PullRequest);
        Assert.Equal(0, prPublisher.PublishCalls);

        var session = Assert.Single(baseFactory.Sandbox.Sessions, s => s.RunId == runId);
        Assert.True(session.Disposals >= 1);

        var notification = await notifier.WaitForKindAsync(runId, NotificationKind.RunFailed, TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(NotificationKind.RunFailed, notification.Kind);
        Assert.Contains("BudgetExceeded", notification.Body, StringComparison.Ordinal);
        await AssertWorkspaceDeletedAsync(runId);
    }

    [Fact]
    public async Task RunOrchestrator_preserves_host_shutdown_precedence_over_budget_and_user_cancel()
    {
        var enteredCoding = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockingCoder = new DelegateCodingAgent(async (input, ct) =>
        {
            enteredCoding.TrySetResult(input.RunId);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new CodingResult.Completed("nunca", "fake");
        });

        await using var baseFactory = new CodeNetixFactory(_dataDirectory, _webRoot, CodeNetixFactory.CompleteSettings);
        await using var factory = baseFactory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            services.AddSingleton<ICodingAgent>(blockingCoder);
        }));
        _ = factory.Server;

        var runId = await SeedRunAsync(factory, budgetLimitUsd: null, preFreezeConfig: false);
        var orchestrator = factory.Services.GetRequiredService<IRunOrchestrator>();

        using var hostStoppingCts = new CancellationTokenSource();
        var runTask = orchestrator.RunAsync(runId, hostStoppingCts.Token);

        await enteredCoding.Task.WaitAsync(TimeSpan.FromSeconds(15), Ct);
        hostStoppingCts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);

        // La Ejecución NO debe quedar marcada como BudgetExceeded ni Cancelled.
        var contexts = factory.Services.GetRequiredService<IDbContextFactory<PipelineDbContext>>();
        await using var db = await contexts.CreateDbContextAsync(Ct);
        var persisted = await db.Runs.SingleAsync(r => r.Id == runId, Ct);
        Assert.False(persisted.State.IsTerminal());
        Assert.Null(persisted.FailureReason);
    }

    private static HttpClient CreateAuthorizedClient(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CodeNetixFactory.TestAccessToken);
        return client;
    }

    private static async Task<Guid> SubmitManualIssueAndWaitForRunAsync(HttpClient client, string issueUrl)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/events/manual",
            new { url = issueUrl, requestId = Guid.NewGuid().ToString() },
            Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var runs = await client.GetFromJsonAsync<List<RunSummary>>("/api/runs", WebJson, Ct);
            if (runs is { Count: > 0 })
            {
                return runs[0].RunId;
            }

            await Task.Delay(25, Ct);
        }

        throw new TimeoutException("No apareció la Ejecución tras enviar el evento manual.");
    }

    private static async Task<RunResponse> WaitForTerminalRunAsync(HttpClient client, Guid runId, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var detail = await client.GetFromJsonAsync<RunResponse>($"/api/runs/{runId}", WebJson, Ct);
            if (detail is not null
                && detail.State.IsTerminal()
                && detail.Events.Any(e => e is SandboxReleased))
            {
                return detail;
            }

            await Task.Delay(30, Ct);
        }

        throw new TimeoutException($"La Ejecución {runId} no alcanzó un Estado terminal con SandboxReleased en {timeout}.");
    }

    private static int _seedSequence;

    private static async Task<Guid> SeedRunAsync(
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory,
        decimal? budgetLimitUsd,
        bool preFreezeConfig = true)
    {
        var contexts = factory.Services.GetRequiredService<IDbContextFactory<PipelineDbContext>>();
        await using var db = await contexts.CreateDbContextAsync(Ct);
        var now = DateTimeOffset.UtcNow;
        var issueNumber = 5200 + Interlocked.Increment(ref _seedSequence);
        var (run, created, initial) = PipelineRun.Create(Guid.CreateVersion7(now), $"github:o/r#{issueNumber}", now);
        if (preFreezeConfig)
        {
            run.RecordBaseSha("0123456789abcdef0123456789abcdef01234567");
            run.FreezeConfiguration(EffectiveConfigCalculator.Calculate(new InstanceLimits { BudgetUsd = budgetLimitUsd }, null));
        }

        db.Runs.Add(run);
        var log = factory.Services.GetRequiredService<RunEventLog>();
        await log.AddAsync(db, run.Id, created, Ct);
        await log.AddAsync(db, run.Id, initial, Ct);
        await log.SaveAndPublishAsync(db, Ct);
        return run.Id;
    }

    private static async Task WriteTarAsync(Stream destination, IReadOnlyList<(string Path, string Content)> files, CancellationToken ct)
    {
        await using var writer = new TarWriter(destination, TarEntryFormat.Pax, leaveOpen: true);
        foreach (var (path, content) in files)
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            var entry = new PaxTarEntry(TarEntryType.RegularFile, path)
            {
                DataStream = new MemoryStream(bytes),
            };
            await writer.WriteEntryAsync(entry, ct);
        }
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

    private async Task AssertWorkspaceDeletedAsync(Guid runId)
    {
        var workspaceDir = Path.Combine(_dataDirectory, "workspaces", RunShortId.Of(runId));
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (Directory.Exists(workspaceDir) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(25, Ct);
        }

        Assert.False(Directory.Exists(workspaceDir), $"El directorio del workspace '{workspaceDir}' debe borrarse en finally.");
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }

    private sealed class NoOpToolsProvisioner : ISandboxToolsProvisioner
    {
        public Task EnsureClaudeCliAsync(ISandboxSession session, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NoOpRunEventSink : IRunEventSink
    {
        public Task<RunEvent> AppendAsync(Guid runId, Guid? stageId, RunEvent payload, CancellationToken cancellationToken) =>
            Task.FromResult(payload);
    }

    private sealed class StubBudgetLedger(RunCostSummary summary) : IBudgetLedger
    {
        public Task<BudgetChargeResult> ChargeAsync(BudgetChargeRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new BudgetChargeResult(
                summary.IsExhausted ? BudgetChargeOutcome.Exhausted : BudgetChargeOutcome.Recorded,
                null,
                summary.EstimatedCostUsd,
                summary.HasUnpricedModels,
                summary.BudgetLimitUsd,
                summary.RemainingUsd));

        public Task<RunCostSummary> GetRunCostAsync(Guid runId, CancellationToken cancellationToken) =>
            Task.FromResult(summary);
    }

    private sealed class SequencedBudgetLedger(params RunCostSummary[] summaries) : IBudgetLedger
    {
        private int _index = -1;

        public Task<BudgetChargeResult> ChargeAsync(BudgetChargeRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<RunCostSummary> GetRunCostAsync(Guid runId, CancellationToken cancellationToken)
        {
            var next = Math.Min(Interlocked.Increment(ref _index), summaries.Length - 1);
            return Task.FromResult(summaries[next]);
        }
    }

    private sealed class ThrowingBudgetLedger : IBudgetLedger
    {
        public Task<BudgetChargeResult> ChargeAsync(BudgetChargeRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("BD no disponible");

        public Task<RunCostSummary> GetRunCostAsync(Guid runId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("BD no disponible");
    }

    private sealed class RecordingSandboxSession(Guid runId, SandboxExecResult execResult, bool hasChanges) : ISandboxSession
    {
        public Guid RunId { get; } = runId;

        public string ContainerId => "fake-container";

        public string Image => "fake-image";

        public List<SandboxCommand> ExecutedCommands { get; } = [];

        public Task<SandboxExecResult> ExecAsync(SandboxCommand command, CancellationToken cancellationToken)
        {
            ExecutedCommands.Add(command);
            return Task.FromResult(execResult);
        }

        public Task<bool> HasChangesAsync(CancellationToken cancellationToken) => Task.FromResult(hasChanges);

        public Task ExportWorkspaceAsync(Stream destination, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync() => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class DelegateCodingAgent(Func<CodingInput, CancellationToken, Task<CodingResult>> handler) : ICodingAgent
    {
        public Task<CodingResult> RunAsync(CodingInput input, CancellationToken cancellationToken) => handler(input, cancellationToken);
    }

    private sealed class RecordingPullRequestPublisher : IPullRequestPublisher
    {
        private int _calls;

        public int PublishCalls => Volatile.Read(ref _calls);

        public Task<PullRequestInfo> PublishAsync(PullRequestRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new PullRequestInfo(
                Number: 1,
                Url: $"https://github.com/{request.Owner}/{request.Repo}/pull/1",
                BranchName: $"codenetix/{request.RunId:D}",
                CommitSha: "0123456789abcdef0123456789abcdef01234567",
                Title: request.Title,
                CreatedAt: DateTimeOffset.UtcNow));
        }

        public Task<PullRequestInfo?> FindByRunAsync(Guid runId, CancellationToken cancellationToken) =>
            Task.FromResult<PullRequestInfo?>(null);
    }

    private sealed class RecordingIssueNotifier : IIssueNotifier
    {
        private readonly ConcurrentQueue<(Guid RunId, NotificationKind Kind, long Number, string Body)> _items = new();
        private readonly SemaphoreSlim _signal = new(0);

        public Task<IssueNotification> NotifyAsync(
            Guid runId,
            string owner,
            string repo,
            long number,
            NotificationKind kind,
            string body,
            CancellationToken cancellationToken)
        {
            _items.Enqueue((runId, kind, number, body));
            _signal.Release();
            return Task.FromResult(IssueNotification.Posted($"https://github.com/{owner}/{repo}/issues/{number}#issuecomment-1"));
        }

        public async Task<(Guid RunId, NotificationKind Kind, long Number, string Body)> WaitForKindAsync(
            Guid runId,
            NotificationKind kind,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            var deadline = DateTimeOffset.UtcNow.Add(timeout);
            while (DateTimeOffset.UtcNow < deadline)
            {
                foreach (var item in _items)
                {
                    if (item.RunId == runId && item.Kind == kind)
                    {
                        return item;
                    }
                }

                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                await _signal.WaitAsync(remaining, cancellationToken);
            }

            throw new TimeoutException($"No se recibió la notificación {kind} para la Ejecución {runId}.");
        }
    }
}
