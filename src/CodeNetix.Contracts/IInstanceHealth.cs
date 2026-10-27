namespace CodeNetix.Contracts;

/// <summary>Estado de salud de la instancia (AD-18).</summary>
public enum InstanceHealthStatus
{
    /// <summary><c>StartupReconciler</c> todavía no ha terminado.</summary>
    Starting,

    /// <summary>Configuración completa: la instancia puede procesar trabajo.</summary>
    Ready,

    /// <summary>La instancia está viva pero no procesa trabajo; <see cref="IInstanceHealth.Reasons"/> dice qué falta.</summary>
    Diagnostic,
}

/// <summary>
/// Salud de la instancia publicada por <c>StartupReconciler</c>. Todo <c>BackgroundService</c>
/// debe esperar a <see cref="WhenReadyAsync"/> antes de procesar trabajo.
/// </summary>
public interface IInstanceHealth
{
    InstanceHealthStatus Status { get; }

    /// <summary>Motivos del modo diagnóstico (frases en español, nunca valores secretos). Vacío fuera de <see cref="InstanceHealthStatus.Diagnostic"/>.</summary>
    IReadOnlyList<string> Reasons { get; }

    /// <summary>Se completa solo cuando el estado es <see cref="InstanceHealthStatus.Ready"/>.</summary>
    Task WhenReadyAsync(CancellationToken cancellationToken);
}
