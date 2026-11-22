using System.Formats.Tar;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using CodeNetix.Agents.ChatClients;
using CodeNetix.Contracts;
using CodeNetix.Host.Configuration;
using CodeNetix.Host.Features.GetRun;
using CodeNetix.Persistence;
using CodeNetix.Pipeline.Data;
using CodeNetix.Pipeline.Domain;
using CodeNetix.Pipeline.Features.Budget;
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
/// Pruebas de la Historia 5.1 (Libro de presupuesto y coste visible, AD-15):
/// Gateway (<see cref="LlmReverseProxy"/>), middleware obligatorio <see cref="IChatClient"/>
/// (<see cref="IAnalyst"/> y <see cref="IReviewerAgent"/>), modelos sin precio conocido (locales),
/// overrides de configuración <c>CODENETIX__Pricing__*</c>, API (<c>GET /api/runs</c> y
/// <c>GET /api/runs/{id}</c>) y cuerpo del PR (<see cref="PullRequestBodyBuilder"/>).
/// </summary>
public sealed class BudgetLedgerTests : IDisposable
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "codenetix-budget-" + Guid.NewGuid().ToString("N"));
    private readonly string _dataDirectory;
    private readonly string _webRoot;

    public BudgetLedgerTests()
    {
        _dataDirectory = Path.Combine(_root, "data");
        _webRoot = Path.Combine(_root, "wwwroot");
        Directory.CreateDirectory(_webRoot);
        File.WriteAllText(Path.Combine(_webRoot, "index.html"), "<!doctype html><html><body>Panel</body></html>");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string DatabasePath => Path.Combine(_dataDirectory, PersistenceOptions.DatabaseFileName);

    [Fact]
    public async Task Gateway_records_priced_model_charge_once_and_exposes_cost_in_runs_api()
    {
        await using var factory = new CodeNetixFactory(_dataDirectory, _webRoot, CodeNetixFactory.CompleteSettings);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", CodeNetixFactory.TestAccessToken);
        var runId = await SeedRunAsync(factory, budgetLimitUsd: 5.0m);

        var gateway = factory.Services.GetRequiredService<ISandboxGateway>();
        var credential = gateway.Register(runId);

        var upstreamResponse = """{"id":"msg_1","model":"claude-sonnet-5-5","usage":{"input_tokens":1000,"output_tokens":500}}""";
        using var transport = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(upstreamResponse, Encoding.UTF8, "application/json"),
            });

        using var proxy = new LlmReverseProxy(
            gateway,
            factory.Services.GetRequiredService<IRunEventSink>(),
            factory.Services.GetRequiredService<IOptionsMonitor<SandboxOptions>>(),
            factory.Services.GetRequiredService<IHttpForwarder>(),
            NullLogger<LlmReverseProxy>.Instance,
            factory.Services.GetRequiredService<IBudgetLedger>(),
            transport);

        var httpContext = new DefaultHttpContext
        {
            RequestServices = factory.Services,
        };
        httpContext.Connection.LocalPort = SandboxOptions.DefaultLlmGatewayPort;
        httpContext.Request.Method = HttpMethods.Post;
        httpContext.Request.Path = "/llm/v1/messages";
        httpContext.Request.ContentType = "application/json";
        httpContext.Request.Headers[LlmReverseProxy.ApiKeyHeader] = credential.Token;
        var reqBytes = Encoding.UTF8.GetBytes("""{"model":"claude-sonnet-5-5","messages":[]}""");
        httpContext.Request.Body = new MemoryStream(reqBytes);
        httpContext.Request.ContentLength = reqBytes.Length;
        httpContext.Response.Body = new MemoryStream();

        await proxy.HandleAsync(httpContext, "v1/messages");

        Assert.Equal(StatusCodes.Status200OK, httpContext.Response.StatusCode);

        // 1. Fila en run_budget con CostUsd > 0 (1000 * $3/1M + 500 * $15/1M = 0.003 + 0.0075 = 0.0105 USD).
        var contexts = factory.Services.GetRequiredService<IDbContextFactory<PipelineDbContext>>();
        await using (var db = await contexts.CreateDbContextAsync(Ct))
        {
            var charge = Assert.Single(await db.BudgetCharges.Where(b => b.RunId == runId).ToListAsync(Ct));
            Assert.Equal(LlmReverseProxy.CoderRole, charge.Role);
            Assert.Equal("claude-sonnet-5-5", charge.Model);
            Assert.Equal(1000, charge.InputTokens);
            Assert.Equal(500, charge.OutputTokens);
            Assert.True(charge.IsPriced);
            Assert.Equal(0.0105m, charge.CostUsd);
        }

        // 2. Exactamente 1 evento TokensConsumed en run_events (sin duplicados).
        var reader = factory.Services.GetRequiredService<IRunReader>();
        var events = await reader.GetEventsAsync(runId, 0, Ct);
        var tokenEvent = Assert.Single(events.OfType<TokensConsumed>());
        Assert.Equal(LlmReverseProxy.CoderRole, tokenEvent.Role);
        Assert.Equal("claude-sonnet-5-5", tokenEvent.Model);
        Assert.Equal(1000, tokenEvent.InputTokens);
        Assert.Equal(500, tokenEvent.OutputTokens);

        // 3. GET /api/runs y GET /api/runs/{id} muestran cost.isEstimable == true y estimatedCostUsd == 0.0105.
        var list = await client.GetFromJsonAsync<List<RunSummary>>("/api/runs", WebJson, Ct);
        Assert.NotNull(list);
        var listed = Assert.Single(list, r => r.RunId == runId);
        Assert.NotNull(listed.Cost);
        Assert.True(listed.Cost!.IsEstimable);
        Assert.False(listed.Cost.HasUnpricedModels);
        Assert.Equal(0.0105m, listed.Cost.EstimatedCostUsd);
        Assert.Equal(1000, listed.Cost.TotalInputTokens);
        Assert.Equal(500, listed.Cost.TotalOutputTokens);
        Assert.Equal(5.0m, listed.Cost.BudgetLimitUsd);

        var detail = await client.GetFromJsonAsync<RunResponse>($"/api/runs/{runId}", WebJson, Ct);
        Assert.NotNull(detail);
        Assert.NotNull(detail.Cost);
        Assert.True(detail.Cost!.IsEstimable);
        Assert.Equal(0.0105m, detail.Cost.EstimatedCostUsd);
    }

    [Fact]
    public async Task ChatClient_middleware_records_usage_for_analyst_and_reviewer_with_stage_id()
    {
        await using var factory = new CodeNetixFactory(_dataDirectory, _webRoot, CodeNetixFactory.CompleteSettings);
        _ = factory.Server;

        factory.ChatClient.DefaultUsage = new UsageDetails
        {
            InputTokenCount = 2000,
            OutputTokenCount = 400,
        };

        var runId = await SeedRunAsync(factory, budgetLimitUsd: null);
        var stageId = Guid.CreateVersion7();
        var config = EffectiveConfigCalculator.Calculate(new CodeNetix.Pipeline.InstanceLimits(), null);

        var analyst = factory.Services.GetRequiredService<IAnalyst>();
        var analysisResult = await analyst.AnalyzeAsync(
            new AnalysisInput(runId, UntrustedText.From("Título issue"), UntrustedText.From("Cuerpo issue"), ["src/App.cs"], false, config),
            Ct);
        Assert.IsType<AnalysisResult.Ok>(analysisResult);

        var reviewer = factory.Services.GetRequiredService<IReviewerAgent>();
        var spec = ((AnalysisResult.Ok)analysisResult).Spec;
        var testReport = new TestReport(true, 5, 5, 0, 0, ["Tests.New"], [], TimeSpan.FromMilliseconds(100));
        var diff = new DiffResult("+var x = 1;", ["src/App.cs"]);
        var reviewResult = await reviewer.ReviewAsync(
            new ReviewInput(runId, stageId, 1, ReviewerKind.Security, spec, diff, testReport, config),
            Ct);
        Assert.Equal(VerdictDecision.Approved, reviewResult.Verdict.Decision);

        var contexts = factory.Services.GetRequiredService<IDbContextFactory<PipelineDbContext>>();
        await using (var db = await contexts.CreateDbContextAsync(Ct))
        {
            var charges = await db.BudgetCharges
                .Where(b => b.RunId == runId)
                .OrderBy(b => b.CreatedAt)
                .ToListAsync(Ct);

            Assert.Equal(2, charges.Count);

            var analystCharge = Assert.Single(charges, c => c.Role == "Analyst");
            Assert.Null(analystCharge.StageId);
            Assert.Equal(2000, analystCharge.InputTokens);
            Assert.Equal(400, analystCharge.OutputTokens);
            Assert.True(analystCharge.IsPriced);
            Assert.True(analystCharge.CostUsd > 0);

            var reviewerCharge = Assert.Single(charges, c => c.Role == "Reviewer:security");
            Assert.Equal(stageId, reviewerCharge.StageId);
            Assert.Equal(2000, reviewerCharge.InputTokens);
            Assert.Equal(400, reviewerCharge.OutputTokens);
            Assert.True(reviewerCharge.IsPriced);
            Assert.True(reviewerCharge.CostUsd > 0);
        }

        var reader = factory.Services.GetRequiredService<IRunReader>();
        var tokenEvents = (await reader.GetEventsAsync(runId, 0, Ct)).OfType<TokensConsumed>().ToList();
        Assert.Equal(2, tokenEvents.Count);
        Assert.Contains(tokenEvents, e => e.Role == "Analyst" && e.StageId is null);
        Assert.Contains(tokenEvents, e => e.Role == "Reviewer:security" && e.StageId == stageId);
    }

    [Fact]
    public async Task Unpriced_local_model_marks_run_as_not_estimable_in_db_api_and_pr_body()
    {
        await using var factory = new CodeNetixFactory(_dataDirectory, _webRoot, CodeNetixFactory.CompleteSettings);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", CodeNetixFactory.TestAccessToken);
        var runId = await SeedRunAsync(factory, budgetLimitUsd: 10.0m);

        var ledger = factory.Services.GetRequiredService<IBudgetLedger>();
        var result = await ledger.ChargeAsync(
            new BudgetChargeRequest(runId, null, "Analyst", "Ollama", "qwen2.5-coder:32b", 1500, 600),
            Ct);

        Assert.Equal(BudgetChargeOutcome.Recorded, result.Outcome);
        Assert.Null(result.ChargeUsd);
        Assert.Null(result.TotalEstimatedCostUsd);
        Assert.True(result.HasUnpricedModels);

        var contexts = factory.Services.GetRequiredService<IDbContextFactory<PipelineDbContext>>();
        await using (var db = await contexts.CreateDbContextAsync(Ct))
        {
            var row = Assert.Single(await db.BudgetCharges.Where(b => b.RunId == runId).ToListAsync(Ct));
            Assert.False(row.IsPriced);
            Assert.Null(row.CostUsd);
        }

        var detail = await client.GetFromJsonAsync<RunResponse>($"/api/runs/{runId}", WebJson, Ct);
        Assert.NotNull(detail);
        Assert.NotNull(detail.Cost);
        Assert.False(detail.Cost!.IsEstimable);
        Assert.True(detail.Cost.HasUnpricedModels);
        Assert.Null(detail.Cost.EstimatedCostUsd);
        Assert.Equal(1500, detail.Cost.TotalInputTokens);
        Assert.Equal(600, detail.Cost.TotalOutputTokens);

        var body = BuildSamplePrBody(detail.Cost);
        Assert.Contains("no estimable", body, StringComparison.Ordinal);
        Assert.DoesNotContain("$0.0000 USD", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pricing_override_calculates_configured_cost_and_negative_pricing_fails_validation()
    {
        var settings = new Dictionary<string, string?>(CodeNetixFactory.CompleteSettings)
        {
            ["CODENETIX:Pricing:mi-modelo:InputPerMillionUsd"] = "2.5",
            ["CODENETIX:Pricing:mi-modelo:OutputPerMillionUsd"] = "10",
        };

        await using var factory = new CodeNetixFactory(_dataDirectory, _webRoot, settings);
        _ = factory.Server;
        var runId = await SeedRunAsync(factory, budgetLimitUsd: null);

        var ledger = factory.Services.GetRequiredService<IBudgetLedger>();
        var result = await ledger.ChargeAsync(
            new BudgetChargeRequest(runId, null, "Analyst", "Custom", "mi-modelo", 1_000_000, 500_000),
            Ct);

        Assert.Equal(BudgetChargeOutcome.Recorded, result.Outcome);
        // 1M * 2.5 + 0.5M * 10 = 2.5 + 5.0 = 7.5 USD
        Assert.Equal(7.5m, result.ChargeUsd);
        Assert.Equal(7.5m, result.TotalEstimatedCostUsd);
        Assert.False(result.HasUnpricedModels);

        var invalidOptions = new InstanceOptions
        {
            GitHub = new GitHubInstanceOptions { Token = "ghp_validtoken" },
            Providers = new Dictionary<string, ProviderInstanceOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["Anthropic"] = new() { ApiKey = "sk-ant-validkey" },
            },
            Pricing = new Dictionary<string, ModelPricingInstanceOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["mi-modelo"] = new() { InputPerMillionUsd = -1m, OutputPerMillionUsd = -2m },
            },
        };

        var errors = InstanceOptionsValidator.Validate(invalidOptions);
        Assert.Contains(errors, e => e.Contains("CODENETIX__Pricing__mi-modelo__InputPerMillionUsd", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("CODENETIX__Pricing__mi-modelo__OutputPerMillionUsd", StringComparison.Ordinal));
    }

    [Fact]
    public void PullRequestBodyBuilder_formats_estimable_unpriced_and_unavailable_cost()
    {
        var estimable = new RunCostSummary(0.0123m, IsEstimable: true, HasUnpricedModels: false, 1200, 350, null);
        var unpriced = new RunCostSummary(null, IsEstimable: false, HasUnpricedModels: true, 800, 200, null);
        var empty = RunCostSummary.Empty;

        var estimableBody = BuildSamplePrBody(estimable);
        var unpricedBody = BuildSamplePrBody(unpriced);
        var emptyBody = BuildSamplePrBody(empty);
        var nullBody = BuildSamplePrBody(null);

        Assert.Contains("$0.0123 USD (1200 tokens entrada / 350 tokens salida)", estimableBody, StringComparison.Ordinal);
        Assert.Contains("no estimable (800 tokens entrada / 200 tokens salida; modelo sin precio conocido)", unpricedBody, StringComparison.Ordinal);
        Assert.Contains("- Coste: no disponible", emptyBody, StringComparison.Ordinal);
        Assert.Contains("- Coste: no disponible", nullBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mixed_priced_and_unpriced_models_mark_run_not_estimable_with_combined_tokens()
    {
        await using var factory = new CodeNetixFactory(_dataDirectory, _webRoot, CodeNetixFactory.CompleteSettings);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", CodeNetixFactory.TestAccessToken);
        var runId = await SeedRunAsync(factory, budgetLimitUsd: 5.0m);

        var ledger = factory.Services.GetRequiredService<IBudgetLedger>();
        var first = await ledger.ChargeAsync(
            new BudgetChargeRequest(runId, null, "Analyst", "Anthropic", "claude-sonnet-5-5", 1000, 500),
            Ct);
        Assert.True(first.ChargeUsd > 0);
        Assert.Equal(0.0105m, first.TotalEstimatedCostUsd);
        Assert.False(first.HasUnpricedModels);
        Assert.Equal(4.9895m, first.RemainingUsd);

        var second = await ledger.ChargeAsync(
            new BudgetChargeRequest(runId, null, "Coder", "Ollama", "qwen2.5-coder:32b", 2000, 300),
            Ct);
        Assert.Null(second.ChargeUsd);
        Assert.Null(second.TotalEstimatedCostUsd);
        Assert.True(second.HasUnpricedModels);
        Assert.Null(second.RemainingUsd);

        var detail = await client.GetFromJsonAsync<RunResponse>($"/api/runs/{runId}", WebJson, Ct);
        Assert.NotNull(detail?.Cost);
        Assert.False(detail!.Cost!.IsEstimable);
        Assert.True(detail.Cost.HasUnpricedModels);
        Assert.Null(detail.Cost.EstimatedCostUsd);
        Assert.Equal(3000, detail.Cost.TotalInputTokens);
        Assert.Equal(800, detail.Cost.TotalOutputTokens);
        Assert.Equal(5.0m, detail.Cost.BudgetLimitUsd);

        var body = BuildSamplePrBody(detail.Cost);
        Assert.Contains("no estimable (3000 tokens entrada / 800 tokens salida; modelo sin precio conocido)", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Gateway_zero_or_missing_usage_creates_no_charge_and_sse_response_records_charge()
    {
        await using var factory = new CodeNetixFactory(_dataDirectory, _webRoot, CodeNetixFactory.CompleteSettings);
        _ = factory.Server;
        var runId = await SeedRunAsync(factory, budgetLimitUsd: null);
        var gateway = factory.Services.GetRequiredService<ISandboxGateway>();
        var credential = gateway.Register(runId);

        // 1. Respuesta sin usage y respuesta con usage 0/0 -> 0 cargos y 0 TokensConsumed.
        foreach (var body in new[]
        {
            """{"id":"msg_no_usage","model":"claude-sonnet-5-5"}""",
            """{"id":"msg_zero","model":"claude-sonnet-5-5","usage":{"input_tokens":0,"output_tokens":0}}""",
        })
        {
            await SendThroughGatewayAsync(factory, credential.Token, body, "application/json");
        }

        var contexts = factory.Services.GetRequiredService<IDbContextFactory<PipelineDbContext>>();
        await using (var db = await contexts.CreateDbContextAsync(Ct))
        {
            Assert.Empty(await db.BudgetCharges.Where(b => b.RunId == runId).ToListAsync(Ct));
        }

        var reader = factory.Services.GetRequiredService<IRunReader>();
        Assert.Empty((await reader.GetEventsAsync(runId, 0, Ct)).OfType<TokensConsumed>());

        // 2. Respuesta SSE (text/event-stream) -> registra 1 cargo y 1 TokensConsumed.
        var ssePayload = string.Join('\n',
            "event: message_start",
            """data: {"type":"message_start","message":{"model":"claude-sonnet-5-5","usage":{"input_tokens":1200,"output_tokens":0}}}""",
            "",
            "event: message_delta",
            """data: {"type":"message_delta","usage":{"output_tokens":300}}""",
            "",
            "");

        await SendThroughGatewayAsync(factory, credential.Token, ssePayload, "text/event-stream");

        await using (var db = await contexts.CreateDbContextAsync(Ct))
        {
            var charge = Assert.Single(await db.BudgetCharges.Where(b => b.RunId == runId).ToListAsync(Ct));
            Assert.Equal(1200, charge.InputTokens);
            Assert.Equal(300, charge.OutputTokens);
            Assert.True(charge.IsPriced);
            // 1200 * 3/1M + 300 * 15/1M = 0.0036 + 0.0045 = 0.0081
            Assert.Equal(0.0081m, charge.CostUsd);
        }

        Assert.Single((await reader.GetEventsAsync(runId, 0, Ct)).OfType<TokensConsumed>());
    }

    [Fact]
    public async Task Analyst_and_reviewer_succeed_without_charges_when_usage_is_null()
    {
        await using var factory = new CodeNetixFactory(_dataDirectory, _webRoot, CodeNetixFactory.CompleteSettings);
        _ = factory.Server;
        factory.ChatClient.DefaultUsage = null;

        var runId = await SeedRunAsync(factory, budgetLimitUsd: null);
        var config = EffectiveConfigCalculator.Calculate(new CodeNetix.Pipeline.InstanceLimits(), null);

        var analyst = factory.Services.GetRequiredService<IAnalyst>();
        var analysis = await analyst.AnalyzeAsync(
            new AnalysisInput(runId, UntrustedText.From("Título"), UntrustedText.From("Cuerpo"), ["src/App.cs"], false, config),
            Ct);
        var ok = Assert.IsType<AnalysisResult.Ok>(analysis);

        var reviewer = factory.Services.GetRequiredService<IReviewerAgent>();
        var review = await reviewer.ReviewAsync(
            new ReviewInput(runId, Guid.CreateVersion7(), 1, ReviewerKind.Quality, ok.Spec, new DiffResult("+x", ["src/App.cs"]), new TestReport(true, 1, 1, 0, 0, ["T"], [], TimeSpan.Zero), config),
            Ct);
        Assert.Equal(VerdictDecision.Approved, review.Verdict.Decision);

        var contexts = factory.Services.GetRequiredService<IDbContextFactory<PipelineDbContext>>();
        await using var db = await contexts.CreateDbContextAsync(Ct);
        Assert.Empty(await db.BudgetCharges.Where(b => b.RunId == runId).ToListAsync(Ct));
    }

    [Fact]
    public async Task BudgetTrackingChatClient_streaming_records_max_cumulative_usage_even_on_early_exit()
    {
        await using var factory = new CodeNetixFactory(_dataDirectory, _webRoot, CodeNetixFactory.CompleteSettings);
        _ = factory.Server;
        var runId = await SeedRunAsync(factory, budgetLimitUsd: null);
        var stageId = Guid.CreateVersion7();
        var ledger = factory.Services.GetRequiredService<IBudgetLedger>();

        var updates = new[]
        {
            new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(new UsageDetails { InputTokenCount = 500, OutputTokenCount = 100 })]) { ModelId = "claude-sonnet-5-5" },
            new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(new UsageDetails { InputTokenCount = 500, OutputTokenCount = 250 })]) { ModelId = "claude-sonnet-5-5" },
            new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(new UsageDetails { InputTokenCount = 500, OutputTokenCount = 400 })]) { ModelId = "claude-sonnet-5-5" },
        };

        using var trackingClient = new BudgetTrackingChatClient(
            new StreamingStubChatClient(updates),
            ledger,
            runId,
            stageId,
            "Analyst",
            "Anthropic",
            "claude-sonnet-5-5",
            NullLogger.Instance);

        var received = 0;
        await foreach (var _ in trackingClient.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hola")], cancellationToken: Ct))
        {
            received++;
            if (received == 2)
            {
                break; // Salida anticipada: el finally debe registrar el máximo acumulado (500 in, 250 out).
            }
        }

        var cost = await ledger.GetRunCostAsync(runId, Ct);
        Assert.True(cost.IsEstimable);
        Assert.Equal(500, cost.TotalInputTokens);
        Assert.Equal(250, cost.TotalOutputTokens);
    }

    [Theory]
    [InlineData("-1", "5")]
    [InlineData("2.5", null)]
    [InlineData(null, "10")]
    public async Task Host_starts_in_Diagnostic_mode_when_pricing_is_negative_or_partial(string? inputPrice, string? outputPrice)
    {
        var settings = new Dictionary<string, string?>(CodeNetixFactory.CompleteSettings);
        if (inputPrice is not null)
        {
            settings["CODENETIX:Pricing:mi-modelo:InputPerMillionUsd"] = inputPrice;
        }

        if (outputPrice is not null)
        {
            settings["CODENETIX:Pricing:mi-modelo:OutputPerMillionUsd"] = outputPrice;
        }

        await using var factory = new CodeNetixFactory(_dataDirectory, _webRoot, settings);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", CodeNetixFactory.TestAccessToken);

        using var healthResponse = await client.GetAsync("/api/health", Ct);
        Assert.Equal(HttpStatusCode.OK, healthResponse.StatusCode);
        using var healthDoc = JsonDocument.Parse(await healthResponse.Content.ReadAsStringAsync(Ct));
        Assert.Equal("Diagnostic", healthDoc.RootElement.GetProperty("status").GetString());

        using var manualResponse = await client.PostAsJsonAsync(
            "/api/events/manual",
            new { url = "https://github.com/acme/app/issues/1", requestId = Guid.NewGuid().ToString() },
            Ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, manualResponse.StatusCode);
    }

    [Fact]
    public void ModelPricingCatalog_resolves_overrides_case_insensitively_date_suffixes_official_prefixes_and_latest()
    {
        var overrides = new Dictionary<string, ModelPricing>(StringComparer.OrdinalIgnoreCase)
        {
            ["claude-sonnet-5-5"] = new(1.25m, 6.25m),
        };

        // Override sobre modelo estándar + mayúsculas + sufijo de fecha.
        Assert.True(ModelPricingCatalog.TryResolve("CLAUDE-SONNET-5-5", overrides, out var overridden));
        Assert.Equal(1.25m, overridden.InputPerMillionUsd);
        Assert.True(ModelPricingCatalog.TryResolve("claude-sonnet-5-5-20260501", overrides, out var overriddenDated));
        Assert.Equal(1.25m, overriddenDated.InputPerMillionUsd);

        // Prefijos oficiales de nube y sufijo -latest en Defaults.
        Assert.True(ModelPricingCatalog.TryResolve("anthropic/claude-sonnet-4-5-latest", null, out var anthropicLatest));
        Assert.Equal(3.00m, anthropicLatest.InputPerMillionUsd);
        Assert.True(ModelPricingCatalog.TryResolve("openai/gpt-4o-2024-08-06", null, out var openAiDated));
        Assert.Equal(2.50m, openAiDated.InputPerMillionUsd);
        Assert.True(ModelPricingCatalog.TryResolve("claude-3-5-sonnet@20241022", null, out _));

        // Nunca resuelve prefijos locales (ollama/, local/) ni modelos desconocidos.
        Assert.False(ModelPricingCatalog.TryResolve("ollama/gpt-4o", null, out _));
        Assert.False(ModelPricingCatalog.TryResolve("local/claude-sonnet-5-5", null, out _));
        Assert.False(ModelPricingCatalog.TryResolve("qwen2.5-coder:32b", null, out _));
    }

    [Fact]
    public async Task Runs_api_returns_non_estimable_empty_cost_summary_when_run_has_no_charges()
    {
        await using var factory = new CodeNetixFactory(_dataDirectory, _webRoot, CodeNetixFactory.CompleteSettings);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", CodeNetixFactory.TestAccessToken);
        var runId = await SeedRunAsync(factory, budgetLimitUsd: 3.0m);

        var list = await client.GetFromJsonAsync<List<RunSummary>>("/api/runs", WebJson, Ct);
        var listed = Assert.Single(list!, r => r.RunId == runId);
        Assert.NotNull(listed.Cost);
        Assert.False(listed.Cost!.IsEstimable);
        Assert.False(listed.Cost.HasUnpricedModels);
        Assert.Null(listed.Cost.EstimatedCostUsd);
        Assert.Equal(0, listed.Cost.TotalInputTokens);
        Assert.Equal(0, listed.Cost.TotalOutputTokens);
        Assert.Equal(3.0m, listed.Cost.BudgetLimitUsd);

        var detail = await client.GetFromJsonAsync<RunResponse>($"/api/runs/{runId}", WebJson, Ct);
        Assert.NotNull(detail?.Cost);
        Assert.False(detail!.Cost!.IsEstimable);
        Assert.False(detail.Cost.HasUnpricedModels);
        Assert.Null(detail.Cost.EstimatedCostUsd);
    }

    [Fact]
    public async Task Concurrent_ChargeAsync_calls_persist_all_charges_and_contiguous_TokensConsumed_sequences()
    {
        await using var factory = new CodeNetixFactory(_dataDirectory, _webRoot, CodeNetixFactory.CompleteSettings);
        _ = factory.Server;
        var runId = await SeedRunAsync(factory, budgetLimitUsd: null);
        var ledger = factory.Services.GetRequiredService<IBudgetLedger>();

        const int workers = 8;
        var tasks = Enumerable.Range(0, workers).Select(i => Task.Run(
            () => ledger.ChargeAsync(new BudgetChargeRequest(runId, null, $"Worker-{i}", "Anthropic", "claude-sonnet-5-5", 1000, 200), Ct),
            Ct));
        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.Equal(BudgetChargeOutcome.Recorded, r.Outcome));

        var cost = await ledger.GetRunCostAsync(runId, Ct);
        Assert.True(cost.IsEstimable);
        Assert.Equal(workers * 1000, cost.TotalInputTokens);
        Assert.Equal(workers * 200, cost.TotalOutputTokens);

        var reader = factory.Services.GetRequiredService<IRunReader>();
        var events = await reader.GetEventsAsync(runId, 0, Ct);
        Assert.Equal(workers, events.OfType<TokensConsumed>().Count());
        Assert.Equal(Enumerable.Range(1, events.Count).Select(i => (long)i), events.Select(e => e.Seq));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunOrchestrator_publishes_pr_body_with_real_cost_or_not_estimable(bool mixUnpricedModel)
    {
        using var repos = new LocalGitRepos();
        repos.Create("acme", "costrepo", new Dictionary<string, string>
        {
            ["README.md"] = "# Cost\n",
            ["src/App.cs"] = "public class App { public int Value => 1; }\n",
        });

        var settings = new Dictionary<string, string?>(CodeNetixFactory.CompleteSettings)
        {
            [CodeNetixFactory.CloneBaseUrlKey] = repos.BaseUrl,
        };

        var capturedPrs = new List<PullRequestRequest>();
        await using var baseFactory = new CodeNetixFactory(_dataDirectory, _webRoot, settings);
        baseFactory.ChatClient.DefaultUsage = new UsageDetails
        {
            InputTokenCount = 1000,
            OutputTokenCount = 200,
        };

        await using var factory = baseFactory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            services.AddSingleton<IPullRequestPublisher>(new CapturingPullRequestPublisher(capturedPrs));
        }));

        baseFactory.Sandbox.OnSessionCreated = session =>
        {
            session.ExportWorkspaceHandler = async (dest, ct) =>
            {
                if (mixUnpricedModel)
                {
                    var ledger = factory.Services.GetRequiredService<IBudgetLedger>();
                    await ledger.ChargeAsync(
                        new BudgetChargeRequest(session.RunId, null, "Coder", "Ollama", "qwen2.5-coder:32b", 500, 100),
                        ct);
                }

                await WriteTarAsync(dest,
                [
                    ("workspace/README.md", "# Cost\n"),
                    ("workspace/src/App.cs", "public class App { public int Value => 2; }\n"),
                ], ct);
            };
        };

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", CodeNetixFactory.TestAccessToken);
        using var submit = await client.PostAsJsonAsync(
            "/api/events/manual",
            new { url = "https://github.com/acme/costrepo/issues/1", requestId = Guid.NewGuid().ToString() },
            Ct);
        Assert.Equal(HttpStatusCode.Created, submit.StatusCode);

        // Esperar a que la Ejecución alcance PullRequestCreated.
        RunResponse? finalDetail = null;
        for (var i = 0; i < 100; i++)
        {
            var runs = await client.GetFromJsonAsync<List<RunSummary>>("/api/runs", WebJson, Ct);
            if (runs is { Count: > 0 } && runs[0].State.IsTerminal())
            {
                finalDetail = await client.GetFromJsonAsync<RunResponse>($"/api/runs/{runs[0].RunId}", WebJson, Ct);
                break;
            }

            await Task.Delay(50, Ct);
        }

        Assert.NotNull(finalDetail);
        Assert.Equal(RunState.PullRequestCreated, finalDetail!.State);

        var prRequest = Assert.Single(capturedPrs);
        Assert.Contains("## Coste", prRequest.Body, StringComparison.Ordinal);
        if (mixUnpricedModel)
        {
            Assert.Contains("no estimable", prRequest.Body, StringComparison.Ordinal);
            Assert.NotNull(finalDetail.Cost);
            Assert.False(finalDetail.Cost!.IsEstimable);
            Assert.True(finalDetail.Cost.HasUnpricedModels);
        }
        else
        {
            Assert.Contains("USD (", prRequest.Body, StringComparison.Ordinal);
            Assert.DoesNotContain("no disponible", prRequest.Body, StringComparison.Ordinal);
            Assert.NotNull(finalDetail.Cost);
            Assert.True(finalDetail.Cost!.IsEstimable);
            Assert.True(finalDetail.Cost.EstimatedCostUsd > 0);
        }
    }

    private static async Task SendThroughGatewayAsync(
        CodeNetixFactory factory,
        string token,
        string responseBody,
        string contentType)
    {
        using var transport = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, contentType),
            });

        using var proxy = new LlmReverseProxy(
            factory.Services.GetRequiredService<ISandboxGateway>(),
            factory.Services.GetRequiredService<IRunEventSink>(),
            factory.Services.GetRequiredService<IOptionsMonitor<SandboxOptions>>(),
            factory.Services.GetRequiredService<IHttpForwarder>(),
            NullLogger<LlmReverseProxy>.Instance,
            factory.Services.GetRequiredService<IBudgetLedger>(),
            transport);

        var httpContext = new DefaultHttpContext { RequestServices = factory.Services };
        httpContext.Connection.LocalPort = SandboxOptions.DefaultLlmGatewayPort;
        httpContext.Request.Method = HttpMethods.Post;
        httpContext.Request.Path = "/llm/v1/messages";
        httpContext.Request.ContentType = "application/json";
        httpContext.Request.Headers[LlmReverseProxy.ApiKeyHeader] = token;
        var reqBytes = Encoding.UTF8.GetBytes("""{"model":"claude-sonnet-5-5","messages":[]}""");
        httpContext.Request.Body = new MemoryStream(reqBytes);
        httpContext.Request.ContentLength = reqBytes.Length;
        httpContext.Response.Body = new MemoryStream();

        await proxy.HandleAsync(httpContext, "v1/messages");
        Assert.Equal(StatusCodes.Status200OK, httpContext.Response.StatusCode);
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

    private static async Task<Guid> SeedRunAsync(CodeNetixFactory factory, decimal? budgetLimitUsd)
    {
        var contexts = factory.Services.GetRequiredService<IDbContextFactory<PipelineDbContext>>();
        await using var db = await contexts.CreateDbContextAsync(Ct);
        var now = DateTimeOffset.UtcNow;
        var (run, created, initial) = PipelineRun.Create(Guid.CreateVersion7(now), "github:acme/app#42", now);
        run.RecordBaseSha("0123456789abcdef0123456789abcdef01234567");
        run.FreezeConfiguration(EffectiveConfigCalculator.Calculate(new CodeNetix.Pipeline.InstanceLimits { BudgetUsd = budgetLimitUsd }, null));
        db.Runs.Add(run);
        var log = factory.Services.GetRequiredService<CodeNetix.Pipeline.Events.RunEventLog>();
        await log.AddAsync(db, run.Id, created, Ct);
        await log.AddAsync(db, run.Id, initial, Ct);
        await log.SaveAndPublishAsync(db, Ct);
        return run.Id;
    }

    private static string BuildSamplePrBody(RunCostSummary? cost)
    {
        var spec = new Spec(
            "Corregir cálculo",
            "Falta redondeo",
            "Aplicar redondeo en el servicio",
            ["El cálculo devuelve el valor esperado"],
            ["src/Calc.cs"]);
        var report = new TestReport(true, 3, 3, 0, 0, ["Tests.Calc"], [], TimeSpan.FromMilliseconds(50));
        return PullRequestBodyBuilder.BuildBody(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            "acme",
            "app",
            42,
            spec,
            [],
            report,
            new NoOpRedactor(),
            cost: cost);
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

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }

    private sealed class NoOpRedactor : ISecretRedactor
    {
        public string Redact(string text) => text;
    }

    private sealed class StreamingStubChatClient(IReadOnlyList<ChatResponseUpdate> updates) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var update in updates)
            {
                await Task.Yield();
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class CapturingPullRequestPublisher(List<PullRequestRequest> captured) : IPullRequestPublisher
    {
        public Task<PullRequestInfo> PublishAsync(PullRequestRequest request, CancellationToken cancellationToken)
        {
            captured.Add(request);
            return Task.FromResult(new PullRequestInfo(
                Number: 99,
                Url: $"https://github.com/{request.Owner}/{request.Repo}/pull/99",
                BranchName: $"codenetix/{request.RunId:D}",
                CommitSha: "abcdef0123456789abcdef0123456789abcdef01",
                Title: request.Title,
                CreatedAt: DateTimeOffset.UtcNow));
        }

        public Task<PullRequestInfo?> FindByRunAsync(Guid runId, CancellationToken cancellationToken) =>
            Task.FromResult<PullRequestInfo?>(null);
    }
}
