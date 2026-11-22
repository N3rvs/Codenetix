using CodeNetix.Contracts;
using CodeNetix.Sandbox.Docker;
using Microsoft.Extensions.Logging;

namespace CodeNetix.Sandbox;

/// <summary>
/// Limpieza de recursos huérfanos del Sandbox tras un reinicio del Host (AD-18 paso 4, FR-24), cuando la
/// reconciliación de Ejecuciones ya ha cerrado todas las no terminales:
/// <list type="number">
/// <item>detiene (con gracia acotada) y elimina cada contenedor con etiqueta <c>codenetix.run</c>;</item>
/// <item>elimina cada volumen cuyo nombre empieza exactamente por <c>codenetix-ws-</c>; nunca <c>codenetix-tools</c>
/// ni un volumen ajeno.</item>
/// </list>
/// Solo se toca un recurso cuyo <c>runId</c> (la etiqueta <c>codenetix.run</c> o, en un volumen sin ella, el sufijo
/// del nombre) es una Ejecución de esta instancia (<see cref="IRunReader"/>) en estado terminal: los de otra instancia
/// que comparta el motor Docker, o los de una Ejecución aún activa, se dejan y se registran.
/// Best-effort con un plazo total (<see cref="CleanupBudget"/>): nunca lanza si Docker no responde o falla un recurso.
/// </summary>
internal sealed partial class SandboxReconciler(
    DockerClient docker,
    IRunReader runs,
    ILogger<SandboxReconciler> logger) : ISandboxReconciler
{
    private const int StopGraceSeconds = 5;

    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Plazo total de la limpieza: Docker colgado nunca bloquea el arranque más allá de esto.</summary>
    internal static readonly TimeSpan CleanupBudget = TimeSpan.FromSeconds(60);

    public async Task<SandboxCleanupSummary> CleanupOrphansAsync(CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(CleanupBudget);
        var summary = await CleanupCoreAsync(budget.Token, cancellationToken).ConfigureAwait(false);
        if (budget.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            LogBudgetExceeded(CleanupBudget.TotalSeconds);
        }

        return summary;
    }

    private async Task<SandboxCleanupSummary> CleanupCoreAsync(CancellationToken budgetToken, CancellationToken cancellationToken)
    {
        var containersRemoved = 0;
        var volumesRemoved = 0;

        IReadOnlyList<DockerResource> containers = [];
        var dockerReachable = true;
        try
        {
            containers = await docker.ListContainersAsync(SandboxManager.RunLabel, budgetToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsNonFatal(ex, cancellationToken))
        {
            LogListContainersFailed(ex.Message);
            if (ex is not DockerApiException)
            {
                dockerReachable = false;
            }
        }

        foreach (var container in containers)
        {
            if (!await IsFinishedRunOfThisInstanceAsync(container.Id, container.LabelValue, budgetToken, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            try
            {
                await docker.StopContainerAsync(container.Id, StopGraceSeconds, StopTimeout, budgetToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsNonFatal(ex, cancellationToken))
            {
                LogStopContainerFailed(container.Id, ex.Message);
            }

            try
            {
                if (await docker.RemoveContainerAsync(container.Id, budgetToken).ConfigureAwait(false))
                {
                    containersRemoved++;
                }
            }
            catch (Exception ex) when (IsNonFatal(ex, cancellationToken))
            {
                LogRemoveContainerFailed(container.Id, ex.Message);
            }
        }

        if (!dockerReachable)
        {
            return new SandboxCleanupSummary(containersRemoved, volumesRemoved);
        }

        IReadOnlyList<DockerResource> volumes = [];
        try
        {
            volumes = await docker.ListVolumesAsync(SandboxManager.RunLabel, budgetToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsNonFatal(ex, cancellationToken))
        {
            LogListVolumesFailed(ex.Message);
        }

        foreach (var volume in volumes)
        {
            if (!IsWorkspaceVolume(volume.Id))
            {
                continue;
            }

            var owner = volume.LabelValue ?? volume.Id[SandboxManager.VolumePrefix.Length..];
            if (!await IsFinishedRunOfThisInstanceAsync(volume.Id, owner, budgetToken, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            try
            {
                if (await docker.RemoveVolumeAsync(volume.Id, budgetToken).ConfigureAwait(false))
                {
                    volumesRemoved++;
                }
            }
            catch (Exception ex) when (IsNonFatal(ex, cancellationToken))
            {
                LogRemoveVolumeFailed(volume.Id, ex.Message);
            }
        }

        return new SandboxCleanupSummary(containersRemoved, volumesRemoved);
    }

    /// <summary>¿<paramref name="runId"/> es una Ejecución de esta instancia ya terminal? Si no, se registra y se deja.</summary>
    private async Task<bool> IsFinishedRunOfThisInstanceAsync(string resource, string? runId, CancellationToken budgetToken, CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(runId, "D", out var id))
        {
            LogUnknownOwner(resource, runId ?? "(sin etiqueta)");
            return false;
        }

        RunSummary? run;
        try
        {
            run = await runs.FindAsync(id.ToString("D"), budgetToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsNonFatal(ex, cancellationToken))
        {
            LogLookupFailed(resource, ex.Message);
            return false;
        }

        if (run is null)
        {
            LogUnknownOwner(resource, id.ToString("D"));
            return false;
        }

        if (!run.State.IsTerminal())
        {
            LogActiveOwner(resource, run.ShortId, run.State);
            return false;
        }

        return true;
    }

    internal static bool IsWorkspaceVolume(string? name) =>
        !string.IsNullOrEmpty(name)
        && !string.Equals(name, SandboxManager.ToolsVolume, StringComparison.Ordinal)
        && name.StartsWith(SandboxManager.VolumePrefix, StringComparison.Ordinal);

    private static bool IsNonFatal(Exception ex, CancellationToken cancellationToken) =>
        ex is not OutOfMemoryException and not StackOverflowException
        && (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested);

    [LoggerMessage(Level = LogLevel.Warning, Message = "La limpieza de huérfanos del Sandbox ha superado {Seconds} s; el arranque continúa sin terminarla.")]
    private partial void LogBudgetExceeded(double seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Se deja '{Resource}': su Ejecución '{RunId}' no es de esta instancia (otra instancia de CodeNetix puede compartir el motor Docker).")]
    private partial void LogUnknownOwner(string resource, string runId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Se deja '{Resource}': su Ejecución {ShortId} sigue en {State}.")]
    private partial void LogActiveOwner(string resource, string shortId, RunState state);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Se deja '{Resource}': no se ha podido consultar su Ejecución ({Reason}).")]
    private partial void LogLookupFailed(string resource, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se han podido listar los contenedores huérfanos del Sandbox en Docker: {Reason}.")]
    private partial void LogListContainersFailed(string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se ha podido detener el contenedor huérfano '{ContainerId}': {Reason}.")]
    private partial void LogStopContainerFailed(string containerId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se ha podido eliminar el contenedor huérfano '{ContainerId}': {Reason}.")]
    private partial void LogRemoveContainerFailed(string containerId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se han podido listar los volúmenes huérfanos del Sandbox en Docker: {Reason}.")]
    private partial void LogListVolumesFailed(string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se ha podido eliminar el volumen huérfano '{VolumeName}': {Reason}.")]
    private partial void LogRemoveVolumeFailed(string volumeName, string reason);
}
