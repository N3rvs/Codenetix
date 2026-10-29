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
