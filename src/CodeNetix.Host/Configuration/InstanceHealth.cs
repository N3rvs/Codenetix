using CodeNetix.Contracts;

namespace CodeNetix.Host.Configuration;

/// <summary>
/// Implementación de <see cref="IInstanceHealth"/>. La publica <see cref="StartupReconciler"/> al arrancar y, desde la
/// historia 8.1, <see cref="PanelSecretsService"/> al guardar claves desde el Panel (solo <c>Diagnostic</c> → <c>Ready</c>
/// o <c>Diagnostic</c>: una instancia <c>Ready</c> no se degrada, sus workers ya están en marcha).
/// </summary>
internal sealed class InstanceHealth : IInstanceHealth
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile Snapshot _current = new(InstanceHealthStatus.Starting, []);
    private volatile IReadOnlyList<string> _startupReasons = [];

    public InstanceHealthStatus Status => _current.Status;

    public IReadOnlyList<string> Reasons => _current.Reasons;

    /// <summary>
    /// Motivos del arranque que solo se resuelven reiniciando (p. ej. Ejecuciones que la reconciliación no pudo cerrar):
    /// una reevaluación posterior los conserva.
    /// </summary>
    public IReadOnlyList<string> StartupReasons => _startupReasons;

    public Task WhenReadyAsync(CancellationToken cancellationToken) => _ready.Task.WaitAsync(cancellationToken);

    /// <summary>Lectura atómica de estado y motivos (para <c>/api/health</c>).</summary>
    public (InstanceHealthStatus Status, IReadOnlyList<string> Reasons) Read()
    {
        var current = _current;
        return (current.Status, current.Reasons);
    }

    public void RecordStartupReasons(IReadOnlyList<string> reasons)
    {
        ArgumentNullException.ThrowIfNull(reasons);
        _startupReasons = [.. reasons];
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
