using System.Collections.Concurrent;
using CodeNetix.Contracts;
using CodeNetix.Sandbox;

namespace CodeNetix.Host.Tests;

/// <summary>
/// <see cref="ISandboxManager"/> falso (por defecto en <see cref="CodeNetixFactory"/>): ningún test de Host necesita
/// Docker. Registra las sesiones creadas y liberadas; <see cref="FailWith"/> hace fallar la creación.
/// </summary>
internal sealed class FakeSandboxManager : ISandboxManager
{
    public const string Image = "codenetix-sandbox:fake";

    private readonly ConcurrentQueue<FakeSandboxSession> _sessions = new();

    /// <summary>Si tiene valor, <see cref="CreateForRunAsync"/> lanza <see cref="SandboxCreationException"/> con este motivo.</summary>
    public string? FailWith { get; set; }
    public Action<FakeSandboxSession>? OnSessionCreated { get; set; }



    public IReadOnlyList<FakeSandboxSession> Sessions => [.. _sessions];

    public Task<ISandboxSession> CreateForRunAsync(Guid runId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (FailWith is { } reason)
        {
            throw new SandboxCreationException(reason);
        }

        var session = new FakeSandboxSession(runId);
        _sessions.Enqueue(session);
        OnSessionCreated?.Invoke(session);
        return Task.FromResult<ISandboxSession>(session);
    }
}

internal sealed class FakeSandboxSession(Guid runId) : ISandboxSession
{
    private int _disposals;

    public Guid RunId { get; } = runId;

    public string ContainerId { get; } = "fake-" + Guid.NewGuid().ToString("N");

    public string Image => FakeSandboxManager.Image;

    public int Disposals => Volatile.Read(ref _disposals);

    public List<SandboxCommand> Commands { get; } = [];

    public bool HasWorkspaceChanges { get; set; } = true;

    public Func<SandboxCommand, SandboxExecResult>? ExecHandler { get; set; }

    public Func<Stream, CancellationToken, Task>? ExportWorkspaceHandler { get; set; }

    public int ExportCalls { get; private set; }

    public Task<SandboxExecResult> ExecAsync(SandboxCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (Commands)
        {
            Commands.Add(command);
        }

        if (ExecHandler is not null)
        {
            return Task.FromResult(ExecHandler(command));
        }

        if (command.Executable == "claude")
        {
            return Task.FromResult(new SandboxExecResult(0, "{\"result\": \"changes made\"}", string.Empty, TimedOut: false, Truncated: false));
        }

        return Task.FromResult(new SandboxExecResult(0, string.Empty, string.Empty, TimedOut: false, Truncated: false));
    }

    public Task<bool> HasChangesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(HasWorkspaceChanges);
    }

    public async Task ExportWorkspaceAsync(Stream destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ExportCalls++;
        if (ExportWorkspaceHandler is not null)
        {
            await ExportWorkspaceHandler(destination, cancellationToken);
        }
    }


    public Task StopAsync() => Task.CompletedTask;

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposals);
        return ValueTask.CompletedTask;
    }
}

/// <summary><see cref="ISandboxHealthCheck"/> falso: sin motivos salvo que se fijen en <see cref="Reasons"/>.</summary>
internal sealed class FakeSandboxHealthCheck : ISandboxHealthCheck
{
    private int _calls;

    public IReadOnlyList<string> Reasons { get; set; } = [];

    /// <summary>Espera antes de responder (respeta la cancelación, como una comprobación real de Docker).</summary>
    public TimeSpan Delay { get; set; } = TimeSpan.Zero;

    public int Calls => Volatile.Read(ref _calls);

    public async Task<IReadOnlyList<string>> CheckAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, cancellationToken);
        }

        return Reasons;
    }
}

/// <summary>
/// <see cref="ISandboxReconciler"/> falso (por defecto en <see cref="CodeNetixFactory"/>): no habla con Docker.
/// <see cref="OnCleanup"/> permite observar el momento de la llamada en el arranque.
/// </summary>
internal sealed class FakeSandboxReconciler : ISandboxReconciler
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public Action? OnCleanup { get; set; }

    public SandboxCleanupSummary Result { get; set; } = new(0, 0);

    public Task<SandboxCleanupSummary> CleanupOrphansAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _calls);
        OnCleanup?.Invoke();
        return Task.FromResult(Result);
    }
}

/// <summary><see cref="ISandboxToolsProvisioner"/> falso: no instala paquetes de verdad.</summary>
internal sealed class FakeSandboxToolsProvisioner : ISandboxToolsProvisioner
{
    public int Calls { get; private set; }
    public string? FailWith { get; set; }

    public Task EnsureClaudeCliAsync(ISandboxSession session, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        if (FailWith is not null)
        {
            throw new SandboxUnavailableException(FailWith);
        }

        return Task.CompletedTask;
    }
}

/// <summary><see cref="ITestRunner"/> falso (por defecto en <see cref="CodeNetixFactory"/>): aprueba con un test nuevo.</summary>
internal sealed class FakeTestRunner : ITestRunner
{
    private readonly ConcurrentQueue<TestReport> _queuedReports = new();

    public int BaselineCalls { get; private set; }

    public int RunCalls { get; private set; }

    public TestBaseline BaselineResult { get; set; } = TestBaseline.Empty;

    public string? FailBaselineWith { get; set; }

    public string? FailRunWith { get; set; }

    public Func<ISandboxSession, TestBaseline, TestReport>? RunHandler { get; set; }

    public TestReport DefaultReport { get; set; } = new(
        BuildSucceeded: true,
        TotalTests: 1,
        PassedTests: 1,
        FailedTests: 0,
        SkippedTests: 0,
        NewTests: ["CodeNetix.GeneratedTests.AddedTest"],
        FailedTestCases: [],
        Duration: TimeSpan.FromMilliseconds(50));

    public void EnqueueReport(TestReport report) => _queuedReports.Enqueue(report);

    public Task<TestBaseline> CaptureBaselineAsync(ISandboxSession session, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BaselineCalls++;
        if (FailBaselineWith is not null)
        {
            throw new SandboxUnavailableException(FailBaselineWith);
        }

        return Task.FromResult(BaselineResult);
    }

    public Task<TestReport> RunAsync(ISandboxSession session, TestBaseline baseline, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RunCalls++;
        if (FailRunWith is not null)
        {
            throw new SandboxUnavailableException(FailRunWith);
        }

        if (RunHandler is not null)
        {
            return Task.FromResult(RunHandler(session, baseline));
        }

        if (_queuedReports.TryDequeue(out var report))
        {
            return Task.FromResult(report);
        }

        return Task.FromResult(DefaultReport);
    }
}
