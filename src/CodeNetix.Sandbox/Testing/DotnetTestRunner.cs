using System.Diagnostics;
using System.Text;
using CodeNetix.Contracts;

namespace CodeNetix.Sandbox.Testing;

/// <summary>
/// Implementación de <see cref="ITestRunner"/> (AD-1, AD-6) que ejecuta <c>dotnet build</c> y
/// <c>dotnet test --logger trx</c> dentro de <c>/workspace</c> en <see cref="ISandboxSession"/>, redirigiendo
/// los artefactos de compilación fuera de <c>/workspace</c> para no ensuciar la detección de cambios.
/// </summary>
internal sealed class DotnetTestRunner : ITestRunner
{
    /// <summary>Tiempo máximo de <c>dotnet build</c> o <c>dotnet test</c> dentro del Sandbox.</summary>
    internal static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(10);

    /// <summary>Tiempo máximo para listar, leer y limpiar los archivos TRX generados en <c>/tmp</c>.</summary>
    internal static readonly TimeSpan ReadTrxTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Ruta fuera de <c>/workspace</c> donde MSBuild coloca <c>bin</c>/<c>obj</c>.</summary>
    internal const string ArtifactsPath = "/tmp/codenetix-artifacts";

    internal const int MaxBuildOutputChars = 8192;

    public async Task<TestBaseline> CaptureBaselineAsync(ISandboxSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        var buildResult = await RunBuildAsync(session, cancellationToken).ConfigureAwait(false);
        if (buildResult.ExitCode != 0)
        {
            return TestBaseline.Empty;
        }

        var trxDir = $"/tmp/codenetix-trx-{Guid.NewGuid():N}";
        try
        {
            var testResult = await RunDotnetTestAsync(session, trxDir, cancellationToken).ConfigureAwait(false);
            _ = testResult; // Incluso si algún test base fallaba, registramos sus nombres como preexistentes.

            var parsedFiles = await ReadAndParseAllTrxAsync(session, trxDir, cancellationToken).ConfigureAwait(false);
            if (parsedFiles.Count == 0)
            {
                return TestBaseline.Empty;
            }

            var merged = TrxParser.Merge(parsedFiles);
            return TrxParser.ToBaseline(merged);
        }
        finally
        {
            await CleanupTrxDirAsync(session, trxDir).ConfigureAwait(false);
        }
    }

    public async Task<TestReport> RunAsync(ISandboxSession session, TestBaseline baseline, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(baseline);

        var stopwatch = Stopwatch.StartNew();

        var buildResult = await RunBuildAsync(session, cancellationToken).ConfigureAwait(false);
        if (buildResult.ExitCode != 0)
        {
            stopwatch.Stop();
            return new TestReport(
                BuildSucceeded: false,
                TotalTests: 0,
                PassedTests: 0,
                FailedTests: 0,
                SkippedTests: 0,
                NewTests: [],
                FailedTestCases: [],
                Duration: stopwatch.Elapsed,
                BuildOutput: CombineOutput(buildResult.StdOut, buildResult.StdErr));
        }

        var trxDir = $"/tmp/codenetix-trx-{Guid.NewGuid():N}";
        try
        {
            var testResult = await RunDotnetTestAsync(session, trxDir, cancellationToken).ConfigureAwait(false);
            var parsedFiles = await ReadAndParseAllTrxAsync(session, trxDir, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            if (parsedFiles.Count == 0)
            {
                var combined = CombineOutput(testResult.StdOut, testResult.StdErr);
                if (testResult.ExitCode != 0)
                {
                    return new TestReport(
                        BuildSucceeded: true,
                        TotalTests: 1,
                        PassedTests: 0,
                        FailedTests: 1,
                        SkippedTests: 0,
                        NewTests: [],
                        FailedTestCases: [new TestCaseResult("dotnet test", "Failed", string.IsNullOrWhiteSpace(combined) ? $"dotnet test terminó con código {testResult.ExitCode}" : combined)],
                        Duration: stopwatch.Elapsed,
                        BuildOutput: combined);
                }

                return new TestReport(
                    BuildSucceeded: true,
                    TotalTests: 0,
                    PassedTests: 0,
                    FailedTests: 0,
                    SkippedTests: 0,
                    NewTests: [],
                    FailedTestCases: [],
                    Duration: stopwatch.Elapsed);
            }

            var merged = TrxParser.Merge(parsedFiles);
            var report = TrxParser.ToReport(merged, baseline, stopwatch.Elapsed);

            // Si dotnet test terminó con error (p. ej. crash del testhost) pero el TRX parcial no registró fallos,
            // añadimos un fallo sintético para no aprobar jamás un run roto.
            if (testResult.ExitCode != 0 && report.FailedTests == 0)
            {
                var combined = CombineOutput(testResult.StdOut, testResult.StdErr);
                var message = string.IsNullOrWhiteSpace(combined)
                    ? $"dotnet test terminó con código {testResult.ExitCode}"
                    : combined;
                var failedCases = report.FailedTestCases.Append(new TestCaseResult("dotnet test", "Failed", message)).ToArray();
                return report with
                {
                    TotalTests = report.TotalTests + 1,
                    FailedTests = 1,
                    FailedTestCases = failedCases,
                    BuildOutput = string.IsNullOrWhiteSpace(combined) ? report.BuildOutput : combined,
                };
            }

            return report;
        }
        finally
        {
            await CleanupTrxDirAsync(session, trxDir).ConfigureAwait(false);
        }
    }

    private static async Task<SandboxExecResult> RunBuildAsync(ISandboxSession session, CancellationToken cancellationToken)
    {
        var command = new SandboxCommand(
            "dotnet",
            ["build", "--nologo", "-v", "minimal", "-p:UseArtifactsOutput=true", $"-p:ArtifactsPath={ArtifactsPath}"])
        {
            WorkingDirectory = SandboxManager.WorkspacePath,
            Timeout = CommandTimeout,
        };

        var result = await session.ExecAsync(command, cancellationToken).ConfigureAwait(false);
        if (result.TimedOut)
        {
            throw new SandboxUnavailableException("dotnet build agotó el tiempo máximo en el Sandbox");
        }

        return result;
    }

    private static async Task<SandboxExecResult> RunDotnetTestAsync(ISandboxSession session, string trxDir, CancellationToken cancellationToken)
    {
        var command = new SandboxCommand(
            "dotnet",
            ["test", "--no-build", "--nologo", "-p:UseArtifactsOutput=true", $"-p:ArtifactsPath={ArtifactsPath}", "--results-directory", trxDir, "--logger", "trx"])
        {
            WorkingDirectory = SandboxManager.WorkspacePath,
            Timeout = CommandTimeout,
        };

        var result = await session.ExecAsync(command, cancellationToken).ConfigureAwait(false);
        if (result.TimedOut)
        {
            throw new SandboxUnavailableException("dotnet test agotó el tiempo máximo en el Sandbox");
        }

        return result;
    }

    private static async Task<List<TrxParser.ParsedTrx>> ReadAndParseAllTrxAsync(ISandboxSession session, string trxDir, CancellationToken cancellationToken)
    {
        var findCommand = new SandboxCommand("find", [trxDir, "-name", "*.trx", "-type", "f"])
        {
            WorkingDirectory = SandboxManager.WorkspacePath,
            Timeout = ReadTrxTimeout,
        };

        var findResult = await session.ExecAsync(findCommand, cancellationToken).ConfigureAwait(false);
        if (findResult.TimedOut)
        {
            throw new SandboxUnavailableException("la búsqueda de archivos TRX agotó el tiempo máximo en el Sandbox");
        }

        if (findResult.ExitCode != 0 || string.IsNullOrWhiteSpace(findResult.StdOut))
        {
            return [];
        }

        var paths = findResult.StdOut
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

        var parsed = new List<TrxParser.ParsedTrx>(paths.Length);
        foreach (var trxPath in paths)
        {
            var catCommand = new SandboxCommand("cat", [trxPath])
            {
                WorkingDirectory = SandboxManager.WorkspacePath,
                Timeout = ReadTrxTimeout,
            };

            var catResult = await session.ExecAsync(catCommand, cancellationToken).ConfigureAwait(false);
            if (catResult.TimedOut)
            {
                throw new SandboxUnavailableException("la lectura del archivo TRX agotó el tiempo máximo en el Sandbox");
            }

            if (catResult.Truncated)
            {
                throw new SandboxUnavailableException("el archivo TRX superó el límite de lectura");
            }

            if (catResult.ExitCode != 0 || string.IsNullOrWhiteSpace(catResult.StdOut))
            {
                continue;
            }

            try
            {
                parsed.Add(TrxParser.Parse(catResult.StdOut));
            }
            catch (FormatException ex)
            {
                throw new SandboxUnavailableException($"no se pudo parsear el archivo TRX: {ex.Message}", ex);
            }
        }

        return parsed;
    }

    private static async Task CleanupTrxDirAsync(ISandboxSession session, string trxDir)
    {
        try
        {
            var command = new SandboxCommand("rm", ["-rf", trxDir])
            {
                WorkingDirectory = SandboxManager.WorkspacePath,
                Timeout = ReadTrxTimeout,
            };
            await session.ExecAsync(command, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SandboxUnavailableException or OperationCanceledException)
        {
            // Limpieza de mejor esfuerzo.
        }
    }

    internal static string CombineOutput(string stdout, string stderr)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(stdout))
        {
            sb.Append(stdout.Trim());
        }

        if (!string.IsNullOrWhiteSpace(stderr))
        {
            if (sb.Length > 0)
            {
                sb.AppendLine();
            }

            sb.Append(stderr.Trim());
        }

        var text = sb.ToString();
        if (text.Length <= MaxBuildOutputChars)
        {
            return text;
        }

        const string prefix = "…\n";
        return prefix + text[^(MaxBuildOutputChars - prefix.Length)..];
    }
}
