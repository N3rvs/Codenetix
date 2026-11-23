using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CodeNetix.Contracts;
using CodeNetix.GitHub.Data;
using CodeNetix.Host.Features.GetRun;
using CodeNetix.Pipeline.Data;
using CodeNetix.Pipeline.Domain;
using CodeNetix.Pipeline.Events;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CodeNetix.Host.Tests;

/// <summary>
/// Prueba de integración de la Historia 4.3 (FR-22): verifica que el historial completo de Ejecuciones
/// (Estado, <see cref="FailureReason"/>, <see cref="Spec"/>, <see cref="TestReport"/>, <see cref="RunVerdict"/>
/// con hallazgos, <see cref="PullRequestInfo"/> y eventos <see cref="TokensConsumed"/> / <see cref="RiskClassified"/> /
/// <see cref="PullRequestOpened"/>) sobrevive íntegro a un reinicio completo del Host compartiendo <c>/data</c>.
/// </summary>
public sealed class RunHistoryPersistenceTests : IDisposable
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "codenetix-history-" + Guid.NewGuid().ToString("N"));
    private readonly string _dataDirectory;
    private readonly string _webRoot;

    public RunHistoryPersistenceTests()
    {
        _dataDirectory = Path.Combine(_root, "data");
        _webRoot = Path.Combine(_root, "wwwroot");
        Directory.CreateDirectory(_webRoot);
        File.WriteAllText(Path.Combine(_webRoot, "index.html"), "<!doctype html><html><body>ok</body></html>");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task History_survives_full_host_restart_with_shared_data_directory()
    {
        var createdAtA = new DateTimeOffset(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);
        var updatedAtA = new DateTimeOffset(2026, 11, 22, 9, 3, 25, TimeSpan.Zero);
        var createdAtB = new DateTimeOffset(2026, 11, 22, 9, 5, 0, TimeSpan.Zero);
        var updatedAtB = new DateTimeOffset(2026, 11, 22, 9, 6, 10, TimeSpan.Zero);

        Guid runAId;
        string runAShortId;
        Guid runBId;
        string runBShortId;

        var expectedSpecA = new Spec(
            "El endpoint de historial no pagina cuando limit excede 200.",
            "ListRunsEndpoint no aplicaba Math.Min sobre el parámetro limit.",
            "Acotar el límite al máximo configurado y añadir tests de regresión.",
            ["GET /api/runs?limit=500 devuelve 200 OK acotado a 200 elementos", "GET /api/runs?limit=0 devuelve 400"],
            ["src/CodeNetix.Host/Features/ListRuns/ListRunsEndpoint.cs", "tests/CodeNetix.Host.Tests/RunHistoryPersistenceTests.cs"]);

        var expectedTestReportA = new TestReport(
            BuildSucceeded: true,
            TotalTests: 14,
            PassedTests: 14,
            FailedTests: 0,
            SkippedTests: 0,
            NewTests: ["CodeNetix.Host.Tests.RunHistoryPersistenceTests.History_survives_full_host_restart_with_shared_data_directory"],
            FailedTestCases: [],
            Duration: TimeSpan.FromSeconds(4.5));

        var expectedFindingsA = ImmutableArray.Create(
            new ReviewFinding("low", "src/CodeNetix.Host/Features/ListRuns/ListRunsEndpoint.cs", 39, "Validar límite máximo con constante compartida."));

        var prCreatedAtA = new DateTimeOffset(2026, 11, 22, 9, 3, 20, TimeSpan.Zero);
        const string expectedPrUrlA = "https://github.com/acme/codenetix/pull/108";
        const string expectedPrBranchA = "codenetix/fix-108";
        const string expectedPrShaA = "9f8e7d6c5b4a3210fedcba9876543210abcdef12";
        const string expectedPrTitleA = "Fix: acotar limit en GET /api/runs";

        const string expectedFailureDetailB = "El revisor de seguridad rechazó el cambio por consulta SQL sin parametrizar.";

        // 1. Arrancar Host #1 sobre _dataDirectory persistente en disco y guardar Ejecución A y Ejecución B.
        var host1 = new CodeNetixFactory(_dataDirectory, _webRoot, CodeNetixFactory.CompleteSettings);
        try
        {
            using var client1 = host1.CreateAuthorizedClient();
            using var health1 = await client1.GetAsync("/api/health", Ct);
            Assert.Equal(HttpStatusCode.OK, health1.StatusCode);

            var pipelineFactory = host1.Services.GetRequiredService<IDbContextFactory<PipelineDbContext>>();
            var gitHubFactory = host1.Services.GetRequiredService<IDbContextFactory<GitHubDbContext>>();

            await using (var db = await pipelineFactory.CreateDbContextAsync(Ct))
            {
                // Ejecución A: PullRequestCreated completa.
                var (runA, createdEvA, initialEvA) = PipelineRun.Create(Guid.NewGuid(), "github:acme/codenetix#108", createdAtA);
                runAId = runA.Id;
                runAShortId = runA.ShortId;

                var toAnalyzing = runA.StartAnalysis();
                var toCoding = runA.StartCoding();
                var toReviewing = runA.StartReview();
                var toDeciding = runA.StartDeciding();
                var toPrCreated = runA.CompletePullRequest();
                runA.Touch(updatedAtA);

                var reviewStageId = Guid.NewGuid();
                db.Runs.Add(runA);
                db.Specs.Add(new RunSpecRecord
                {
                    RunId = runA.Id,
                    SpecJson = SpecSerializer.Serialize(expectedSpecA),
                    Model = "claude-sonnet-4-5",
                    Attempts = 1,
                    CreatedAt = createdAtA.AddSeconds(15),
                });
                db.TestReports.Add(new RunTestReportRecord
                {
                    RunId = runA.Id,
                    ReportJson = TestReportSerializer.Serialize(expectedTestReportA),
                    Passed = expectedTestReportA.Passed,
                    TotalTests = expectedTestReportA.TotalTests,
                    FailedTests = expectedTestReportA.FailedTests,
                    NewTestsCount = expectedTestReportA.NewTests.Count,
                    CreatedAt = createdAtA.AddMinutes(2),
                });
                db.Stages.Add(new RunStageRecord
                {
                    Id = reviewStageId,
                    RunId = runA.Id,
                    Kind = StageKind.Review,
                    Round = 1,
                    Reviewer = ReviewerKind.Security,
                    StartedAt = createdAtA.AddMinutes(2).AddSeconds(5),
                    CompletedAt = createdAtA.AddMinutes(2).AddSeconds(40),
                });
                db.Verdicts.Add(new RunVerdictRecord
                {
                    Id = Guid.NewGuid(),
                    RunId = runA.Id,
                    StageId = reviewStageId,
                    Round = 1,
                    Reviewer = ReviewerKind.Security,
                    Decision = VerdictDecision.Approved,
                    Summary = "Sin problemas de seguridad bloqueantes; sugerencia menor.",
                    FindingsJson = VerdictFindingsSerializer.Serialize(expectedFindingsA),
                    Model = "claude-sonnet-4-5",
                    Attempts = 1,
                    CreatedAt = createdAtA.AddMinutes(2).AddSeconds(40),
                });

                var eventsA = new RunEvent[]
                {
                    createdEvA with { RunId = runA.Id, Seq = 1, At = createdAtA },
                    initialEvA with { RunId = runA.Id, Seq = 2, At = createdAtA },
                    toAnalyzing with { RunId = runA.Id, Seq = 3, At = createdAtA.AddSeconds(2) },
                    new TokensConsumed("analyst", "anthropic", "claude-sonnet-4-5", 1400, 520) with { RunId = runA.Id, Seq = 4, At = createdAtA.AddSeconds(15) },
                    toCoding with { RunId = runA.Id, Seq = 5, At = createdAtA.AddSeconds(16) },
                    new TokensConsumed("coder", "anthropic", "claude-sonnet-4-5", 4800, 1900) with { RunId = runA.Id, Seq = 6, At = createdAtA.AddMinutes(2) },
                    toReviewing with { RunId = runA.Id, Seq = 7, At = createdAtA.AddMinutes(2).AddSeconds(1) },
                    toDeciding with { RunId = runA.Id, Seq = 8, At = createdAtA.AddMinutes(2).AddSeconds(45) },
                    new RiskClassified(RiskClass.NeedsAttention, 84, ["src/CodeNetix.Host/Access/AdminTokenMiddleware.cs"]) with { RunId = runA.Id, Seq = 9, At = createdAtA.AddMinutes(3) },
                    new PullRequestOpened(108, expectedPrUrlA, expectedPrBranchA, expectedPrShaA) with { RunId = runA.Id, Seq = 10, At = prCreatedAtA },
                    toPrCreated with { RunId = runA.Id, Seq = 11, At = updatedAtA },
                };

                foreach (var ev in eventsA)
                {
                    db.Events.Add(ToRecord(ev));
                }

                // Ejecución B: Failed(ReviewRejected) con FailureDetail.
                var (runB, createdEvB, initialEvB) = PipelineRun.Create(Guid.NewGuid(), "github:acme/codenetix#109", createdAtB);
                runBId = runB.Id;
                runBShortId = runB.ShortId;

                var bAnalyzing = runB.StartAnalysis();
                var bCoding = runB.StartCoding();
                var bReviewing = runB.StartReview();
                var bFailed = runB.Fail(FailureReason.ReviewRejected, expectedFailureDetailB);
                runB.Touch(updatedAtB);

                db.Runs.Add(runB);
                var eventsB = new RunEvent[]
                {
                    createdEvB with { RunId = runB.Id, Seq = 1, At = createdAtB },
                    initialEvB with { RunId = runB.Id, Seq = 2, At = createdAtB },
                    bAnalyzing with { RunId = runB.Id, Seq = 3, At = createdAtB.AddSeconds(5) },
                    bCoding with { RunId = runB.Id, Seq = 4, At = createdAtB.AddSeconds(20) },
                    bReviewing with { RunId = runB.Id, Seq = 5, At = createdAtB.AddSeconds(50) },
                    bFailed with { RunId = runB.Id, Seq = 6, At = updatedAtB },
                };

                foreach (var ev in eventsB)
                {
                    db.Events.Add(ToRecord(ev));
                }

                await db.SaveChangesAsync(Ct);
            }

            await using (var ghDb = await gitHubFactory.CreateDbContextAsync(Ct))
            {
                ghDb.PullRequests.Add(new PullRequestRecord
                {
                    Id = Guid.NewGuid(),
                    RunId = runAId,
                    Number = 108,
                    Url = expectedPrUrlA,
                    BranchName = expectedPrBranchA,
                    CommitSha = expectedPrShaA,
                    Title = expectedPrTitleA,
                    CreatedAt = prCreatedAtA,
                });

                await ghDb.SaveChangesAsync(Ct);
            }
        }
        finally
        {
            // 2. Detener y liberar por completo Host #1 y limpiar pools de SQLite.
            await host1.DisposeAsync();
            SqliteConnection.ClearAllPools();
        }

        Assert.NotEmpty(Directory.GetFiles(_dataDirectory, "*.db"));

        // 3. Arrancar Host #2 apuntando al mismo directorio _dataDirectory.
        await using var host2 = new CodeNetixFactory(_dataDirectory, _webRoot, CodeNetixFactory.CompleteSettings);
        using var client2 = host2.CreateAuthorizedClient();

        // 4. Verificar GET /api/runs en Host #2.
        var listed = await client2.GetFromJsonAsync<List<RunSummary>>("/api/runs?limit=200", WebJson, Ct);
        Assert.NotNull(listed);

        var summaryB = Assert.Single(listed, r => r.RunId == runBId);
        var summaryA = Assert.Single(listed, r => r.RunId == runAId);
        Assert.True(listed.IndexOf(summaryB) < listed.IndexOf(summaryA), "La Ejecución más reciente (B) debe aparecer antes que A.");

        Assert.Equal(runAShortId, summaryA.ShortId);
        Assert.Equal(RunState.PullRequestCreated, summaryA.State);
        Assert.Null(summaryA.FailureReason);
        Assert.Equal(createdAtA, summaryA.CreatedAt);
        Assert.Equal(updatedAtA, summaryA.UpdatedAt);

        Assert.Equal(runBShortId, summaryB.ShortId);
        Assert.Equal(RunState.Failed, summaryB.State);
        Assert.Equal(FailureReason.ReviewRejected, summaryB.FailureReason);
        Assert.Equal(expectedFailureDetailB, summaryB.FailureDetail);
        Assert.Equal(createdAtB, summaryB.CreatedAt);
        Assert.Equal(updatedAtB, summaryB.UpdatedAt);

        // 5. Verificar GET /api/runs/{id} para Ejecución A tras reinicio.
        var detailA = await client2.GetFromJsonAsync<RunResponse>($"/api/runs/{runAId}", WebJson, Ct);
        Assert.NotNull(detailA);
        Assert.Equal(RunState.PullRequestCreated, detailA.State);
        Assert.Equal(createdAtA, detailA.CreatedAt);
        Assert.Equal(updatedAtA, detailA.UpdatedAt);

        Assert.NotNull(detailA.Spec);
        Assert.Equal(expectedSpecA.Problem, detailA.Spec.Problem);
        Assert.Equal(expectedSpecA.ProbableCause, detailA.Spec.ProbableCause);
        Assert.Equal(expectedSpecA.ProposedChange, detailA.Spec.ProposedChange);
        Assert.Equal(expectedSpecA.AcceptanceCriteria, detailA.Spec.AcceptanceCriteria);
        Assert.Equal(expectedSpecA.AffectedFiles, detailA.Spec.AffectedFiles);

        Assert.NotNull(detailA.TestReport);
        Assert.True(detailA.TestReport.BuildSucceeded);
        Assert.Equal(14, detailA.TestReport.TotalTests);
        Assert.Equal(14, detailA.TestReport.PassedTests);
        Assert.Equal(0, detailA.TestReport.FailedTests);
        Assert.Single(detailA.TestReport.NewTests);
        Assert.Equal(expectedTestReportA.Duration, detailA.TestReport.Duration);

        var verdictA = Assert.Single(detailA.Verdicts);
        Assert.Equal(1, verdictA.Round);
        Assert.Equal(ReviewerKind.Security, verdictA.Reviewer);
        Assert.Equal(VerdictDecision.Approved, verdictA.Decision);
        Assert.Equal("Sin problemas de seguridad bloqueantes; sugerencia menor.", verdictA.Summary);
        Assert.Equal("claude-sonnet-4-5", verdictA.Model);
        Assert.Equal(1, verdictA.Attempts);
        var findingA = Assert.Single(verdictA.Findings);
        Assert.Equal("low", findingA.Severity);
        Assert.Equal("src/CodeNetix.Host/Features/ListRuns/ListRunsEndpoint.cs", findingA.File);
        Assert.Equal(39, findingA.Line);
        Assert.Equal("Validar límite máximo con constante compartida.", findingA.Message);

        Assert.NotNull(detailA.PullRequest);
        Assert.Equal(108, detailA.PullRequest.Number);
        Assert.Equal(expectedPrUrlA, detailA.PullRequest.Url);
        Assert.Equal(expectedPrBranchA, detailA.PullRequest.BranchName);
        Assert.Equal(expectedPrShaA, detailA.PullRequest.CommitSha);
        Assert.Equal(expectedPrTitleA, detailA.PullRequest.Title);
        Assert.Equal(prCreatedAtA, detailA.PullRequest.CreatedAt);

        var tokenEvents = detailA.Events.OfType<TokensConsumed>().ToList();
        Assert.Equal(2, tokenEvents.Count);
        Assert.Equal(6200, tokenEvents.Sum(e => e.InputTokens));
        Assert.Equal(2420, tokenEvents.Sum(e => e.OutputTokens));

        var riskEvent = Assert.Single(detailA.Events.OfType<RiskClassified>());
        Assert.Equal(RiskClass.NeedsAttention, riskEvent.RiskClass);
        Assert.Equal(84, riskEvent.DiffLines);
        Assert.Contains("src/CodeNetix.Host/Access/AdminTokenMiddleware.cs", riskEvent.MatchedProtectedPaths);

        var prEvent = Assert.Single(detailA.Events.OfType<PullRequestOpened>());
        Assert.Equal(108, prEvent.Number);
        Assert.Equal(expectedPrUrlA, prEvent.Url);

        // 6. Verificar GET /api/runs/{id} para Ejecución B tras reinicio (también accesible por shortId).
        var detailB = await client2.GetFromJsonAsync<RunResponse>($"/api/runs/{runBShortId}", WebJson, Ct);
        Assert.NotNull(detailB);
        Assert.Equal(runBId, detailB.RunId);
        Assert.Equal(RunState.Failed, detailB.State);
        Assert.Equal(FailureReason.ReviewRejected, detailB.FailureReason);
        Assert.Equal(expectedFailureDetailB, detailB.FailureDetail);
        Assert.Null(detailB.Spec);
        Assert.Null(detailB.TestReport);
        Assert.Empty(detailB.Verdicts);
        Assert.Null(detailB.PullRequest);
        Assert.Equal(6, detailB.Events.Count);
        Assert.Equal(createdAtB, detailB.CreatedAt);
        Assert.Equal(updatedAtB, detailB.UpdatedAt);
    }

    private static RunEventRecord ToRecord(RunEvent ev) => new()
    {
        RunId = ev.RunId,
        Seq = ev.Seq,
        StageId = ev.StageId,
        Type = ev.GetType().Name,
        PayloadJson = RunEventJson.Serialize(ev),
        At = ev.At,
    };

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
