using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CodeNetix.Contracts;
using CodeNetix.Persistence;
using CodeNetix.Sandbox.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace CodeNetix.Host.Tests;

/// <summary>
/// Pruebas de la historia 2.4: <see cref="TrxParser"/>, <see cref="DotnetTestRunner"/>, bucle de reintentos
/// del orquestador con <see cref="ITestRunner"/>, persistencia en <c>run_test_reports</c> y exposición en
/// <c>GET /api/runs/{id}</c>.
/// </summary>
public sealed class TestRunnerTests : IDisposable
{
    private const string IssueUrl = "https://github.com/O/R/issues/42";
    private const string GitHubToken = "ghp_testtoken0123456789abcdefghijklmn";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "codenetix-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _dataDirectory;
    private readonly string _webRoot;

    public TestRunnerTests()
    {
        _dataDirectory = Path.Combine(_root, "data");
        _webRoot = Path.Combine(_root, "wwwroot");
        Directory.CreateDirectory(_webRoot);
        File.WriteAllText(Path.Combine(_webRoot, "index.html"), "<!doctype html><html><body>Panel</body></html>");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string DatabasePath => Path.Combine(_dataDirectory, PersistenceOptions.DatabaseFileName);

    // --- TrxParser ------------------------------------------------------------------------

    [Fact]
    public void TrxParser_extracts_counts_failures_duration_and_new_tests_against_baseline()
    {
        const string trx = """
            <?xml version="1.0" encoding="utf-8"?>
            <TestRun id="1" name="run" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Times start="2026-10-29T10:00:00.0000000+00:00" finish="2026-10-29T10:00:02.5000000+00:00" />
              <Results>
                <UnitTestResult testId="t1" testName="App.Tests.Existing_passes" duration="00:00:00.1000000" outcome="Passed" />
                <UnitTestResult testId="t2" testName="App.Tests.New_test_passes" duration="00:00:00.2000000" outcome="Passed" />
                <UnitTestResult testId="t3" testName="App.Tests.New_skipped_test" duration="00:00:00.0000000" outcome="NotExecuted" />
                <UnitTestResult testId="t4" testName="App.Tests.Regression_fails" duration="00:00:00.0500000" outcome="Failed">
                  <Output>
                    <ErrorInfo>
                      <Message>Assert.Equal() Failure: Expected 42, Actual 0</Message>
                      <StackTrace>   at App.Tests.Regression_fails() in Tests.cs:line 18</StackTrace>
                    </ErrorInfo>
                  </Output>
                </UnitTestResult>
              </Results>
            </TestRun>
            """;

        var baseline = new TestBaseline(new HashSet<string>(["App.Tests.Existing_passes"], StringComparer.Ordinal));
        var parsed = TrxParser.Parse(trx);
        var report = TrxParser.ToReport(parsed, baseline);

        Assert.True(report.BuildSucceeded);
        Assert.Equal(4, report.TotalTests);
        Assert.Equal(2, report.PassedTests);
        Assert.Equal(1, report.FailedTests);
        Assert.Equal(1, report.SkippedTests);
        Assert.Equal(["App.Tests.New_test_passes"], report.NewTests);
        Assert.False(report.Passed); // hay 1 fallo
        Assert.Equal(TimeSpan.FromSeconds(2.5), report.Duration);

        var failed = Assert.Single(report.FailedTestCases);
        Assert.Equal("App.Tests.Regression_fails", failed.Name);
        Assert.Equal("Failed", failed.Outcome);
        Assert.Contains("Expected 42, Actual 0", failed.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("Tests.cs:line 18", failed.StackTrace, StringComparison.Ordinal);
    }

    [Fact]
    public void TrxParser_qualifies_short_mstest_names_including_dotted_parameters_and_expands_inner_results()
    {
        const string trx = """
            <?xml version="1.0" encoding="utf-8"?>
            <TestRun id="1" name="run" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results>
                <UnitTestResult testId="id-1" testName="Parses_decimal" outcome="Passed">
                  <InnerResults>
                    <UnitTestResult testId="id-1" testName="Parses_decimal(1.5)" outcome="Passed" duration="00:00:00.0100000" />
                    <UnitTestResult testId="id-1" testName="Parses_decimal(2.5)" outcome="Warning" duration="00:00:00.0200000" />
                  </InnerResults>
                </UnitTestResult>
              </Results>
              <TestDefinitions>
                <UnitTest id="id-1" name="Parses_decimal">
                  <TestMethod className="MyApp.DecimalTests, MyApp.Tests" name="Parses_decimal" />
                </UnitTest>
              </TestDefinitions>
            </TestRun>
            """;

        var parsed = TrxParser.Parse(trx);
        var report = TrxParser.ToReport(parsed, TestBaseline.Empty);

        Assert.Equal(2, report.TotalTests);
        Assert.Equal(1, report.PassedTests);
        Assert.Equal(1, report.FailedTests); // Warning no se clasifica como skipped
        Assert.Equal(0, report.SkippedTests);
        Assert.Equal(["MyApp.DecimalTests.Parses_decimal(1.5)"], report.NewTests);
        var warnCase = Assert.Single(report.FailedTestCases);
        Assert.Equal("MyApp.DecimalTests.Parses_decimal(2.5)", warnCase.Name);
        Assert.Equal("Warning", warnCase.Outcome);
    }

    [Fact]
    public void TrxParser_throws_FormatException_on_malformed_xml()
    {
        Assert.Throws<FormatException>(() => TrxParser.Parse("<TestRun><broken"));
    }

    // --- DotnetTestRunner -----------------------------------------------------------------

    [Fact]
    public async Task DotnetTestRunner_returns_build_failure_report_preserving_tail_diagnostics_and_redirects_artifacts()
    {
        var longPrefix = new string('a', DotnetTestRunner.MaxBuildOutputChars + 500);
        var tailError = "src/Profile.cs(12,5): error CS1002: ; expected";
        var session = new FakeSandboxSession(Guid.NewGuid())
        {
            ExecHandler = cmd => cmd.Executable == "dotnet" && cmd.Arguments.Contains("build")
                ? new SandboxExecResult(1, longPrefix + "\n" + tailError, string.Empty, TimedOut: false, Truncated: false)
                : throw new InvalidOperationException($"No debía ejecutar {cmd.Executable}"),
        };

        var runner = new DotnetTestRunner();
        var report = await runner.RunAsync(session, TestBaseline.Empty, Ct);

        Assert.False(report.BuildSucceeded);
        Assert.False(report.Passed);
        Assert.Equal(0, report.TotalTests);
        Assert.NotNull(report.BuildOutput);
        Assert.StartsWith("…\n", report.BuildOutput, StringComparison.Ordinal);
        Assert.EndsWith(tailError, report.BuildOutput, StringComparison.Ordinal);
        Assert.True(report.BuildOutput.Length <= DotnetTestRunner.MaxBuildOutputChars);

        var buildCmd = Assert.Single(session.Commands);
        Assert.Contains("-p:UseArtifactsOutput=true", buildCmd.Arguments);
        Assert.Contains($"-p:ArtifactsPath={DotnetTestRunner.ArtifactsPath}", buildCmd.Arguments);
    }

    [Fact]
    public async Task DotnetTestRunner_captures_baseline_aggregates_multiple_trx_and_detects_new_tests()
    {
        var trxA = BuildTrxXml(("ProjectA.Tests.OldTest", "Passed", null));
        var trxB = BuildTrxXml(("ProjectB.Tests.OtherOldTest", "Passed", null));
        var trxBAfter = BuildTrxXml(
            ("ProjectB.Tests.OtherOldTest", "Passed", null),
            ("ProjectB.Tests.NewSpecTest", "Passed", null));

        var afterCoding = false;
        var session = new FakeSandboxSession(Guid.NewGuid())
        {
            ExecHandler = cmd => cmd.Executable switch
            {
                "dotnet" => new SandboxExecResult(0, string.Empty, string.Empty, TimedOut: false, Truncated: false),
                "find" => new SandboxExecResult(0, $"{cmd.Arguments[0]}/a.trx\n{cmd.Arguments[0]}/b.trx\n", string.Empty, TimedOut: false, Truncated: false),
                "cat" when cmd.Arguments[0].EndsWith("/a.trx", StringComparison.Ordinal) =>
                    new SandboxExecResult(0, trxA, string.Empty, TimedOut: false, Truncated: false),
                "cat" when cmd.Arguments[0].EndsWith("/b.trx", StringComparison.Ordinal) =>
                    new SandboxExecResult(0, afterCoding ? trxBAfter : trxB, string.Empty, TimedOut: false, Truncated: false),
                "rm" => new SandboxExecResult(0, string.Empty, string.Empty, TimedOut: false, Truncated: false),
                _ => new SandboxExecResult(0, string.Empty, string.Empty, TimedOut: false, Truncated: false),
            },
        };

        var runner = new DotnetTestRunner();
        var baseline = await runner.CaptureBaselineAsync(session, Ct);
        Assert.Equal(2, baseline.ExistingTests.Count);
        Assert.Contains("ProjectA.Tests.OldTest", baseline.ExistingTests);
        Assert.Contains("ProjectB.Tests.OtherOldTest", baseline.ExistingTests);

        // Sin tests nuevos -> Passed == false
        var noNewReport = await runner.RunAsync(session, baseline, Ct);
        Assert.True(noNewReport.BuildSucceeded);
        Assert.Equal(2, noNewReport.TotalTests);
        Assert.Equal(0, noNewReport.FailedTests);
        Assert.Empty(noNewReport.NewTests);
        Assert.False(noNewReport.Passed);

        // Con 1 test nuevo en el segundo proyecto -> Passed == true
        afterCoding = true;
        var passedReport = await runner.RunAsync(session, baseline, Ct);
        Assert.True(passedReport.BuildSucceeded);
        Assert.Equal(3, passedReport.TotalTests);
        Assert.Equal(3, passedReport.PassedTests);
        Assert.Equal(0, passedReport.FailedTests);
        Assert.Equal(["ProjectB.Tests.NewSpecTest"], passedReport.NewTests);
        Assert.True(passedReport.Passed);

        // Verifica flags de dotnet test (--logger trx y ArtifactsPath) y limpieza de /tmp
        var testCmd = session.Commands.Last(c => c.Executable == "dotnet" && c.Arguments.Contains("test"));
        Assert.Contains("-p:UseArtifactsOutput=true", testCmd.Arguments);
        Assert.Contains($"-p:ArtifactsPath={DotnetTestRunner.ArtifactsPath}", testCmd.Arguments);
        Assert.Contains("--logger", testCmd.Arguments);
        Assert.Contains("trx", testCmd.Arguments);
        Assert.Contains(session.Commands, c => c.Executable == "rm" && c.Arguments.Contains("-rf"));
    }

    [Fact]
    public async Task DotnetTestRunner_handles_missing_trx_and_nonzero_exit_with_partial_passing_trx()
    {
        // 1) Sin TRX + ExitCode == 0 -> Passed == false (0 tests nuevos)
        var emptyZeroSession = new FakeSandboxSession(Guid.NewGuid())
        {
            ExecHandler = cmd => cmd.Executable switch
            {
                "find" => new SandboxExecResult(0, string.Empty, string.Empty, TimedOut: false, Truncated: false),
                _ => new SandboxExecResult(0, string.Empty, string.Empty, TimedOut: false, Truncated: false),
            },
        };

        var runner = new DotnetTestRunner();
        var zeroReport = await runner.RunAsync(emptyZeroSession, TestBaseline.Empty, Ct);
        Assert.True(zeroReport.BuildSucceeded);
        Assert.Equal(0, zeroReport.TotalTests);
        Assert.False(zeroReport.Passed);

        // 2) Sin TRX + ExitCode != 0 -> fallo sintético
        var emptyFailSession = new FakeSandboxSession(Guid.NewGuid())
        {
            ExecHandler = cmd => cmd.Executable switch
            {
                "dotnet" when cmd.Arguments.Contains("test") => new SandboxExecResult(1, string.Empty, "Test host crashed", TimedOut: false, Truncated: false),
                "find" => new SandboxExecResult(0, string.Empty, string.Empty, TimedOut: false, Truncated: false),
                _ => new SandboxExecResult(0, string.Empty, string.Empty, TimedOut: false, Truncated: false),
            },
        };

        var failReport = await runner.RunAsync(emptyFailSession, TestBaseline.Empty, Ct);
        Assert.True(failReport.BuildSucceeded);
        Assert.Equal(1, failReport.FailedTests);
        Assert.False(failReport.Passed);
        Assert.Contains("Test host crashed", Assert.Single(failReport.FailedTestCases).ErrorMessage, StringComparison.Ordinal);

        // 3) TRX parcial en verde + ExitCode != 0 -> nunca aprueba, añade fallo sintético
        var partialCrashSession = new FakeSandboxSession(Guid.NewGuid())
        {
            ExecHandler = cmd => cmd.Executable switch
            {
                "dotnet" when cmd.Arguments.Contains("test") => new SandboxExecResult(134, string.Empty, "Stack overflow in second project", TimedOut: false, Truncated: false),
                "find" => new SandboxExecResult(0, $"{cmd.Arguments[0]}/p1.trx\n", string.Empty, TimedOut: false, Truncated: false),
                "cat" => new SandboxExecResult(0, BuildTrxXml(("Project1.NewTest", "Passed", null)), string.Empty, TimedOut: false, Truncated: false),
                _ => new SandboxExecResult(0, string.Empty, string.Empty, TimedOut: false, Truncated: false),
            },
        };

        var partialCrashReport = await runner.RunAsync(partialCrashSession, TestBaseline.Empty, Ct);
        Assert.False(partialCrashReport.Passed);
        Assert.Equal(1, partialCrashReport.FailedTests);
        Assert.Contains("Stack overflow", Assert.Single(partialCrashReport.FailedTestCases).ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DotnetTestRunner_throws_SandboxUnavailableException_on_timeout_truncated_or_malformed_trx()
    {
        var runner = new DotnetTestRunner();

        // Timeout en dotnet test
        var timeoutSession = new FakeSandboxSession(Guid.NewGuid())
        {
            ExecHandler = cmd => cmd.Executable == "dotnet" && cmd.Arguments.Contains("test")
                ? new SandboxExecResult(124, string.Empty, string.Empty, TimedOut: true, Truncated: false)
                : new SandboxExecResult(0, string.Empty, string.Empty, TimedOut: false, Truncated: false),
        };
        await Assert.ThrowsAsync<SandboxUnavailableException>(() => runner.RunAsync(timeoutSession, TestBaseline.Empty, Ct));

        // TRX truncado (> 1 MiB)
        var truncatedSession = new FakeSandboxSession(Guid.NewGuid())
        {
            ExecHandler = cmd => cmd.Executable switch
            {
                "find" => new SandboxExecResult(0, $"{cmd.Arguments[0]}/huge.trx\n", string.Empty, TimedOut: false, Truncated: false),
                "cat" => new SandboxExecResult(0, "<TestRun>", string.Empty, TimedOut: false, Truncated: true),
                _ => new SandboxExecResult(0, string.Empty, string.Empty, TimedOut: false, Truncated: false),
            },
        };
        var truncatedEx = await Assert.ThrowsAsync<SandboxUnavailableException>(() => runner.RunAsync(truncatedSession, TestBaseline.Empty, Ct));
        Assert.Contains("límite de lectura", truncatedEx.Message, StringComparison.Ordinal);

        // TRX malformado
        var malformedSession = new FakeSandboxSession(Guid.NewGuid())
        {
            ExecHandler = cmd => cmd.Executable switch
            {
                "find" => new SandboxExecResult(0, $"{cmd.Arguments[0]}/bad.trx\n", string.Empty, TimedOut: false, Truncated: false),
                "cat" => new SandboxExecResult(0, "<TestRun><unclosed", string.Empty, TimedOut: false, Truncated: false),
                _ => new SandboxExecResult(0, string.Empty, string.Empty, TimedOut: false, Truncated: false),
            },
        };
        await Assert.ThrowsAsync<SandboxUnavailableException>(() => runner.RunAsync(malformedSession, TestBaseline.Empty, Ct));
    }

    [Fact]
    public async Task DotnetTestRunner_CaptureBaselineAsync_returns_empty_when_build_fails_or_no_trx_found()
    {
        var runner = new DotnetTestRunner();

        var buildFailSession = new FakeSandboxSession(Guid.NewGuid())
        {
            ExecHandler = _ => new SandboxExecResult(1, "build failed", string.Empty, TimedOut: false, Truncated: false),
        };
        Assert.Empty((await runner.CaptureBaselineAsync(buildFailSession, Ct)).ExistingTests);

        var noTrxSession = new FakeSandboxSession(Guid.NewGuid())
        {
            ExecHandler = cmd => cmd.Executable switch
            {
                "find" => new SandboxExecResult(0, string.Empty, string.Empty, TimedOut: false, Truncated: false),
                _ => new SandboxExecResult(0, string.Empty, string.Empty, TimedOut: false, Truncated: false),
            },
        };
        Assert.Empty((await runner.CaptureBaselineAsync(noTrxSession, Ct)).ExistingTests);
    }

    // --- Orquestador + Persistencia + API -------------------------------------------------

    [Fact]
    public async Task Orchestrator_forwards_baseline_retries_with_feedback_redacts_secrets_and_persists_columns()
    {
        var coder = new RecordingCodingAgent();
        var observedBaselines = new List<TestBaseline>();
        var expectedBaseline = new TestBaseline(new HashSet<string>(["Tests.Existing.One", "Tests.Existing.Two"], StringComparer.Ordinal));

        await using var baseFactory = CreateReadyFactory();
        baseFactory.TestRunner.BaselineResult = expectedBaseline;

        var attempt = 0;
        baseFactory.TestRunner.RunHandler = (_, baseline) =>
        {
            observedBaselines.Add(baseline);
            attempt++;
            return attempt switch
            {
                1 => new TestReport(
                    BuildSucceeded: false,
                    TotalTests: 0,
                    PassedTests: 0,
                    FailedTests: 0,
                    SkippedTests: 0,
                    NewTests: [],
                    FailedTestCases: [],
                    Duration: TimeSpan.FromMilliseconds(100),
                    BuildOutput: $"src/ProfileController.cs(10,5): error CS0103 con {GitHubToken}"),
                2 => new TestReport(
                    BuildSucceeded: true,
                    TotalTests: 2,
                    PassedTests: 1,
                    FailedTests: 1,
                    SkippedTests: 0,
                    NewTests: [],
                    FailedTestCases: [new TestCaseResult("Tests.ProfileTests.Saves_changes", "Failed", $"Expected 1, Actual 0 (token {GitHubToken})", $"at ProfileTests.cs:10 ({GitHubToken})")],
                    Duration: TimeSpan.FromMilliseconds(200)),
                _ => new TestReport(
                    BuildSucceeded: true,
                    TotalTests: 3,
                    PassedTests: 3,
                    FailedTests: 0,
                    SkippedTests: 0,
                    NewTests: ["Tests.ProfileTests.New_save_test"],
                    FailedTestCases: [],
                    Duration: TimeSpan.FromMilliseconds(250),
                    BuildOutput: $"build ok con {GitHubToken}"),
            };
        };

        await using var factory = baseFactory.WithWebHostBuilder(b =>
            b.ConfigureTestServices(s => s.AddSingleton<ICodingAgent>(coder)));
        using var client = Authorized(factory);

        var run = await SubmitAndWaitForTerminalAsync(client);

        Assert.Equal("PullRequestCreated", run.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, run.GetProperty("failureReason").ValueKind);
        Assert.Equal(JsonValueKind.Null, run.GetProperty("failureDetail").ValueKind);

        // La línea base capturada se pasa intacta en cada intento
        Assert.Equal(3, observedBaselines.Count);
        Assert.All(observedBaselines, b => Assert.Equal(expectedBaseline.ExistingTests, b.ExistingTests));

        // Feedback exacto (y redactado) recibido por el Programador en cada reintento
        Assert.Equal(3, coder.Inputs.Count);
        Assert.Null(coder.Inputs[0].Feedback);
        Assert.Contains("CS0103", coder.Inputs[1].Feedback, StringComparison.Ordinal);
        Assert.DoesNotContain(GitHubToken, coder.Inputs[1].Feedback, StringComparison.Ordinal);
        Assert.Contains("Tests.ProfileTests.Saves_changes", coder.Inputs[2].Feedback, StringComparison.Ordinal);
        Assert.Contains("Expected 1, Actual 0", coder.Inputs[2].Feedback, StringComparison.Ordinal);
        Assert.DoesNotContain(GitHubToken, coder.Inputs[2].Feedback, StringComparison.Ordinal);

        // Eventos CodingAttemptFailed emitidos para intentos 1 y 2
        var failedEvents = run.GetProperty("events").EnumerateArray()
            .Where(e => e.GetProperty("type").GetString() == "CodingAttemptFailed")
            .ToList();
        Assert.Equal(2, failedEvents.Count);
        Assert.Equal(1, failedEvents[0].GetProperty("attempt").GetInt32());
        Assert.Equal(2, failedEvents[1].GetProperty("attempt").GetInt32());

        // testReport expuesto en GET /api/runs/{id}, redactado y persistido en run_test_reports
        var testReport = run.GetProperty("testReport");
        Assert.Equal(JsonValueKind.Object, testReport.ValueKind);
        Assert.True(testReport.GetProperty("buildSucceeded").GetBoolean());
        Assert.True(testReport.GetProperty("passed").GetBoolean());
        Assert.Equal(3, testReport.GetProperty("totalTests").GetInt32());
        Assert.Equal(0, testReport.GetProperty("failedTests").GetInt32());
        Assert.Equal("Tests.ProfileTests.New_save_test", Assert.Single(testReport.GetProperty("newTests").EnumerateArray()).GetString());
        Assert.DoesNotContain(GitHubToken, testReport.GetProperty("buildOutput").GetString(), StringComparison.Ordinal);

        var runId = run.GetProperty("runId").GetGuid();
        var reader = factory.Services.GetRequiredService<IRunReader>();
        var stored = await reader.GetTestReportAsync(runId, Ct);
        Assert.NotNull(stored);
        Assert.True(stored.Passed);
        Assert.Equal(["Tests.ProfileTests.New_save_test"], stored.NewTests);
        Assert.DoesNotContain(GitHubToken, stored.BuildOutput, StringComparison.Ordinal);

        // Verifica columnas denormalizadas y report_json redactado en SQLite
        await using var connection = new SqliteConnection($"Data Source={DatabasePath};Mode=ReadOnly");
        await connection.OpenAsync(Ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT passed, total_tests, failed_tests, new_tests_count, report_json FROM run_test_reports WHERE run_id = '{runId.ToString().ToUpperInvariant()}';";
        await using var sqlReader = await cmd.ExecuteReaderAsync(Ct);
        Assert.True(await sqlReader.ReadAsync(Ct));
        Assert.Equal(1L, sqlReader.GetInt64(0));
        Assert.Equal(3L, sqlReader.GetInt64(1));
        Assert.Equal(0L, sqlReader.GetInt64(2));
        Assert.Equal(1L, sqlReader.GetInt64(3));
        Assert.DoesNotContain(GitHubToken, sqlReader.GetString(4), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Orchestrator_fails_with_TestsFailed_after_exhausting_retries_when_no_new_tests_added()
    {
        var coder = new RecordingCodingAgent();
        await using var baseFactory = CreateReadyFactory();
        baseFactory.TestRunner.DefaultReport = new TestReport(
            BuildSucceeded: true,
            TotalTests: 5,
            PassedTests: 5,
            FailedTests: 0,
            SkippedTests: 0,
            NewTests: [],
            FailedTestCases: [],
            Duration: TimeSpan.FromMilliseconds(120));

        await using var factory = baseFactory.WithWebHostBuilder(b =>
            b.ConfigureTestServices(s => s.AddSingleton<ICodingAgent>(coder)));
        using var client = Authorized(factory);

        var run = await SubmitAndWaitForTerminalAsync(client);

        Assert.Equal("Failed", run.GetProperty("state").GetString());
        Assert.Equal("TestsFailed", run.GetProperty("failureReason").GetString());
        Assert.Contains("test nuevo", run.GetProperty("failureDetail").GetString(), StringComparison.OrdinalIgnoreCase);

        Assert.Equal(3, coder.Inputs.Count);
        Assert.Equal(1, baseFactory.TestRunner.BaselineCalls);
        Assert.Equal(3, baseFactory.TestRunner.RunCalls);

        var failedEvents = run.GetProperty("events").EnumerateArray()
            .Where(e => e.GetProperty("type").GetString() == "CodingAttemptFailed")
            .ToList();
        Assert.Equal(3, failedEvents.Count);
        Assert.Equal([1, 2, 3], failedEvents.Select(e => e.GetProperty("attempt").GetInt32()));

        // Verifica contadores en SQLite
        var runId = run.GetProperty("runId").GetGuid().ToString().ToUpperInvariant();
        await using var connection = new SqliteConnection($"Data Source={DatabasePath};Mode=ReadOnly");
        await connection.OpenAsync(Ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT coding_attempts, failed_coding_attempts FROM run_runs WHERE id = '{runId}';";
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));
        Assert.Equal(3L, reader.GetInt64(0));
        Assert.Equal(3L, reader.GetInt64(1));

        // El último TestReport sigue consultable en la API con passed = false
        var testReport = run.GetProperty("testReport");
        Assert.Equal(JsonValueKind.Object, testReport.ValueKind);
        Assert.False(testReport.GetProperty("passed").GetBoolean());
        Assert.Empty(testReport.GetProperty("newTests").EnumerateArray());
    }

    [Fact]
    public async Task Orchestrator_fails_with_StageError_when_baseline_or_test_runner_throws_SandboxUnavailableException()
    {
        // Fallo en CaptureBaselineAsync
        await using (var baselineFactory = CreateReadyFactory())
        {
            baselineFactory.TestRunner.FailBaselineWith = "contenedor detenido al capturar baseline";
            using var client = baselineFactory.CreateAuthorizedClient();

            var run = await SubmitAndWaitForTerminalAsync(client);

            Assert.Equal("Failed", run.GetProperty("state").GetString());
            Assert.Equal("StageError", run.GetProperty("failureReason").GetString());
            Assert.Contains("capturar baseline", run.GetProperty("failureDetail").GetString(), StringComparison.Ordinal);
            Assert.Equal(JsonValueKind.Null, run.GetProperty("testReport").ValueKind);
            Assert.Equal(1, baselineFactory.TestRunner.BaselineCalls);
            Assert.Equal(0, baselineFactory.TestRunner.RunCalls);
        }

        // Fallo en RunAsync
        var runDataDir = Path.Combine(_root, "data-run-fail");
        await using var runFactory = new CodeNetixFactory(runDataDir, _webRoot, new Dictionary<string, string?>(CodeNetixFactory.CompleteSettings));
        runFactory.TestRunner.FailRunWith = "dotnet test agotó el tiempo máximo en el Sandbox";
        using var runClient = runFactory.CreateAuthorizedClient();

        var failedRun = await SubmitAndWaitForTerminalAsync(runClient);

        Assert.Equal("Failed", failedRun.GetProperty("state").GetString());
        Assert.Equal("StageError", failedRun.GetProperty("failureReason").GetString());
        Assert.Contains("dotnet test agotó el tiempo máximo", failedRun.GetProperty("failureDetail").GetString(), StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Null, failedRun.GetProperty("testReport").ValueKind);
    }

    [Fact]
    public async Task Api_returns_null_testReport_when_run_fails_before_testing()
    {
        await using var factory = CreateReadyFactory();
        factory.ChatClient.Respond(FakeChatClient.NotAutomatableAnswer("missing_information", "Faltan detalles para automatizar."));
        using var client = factory.CreateAuthorizedClient();

        var run = await SubmitAndWaitForTerminalAsync(client);

        Assert.Equal("Failed", run.GetProperty("state").GetString());
        Assert.Equal("NotAutomatable", run.GetProperty("failureReason").GetString());
        Assert.Equal(JsonValueKind.Null, run.GetProperty("testReport").ValueKind);
        Assert.Equal(0, factory.TestRunner.BaselineCalls);
        Assert.Equal(0, factory.TestRunner.RunCalls);
    }

    // --- Utilidades ----------------------------------------------------------------------

    private CodeNetixFactory CreateReadyFactory() =>
        new(_dataDirectory, _webRoot, new Dictionary<string, string?>(CodeNetixFactory.CompleteSettings));

    private static HttpClient Authorized(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", CodeNetixFactory.TestAccessToken);
        return client;
    }

    private static async Task<JsonElement> SubmitAndWaitForTerminalAsync(HttpClient client)
    {
        using var submit = await client.PostAsJsonAsync("/api/events/manual", new { url = IssueUrl, requestId = Guid.NewGuid().ToString() }, Ct);
        Assert.Equal(HttpStatusCode.Created, submit.StatusCode);
        using var submitJson = JsonDocument.Parse(await submit.Content.ReadAsStringAsync(Ct));
        var eventId = submitJson.RootElement.GetProperty("eventId").GetGuid();

        var sw = Stopwatch.StartNew();
        Guid runId = Guid.Empty;
        while (runId == Guid.Empty)
        {
            using var evRes = await client.GetAsync($"/api/events/{eventId}", Ct);
            using var evJson = JsonDocument.Parse(await evRes.Content.ReadAsStringAsync(Ct));
            if (evJson.RootElement.TryGetProperty("runId", out var r) && r.ValueKind == JsonValueKind.String)
            {
                runId = r.GetGuid();
                break;
            }

            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), "No se creó la Ejecución a tiempo.");
            await Task.Delay(50, Ct);
        }

        while (true)
        {
            using var runRes = await client.GetAsync($"/api/runs/{runId}", Ct);
            Assert.Equal(HttpStatusCode.OK, runRes.StatusCode);
            using var runJson = JsonDocument.Parse(await runRes.Content.ReadAsStringAsync(Ct));
            var root = runJson.RootElement;
            var state = root.GetProperty("state").GetString();
            var eventTypes = root.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("type").GetString()).ToList();
            var settled = !eventTypes.Contains("SandboxCreated") || eventTypes.Contains("SandboxReleased");
            if (state is "Failed" or "PullRequestCreated" or "Cancelled" && settled)
            {
                return root.Clone();
            }

            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"La Ejecución sigue en {state}.");
            await Task.Delay(50, Ct);
        }
    }

    private static string BuildTrxXml(params (string Name, string Outcome, string? Error)[] tests)
    {
        var results = string.Join(Environment.NewLine, tests.Select((t, i) =>
            t.Error is null
                ? $"<UnitTestResult testId=\"t{i}\" testName=\"{t.Name}\" outcome=\"{t.Outcome}\" duration=\"00:00:00.0100000\" />"
                : $"<UnitTestResult testId=\"t{i}\" testName=\"{t.Name}\" outcome=\"{t.Outcome}\" duration=\"00:00:00.0100000\"><Output><ErrorInfo><Message>{t.Error}</Message></ErrorInfo></Output></UnitTestResult>"));

        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <TestRun id="1" name="run" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results>
                {results}
              </Results>
            </TestRun>
            """;
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

    private sealed class RecordingCodingAgent : ICodingAgent
    {
        public List<CodingInput> Inputs { get; } = [];

        public Task<CodingResult> RunAsync(CodingInput input, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Inputs.Add(input);
            return Task.FromResult<CodingResult>(new CodingResult.Completed($"intento {input.Attempt}", "claude-sonnet-5-5"));
        }
    }
}
