using System.Collections.Concurrent;
using CodeNetix.Contracts;
using CodeNetix.Ingestion.Features.RecordLabelEvent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeNetix.Ingestion.Features.ReceiveGitHubWebhook;

/// <summary>
/// Procesado asíncrono de los webhooks de GitHub (FR-7, FR-26, AD-4), solo en <c>serve</c>. Espera a <c>Ready</c> y
/// procesa hasta <see cref="GitHubWebhookOptions.MaxConcurrency"/> entregas a la vez, un intento cada vez. Por intento: el
/// evento <c>labeled</c> más reciente (el mismo puerto que el sondeo) y, con él, <see cref="LabelEventRecorder"/> (misma
/// clave y reglas que el sondeo). Si GitHub aún no muestra el evento de este etiquetado (ninguno, uno sin fecha o uno
/// anterior al <c>updated_at</c> del payload) o no responde, la entrega se vuelve a encolar tras la siguiente espera de
/// <see cref="GitHubWebhookOptions.RetryDelays"/> (sin ocupar un hueco mientras espera); agotadas, ante un rate limit o con
/// la cola llena, no persiste nada y lo deja al sondeo o a una reentrega. Nunca crea Ejecuciones.
/// </summary>
internal sealed partial class GitHubWebhookProcessor(
    IOptions<GitHubWebhookOptions> options,
    GitHubWebhookIngestion ingestion,
    ILabeledIssueSource source,
    LabelEventRecorder recorder,
    IInstanceHealth health,
    TimeProvider timeProvider,
    ILogger<GitHubWebhookProcessor> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<Task, byte> _running = new();
    private int _completed;

    /// <summary>Entregas terminadas (con Evento, ya existente o descartadas) desde el arranque.</summary>
    public int CompletedDeliveries => Volatile.Read(ref _completed);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            return;
        }

        try
        {
            await health.WhenReadyAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var slots = new SemaphoreSlim(Math.Max(1, settings.MaxConcurrency));
        try
        {
            await foreach (var item in ingestion.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                await slots.WaitAsync(stoppingToken).ConfigureAwait(false);
                Track(Task.Run(
                    async () =>
                    {
                        try
                        {
                            await ProcessAttemptAsync(item, settings, stoppingToken).ConfigureAwait(false);
                        }
                        finally
                        {
                            slots.Release();
                        }
                    },
                    CancellationToken.None));
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Parada: lo que quede en cola se pierde (en memoria); el sondeo o una reentrega lo recuperan.
        }
        finally
        {
            // Ni los intentos ni las esperas lanzan: capturan sus propias excepciones.
            await Task.WhenAll(_running.Keys).ConfigureAwait(false);
        }
    }

    private void Track(Task task)
    {
        _running[task] = 0;
        _ = task.ContinueWith(t => _running.TryRemove(t, out _), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>Un intento; si hay que reintentar, programa el siguiente y libera el hueco.</summary>
    private async Task ProcessAttemptAsync(GitHubWebhookWorkItem item, GitHubWebhookOptions settings, CancellationToken cancellationToken)
    {
        var delivery = item.DeliveryId ?? "-";
        var repository = $"{item.Owner}/{item.Repo}";
        try
        {
            var outcome = await TryOnceAsync(item, settings, cancellationToken).ConfigureAwait(false);
            if (outcome.Done)
            {
                Finish(item);
                return;
            }

            if (outcome.Stop || item.Attempt >= settings.RetryDelays.Count)
            {
                LogGaveUp(delivery, repository, item.IssueNumber, item.Attempt + 1, outcome.Reason!);
                Finish(item);
                return;
            }

            var delay = settings.RetryDelays[item.Attempt];
            LogRetrying(delivery, repository, item.IssueNumber, outcome.Reason!, delay);
            Track(RequeueAfterAsync(item with { Attempt = item.Attempt + 1 }, delay, cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Finish(item);
        }
        catch (Exception ex)
        {
            LogFailed(ex, delivery, repository, item.IssueNumber);
            Finish(item);
        }
    }

    private async Task RequeueAfterAsync(GitHubWebhookWorkItem item, TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, timeProvider, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Finish(item);
            return;
        }

        if (!ingestion.TryRequeue(item))
        {
            LogGaveUp(item.DeliveryId ?? "-", $"{item.Owner}/{item.Repo}", item.IssueNumber, item.Attempt, "la cola de webhooks está llena");
            Finish(item);
        }
    }

    private void Finish(GitHubWebhookWorkItem item)
    {
        ingestion.Complete(item);
        Interlocked.Increment(ref _completed);
    }

    private async Task<(bool Done, string? Reason, bool Stop)> TryOnceAsync(
        GitHubWebhookWorkItem item,
        GitHubWebhookOptions settings,
        CancellationToken cancellationToken)
    {
        var delivery = item.DeliveryId ?? "-";
        var repository = $"{item.Owner}/{item.Repo}";
        LabelEventLookup lookup;
        try
        {
            lookup = await source.FindLatestLabelEventAsync(item.Owner, item.Repo, item.IssueNumber, item.Label, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogFailed(ex, delivery, repository, item.IssueNumber);
            return (false, "fallo inesperado al leer los eventos de la issue", false);
        }

        switch (lookup.Status)
        {
            case LabelEventLookupStatus.Unavailable:
                return (false, lookup.Failure!.Reason, lookup.Failure.RateLimited);
            case LabelEventLookupStatus.NotFound when lookup.IssueMissing:
                // 404/410: la issue o el repositorio no existen para el token. Es definitivo: no se reintenta.
                return (false, "GitHub no encuentra la issue o el token no puede ver el repositorio", true);
            case LabelEventLookupStatus.NotFound:
                return (false, "GitHub aún no muestra el evento labeled", false);
        }

        var labelEvent = lookup.Event!;

        // Frescura: el evento debe ser el de este etiquetado. Las dos fechas son de GitHub (sin reloj local), así que el
        // margen es pequeño: un reetiquetado rápido no debe resolverse al etiquetado anterior (ya registrado → perdido).
        var freshnessChecked = item.IssueUpdatedAt is not null;
        if (item.IssueUpdatedAt is { } updatedAt)
        {
            if (labelEvent.CreatedAt is not { } createdAt)
            {
                return (false, "el evento labeled aún no trae fecha", false);
            }

            if (createdAt < updatedAt - settings.FreshnessTolerance)
            {
                return (false, "GitHub aún no muestra el evento labeled de esta entrega", false);
            }
        }

        if (labelEvent.Actor is { } actor && !string.Equals(actor.Trim(), item.Sender, StringComparison.OrdinalIgnoreCase))
        {
            // Otro etiquetado posterior (quitar y volver a poner) sustituye a este: se registra ese, autorizando a su autor
            // (FR-26 sobre quien de verdad etiquetó; el sender de la entrega nunca se autoriza por sí mismo).
            LogSupersededSender(delivery, repository, item.IssueNumber, item.Sender);
        }

        LabelEventRecordResult result;
        try
        {
            result = await recorder.RecordAsync(item.Owner, item.Repo, item.IssueNumber, item.Label, labelEvent, item.DeliveryId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogFailed(ex, delivery, repository, item.IssueNumber);
            return (false, "fallo inesperado al registrar el Evento", false);
        }

        switch (result.Status)
        {
            case LabelEventRecordStatus.Unavailable:
                return (false, result.Reason, result.RateLimited);
            case LabelEventRecordStatus.Accepted:
                LogAccepted(delivery, result.EventId!.Value, repository, item.IssueNumber, result.Actor!);
                break;
            case LabelEventRecordStatus.Ignored:
                LogIgnored(delivery, result.EventId!.Value, repository, item.IssueNumber, result.Reason!);
                break;
            default:
                if (freshnessChecked)
                {
                    LogExisting(delivery, repository, item.IssueNumber, labelEvent.Id);
                }
                else
                {
                    // Sin updated_at en el payload no se pudo comprobar que el evento sea el de esta entrega.
                    LogExistingUnchecked(delivery, repository, item.IssueNumber, labelEvent.Id);
                }

                break;
        }

        return (true, null, false);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook de GitHub: entrega {DeliveryId} → Evento {EventId} aceptado para {Repository}#{Number} (etiquetada por {Actor}).")]
    private partial void LogAccepted(string deliveryId, Guid eventId, string repository, long number, string actor);

    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook de GitHub: entrega {DeliveryId} → Evento {EventId} ignorado para {Repository}#{Number}: {Reason}.")]
    private partial void LogIgnored(string deliveryId, Guid eventId, string repository, long number, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook de GitHub: entrega {DeliveryId} para {Repository}#{Number}: el etiquetado {LabelEventId} ya estaba registrado.")]
    private partial void LogExisting(string deliveryId, string repository, long number, long labelEventId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Webhook de GitHub: entrega {DeliveryId} para {Repository}#{Number}: el etiquetado {LabelEventId} ya estaba registrado, pero el payload no trae issue.updated_at y no se ha podido comprobar que sea el de esta entrega.")]
    private partial void LogExistingUnchecked(string deliveryId, string repository, long number, long labelEventId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook de GitHub: entrega {DeliveryId} para {Repository}#{Number}: un etiquetado posterior sustituye al de {Sender}.")]
    private partial void LogSupersededSender(string deliveryId, string repository, long number, string sender);

    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook de GitHub: entrega {DeliveryId} para {Repository}#{Number} se reintenta en {Delay}: {Reason}.")]
    private partial void LogRetrying(string deliveryId, string repository, long number, string reason, TimeSpan delay);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Webhook de GitHub: entrega {DeliveryId} para {Repository}#{Number} descartada sin Evento tras {Attempts} intento(s) ({Reason}); la recuperan el sondeo o «Redeliver» en GitHub.")]
    private partial void LogGaveUp(string deliveryId, string repository, long number, int attempts, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Webhook de GitHub: fallo inesperado con la entrega {DeliveryId} para {Repository}#{Number}.")]
    private partial void LogFailed(Exception exception, string deliveryId, string repository, long number);
}
