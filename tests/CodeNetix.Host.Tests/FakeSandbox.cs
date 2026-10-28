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

    public Task<SandboxExecResult> ExecAsync(SandboxCommand command, CancellationToken cancellationToken) =>
        Task.FromResult(new SandboxExecResult(0, string.Empty, string.Empty, TimedOut: false, Truncated: false));

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
    public IReadOnlyList<string> Reasons { get; set; } = [];

    public Task<IReadOnlyList<string>> CheckAsync(CancellationToken cancellationToken) => Task.FromResult(Reasons);
}
