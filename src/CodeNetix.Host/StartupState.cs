namespace CodeNetix.Host;

/// <summary>
/// Indica si <see cref="StartupReconciler"/> ha terminado. Evita que <c>/api/health</c>
/// responda "Ready" antes de que la base de datos exista. (El estado de salud completo,
/// <c>IInstanceHealth</c>, llega en la historia 1.2.)
/// </summary>
internal sealed class StartupState
{
    private volatile bool _completed;

    public bool IsCompleted => _completed;

    public void MarkCompleted() => _completed = true;
}
