using CodeNetix.Contracts;

namespace CodeNetix.Host.Configuration;

/// <summary>Implementación de <see cref="IInstanceHealth"/>; solo la publica <see cref="StartupReconciler"/>.</summary>
internal sealed class InstanceHealth : IInstanceHealth
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile Snapshot _current = new(InstanceHealthStatus.Starting, []);

    public InstanceHealthStatus Status => _current.Status;

    public IReadOnlyList<string> Reasons => _current.Reasons;

    public Task WhenReadyAsync(CancellationToken cancellationToken) => _ready.Task.WaitAsync(cancellationToken);

    /// <summary>Lectura atómica de estado y motivos (para <c>/api/health</c>).</summary>
    public (InstanceHealthStatus Status, IReadOnlyList<string> Reasons) Read()
    {
        var current = _current;
        return (current.Status, current.Reasons);
    }

    public void MarkReady()
    {
        _current = new Snapshot(InstanceHealthStatus.Ready, []);
        _ready.TrySetResult();
    }

    public void MarkDiagnostic(IReadOnlyList<string> reasons)
    {
        ArgumentNullException.ThrowIfNull(reasons);
        _current = new Snapshot(InstanceHealthStatus.Diagnostic, [.. reasons]);
    }

    private sealed record Snapshot(InstanceHealthStatus Status, IReadOnlyList<string> Reasons);
}
