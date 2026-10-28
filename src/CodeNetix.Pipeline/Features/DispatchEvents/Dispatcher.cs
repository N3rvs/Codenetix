using CodeNetix.Contracts;
using CodeNetix.Pipeline.Features.RunPipeline;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodeNetix.Pipeline.Features.DispatchEvents;

/// <summary>
/// Dispatcher (AD-4, AD-18): único creador de Ejecuciones. Espera a que la instancia esté <c>Ready</c> (en
/// <c>Diagnostic</c> no crea nada); al arrancar vuelve a orquestar las Ejecuciones que quedaron en <c>Received</c>
/// (corte entre crearlas y orquestarlas), y después despierta con <see cref="IEventDispatchSignal"/> o cada
/// <see cref="DispatcherOptions.PollInterval"/>. Concurrencia 1: cada Ejecución se entrega al orquestador antes de seguir.
/// <list type="bullet">
/// <item>Si el orquestador lanza, la Ejecución se cierra como <c>Failed(StageError)</c> (<see cref="StuckRunRecovery"/>).</item>
/// <item>Un fallo al despachar un Evento se registra y no detiene el bucle: el Evento se reintenta pasado
/// <see cref="DispatcherOptions.RetryBackoff"/> (mientras tanto no se pide, para que los nuevos sigan siendo
/// alcanzables) y, tras <see cref="DispatcherOptions.MaxAttempts"/> fallos, pasa a <c>Ignored</c>.</item>
/// </list>
/// </summary>
internal sealed partial class Dispatcher(
    IInstanceHealth health,
    EventDispatchSignal signal,
    IAcceptedEventSource events,
    IRunDispatchHandler handler,
    IRunOrchestrator orchestrator,
    StuckRunRecovery recovery,
    ISecretRedactor redactor,
    DispatcherOptions options,
    TimeProvider timeProvider,
    ILogger<Dispatcher> logger) : BackgroundService
{
    public const string UndispatchableReasonPrefix = "no se pudo despachar: ";

    private const int BatchSize = 50;

    private readonly Dictionary<Guid, (int Failures, DateTimeOffset RetryAfter)> _failures = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await health.WhenReadyAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        LogStarted();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ResumeReceivedRunsAsync(stoppingToken).ConfigureAwait(false);
                break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogLoopFailed(ex);
                await Task.Delay(options.PollInterval, timeProvider, stoppingToken).ConfigureAwait(false);
            }
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DispatchPendingAsync(stoppingToken).ConfigureAwait(false);
                await signal.WaitAsync(options.PollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Fallo al leer los pendientes (p. ej. base de datos ocupada): se reintenta en la siguiente vuelta.
                LogLoopFailed(ex);
                await Task.Delay(options.PollInterval, timeProvider, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Ejecuciones creadas pero nunca orquestadas (el proceso cayó entre ambos pasos).</summary>
    private async Task ResumeReceivedRunsAsync(CancellationToken cancellationToken)
    {
        foreach (var runId in await recovery.GetReceivedRunIdsAsync(cancellationToken).ConfigureAwait(false))
        {
            LogResuming(runId);
            await OrchestrateAsync(runId, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task DispatchPendingAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var waiting = _failures.Where(f => f.Value.RetryAfter > now).Select(f => f.Key).ToList();
        var pending = await events.GetPendingAsync(BatchSize, waiting, cancellationToken).ConfigureAwait(false);
        foreach (var item in pending)
        {
            Guid? runId;
            try
            {
                var result = await handler.DispatchAsync(item, cancellationToken).ConfigureAwait(false);
                _failures.Remove(item.EventId);
                if (result.Outcome == DispatchOutcome.ObjectBusy)
                {
                    LogObjectBusy(item.EventId, item.ObjectKey);
                    continue;
                }

                runId = result.RunId;
                if (result.Outcome == DispatchOutcome.Created)
                {
                    LogRunCreated(runId!.Value, item.EventId, item.ObjectKey);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                await RecordFailureAsync(item.EventId, ex, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (runId is { } id)
            {
                await OrchestrateAsync(id, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Orquesta la Ejecución; si el orquestador lanza, la cierra como <c>Failed(StageError)</c>.</summary>
    private async Task OrchestrateAsync(Guid runId, CancellationToken cancellationToken)
    {
        try
        {
            await orchestrator.RunAsync(runId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogOrchestrationFailed(ex, runId);
            try
            {
                await recovery.FailIfActiveAsync(runId, ex, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception inner) when (inner is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // Se volverá a intentar en el siguiente arranque (sigue en Received) o en la reconciliación (Épica 5).
                LogRecoveryFailed(inner, runId);
            }
        }
    }

    private async Task RecordFailureAsync(Guid eventId, Exception error, CancellationToken cancellationToken)
    {
        var failures = (_failures.TryGetValue(eventId, out var previous) ? previous.Failures : 0) + 1;
        LogDispatchFailed(error, eventId, failures, options.MaxAttempts);
        if (failures >= options.MaxAttempts)
        {
            try
            {
                var reason = UndispatchableReasonPrefix + redactor.Redact(StuckRunRecovery.ShortReason(error));
                await events.MarkIgnoredAsync(eventId, reason, cancellationToken).ConfigureAwait(false);
                _failures.Remove(eventId);
                LogGaveUp(eventId, failures);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                LogDispatchFailed(ex, eventId, failures, options.MaxAttempts);
            }
        }

        _failures[eventId] = (failures, timeProvider.GetUtcNow() + options.RetryBackoff);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Dispatcher en marcha: los Eventos aceptados se convierten en Ejecuciones.")]
    private partial void LogStarted();

    [LoggerMessage(Level = LogLevel.Warning, Message = "La Ejecución {RunId} quedó en Received sin orquestar; se reanuda.")]
    private partial void LogResuming(Guid runId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Ejecución {RunId} creada para el Evento {EventId} ({ObjectKey}).")]
    private partial void LogRunCreated(Guid runId, Guid eventId, string objectKey);

    [LoggerMessage(Level = LogLevel.Information, Message = "Evento {EventId} ignorado: ya hay una Ejecución en curso para {ObjectKey}.")]
    private partial void LogObjectBusy(Guid eventId, string objectKey);

    [LoggerMessage(Level = LogLevel.Error, Message = "No se ha podido despachar el Evento {EventId} (intento {Attempt} de {MaxAttempts}).")]
    private partial void LogDispatchFailed(Exception exception, Guid eventId, int attempt, int maxAttempts);

    [LoggerMessage(Level = LogLevel.Warning, Message = "El Evento {EventId} pasa a Ignored tras {Attempts} intentos fallidos.")]
    private partial void LogGaveUp(Guid eventId, int attempts);

    [LoggerMessage(Level = LogLevel.Error, Message = "La orquestación de la Ejecución {RunId} ha fallado; se cierra como Failed(StageError).")]
    private partial void LogOrchestrationFailed(Exception exception, Guid runId);

    [LoggerMessage(Level = LogLevel.Error, Message = "No se ha podido cerrar la Ejecución {RunId} tras el fallo del orquestador.")]
    private partial void LogRecoveryFailed(Exception exception, Guid runId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Fallo en el bucle del Dispatcher; se reintenta.")]
    private partial void LogLoopFailed(Exception exception);
}
