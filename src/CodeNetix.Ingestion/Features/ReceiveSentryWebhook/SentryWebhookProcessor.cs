using System.Collections.Concurrent;
using CodeNetix.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeNetix.Ingestion.Features.ReceiveSentryWebhook;

/// <summary>
/// Procesado asíncrono de los webhooks de Sentry (FR-7, FR-8, AD-4), solo en <c>serve</c>. Espera a <c>Ready</c> y procesa
/// las entregas <b>de una en una</b> (dos entregas del mismo error nunca crean dos issues de seguimiento). Por intento:
/// clave (<see cref="SentryEventRecorder.ResolveAsync"/>; ya registrada → nada), issue de seguimiento (la de un Evento
/// anterior de la incidencia en el mismo repositorio o <see cref="ISentryTrackingIssues.EnsureAsync"/>) y Evento
/// <c>Accepted</c> con el objeto <c>github:&lt;owner&gt;/&lt;repo&gt;#&lt;n&gt;</c>. Si GitHub no responde, la entrega se vuelve a
/// encolar tras la siguiente espera de <see cref="SentryWebhookOptions.RetryDelays"/> (ante un rate limit, la mayor entre
/// esa espera y el <c>Retry-After</c> de GitHub, con el tope <see cref="SentryWebhookOptions.MaxRetryAfter"/>). Si al final
/// se abandona (intentos agotados, cola llena o fallo inesperado), deja un Evento <c>Ignored</c> "no procesada" visible en
/// el Panel que no consume la clave de la incidencia. Un rechazo definitivo de GitHub (repositorio inexistente, sin
/// permiso) deja un Evento <c>Ignored</c> con el motivo. Nunca crea Ejecuciones.
/// </summary>
internal sealed partial class SentryWebhookProcessor(
    IOptions<SentryWebhookOptions> options,
    SentryWebhookIngestion ingestion,
    SentryEventRecorder recorder,
    ISentryTrackingIssues trackingIssues,
    SentryIssueLocks locks,
    IInstanceHealth health,
    TimeProvider timeProvider,
    ILogger<SentryWebhookProcessor> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<Task, byte> _waiting = new();
    private int _completed;

    /// <summary>Entregas terminadas (con Evento, ya existente o abandonadas) desde el arranque.</summary>
    public int CompletedDeliveries => Volatile.Read(ref _completed);

    public static string UnprocessedReason(string reason) =>
        $"no procesada ({reason}); se procesará si Sentry vuelve a enviarla";

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

        try
        {
            await foreach (var incident in ingestion.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                await ProcessAttemptAsync(incident, settings, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Parada: lo que quede en cola se pierde (en memoria).
        }
        finally
        {
            await Task.WhenAll(_waiting.Keys).ConfigureAwait(false);
        }
    }

    private async Task ProcessAttemptAsync(SentryIncident incident, SentryWebhookOptions settings, CancellationToken cancellationToken)
    {
        var delivery = incident.DeliveryId ?? "-";
        try
        {
            var outcome = await TryOnceAsync(incident, cancellationToken).ConfigureAwait(false);
            if (outcome.Done)
            {
                Finish(incident);
                return;
            }

            if (incident.Attempt >= settings.RetryDelays.Count)
            {
                await GiveUpAsync(incident, incident.Attempt + 1, outcome.Reason!, cancellationToken).ConfigureAwait(false);
                return;
            }

            var delay = settings.RetryDelays[incident.Attempt];
            if (outcome.RateLimited)
            {
                // Rate limit: se respeta el Retry-After de GitHub, acotado.
                var wanted = outcome.RetryAfter is { } retryAfter && retryAfter > delay ? retryAfter : delay;
                delay = wanted > settings.MaxRetryAfter ? settings.MaxRetryAfter : wanted;
            }

            LogRetrying(delivery, incident.IssueId, outcome.Reason!, delay);
            Track(RequeueAfterAsync(incident with { Attempt = incident.Attempt + 1 }, delay, cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Finish(incident);
        }
        catch (Exception ex)
        {
            LogFailed(ex, delivery, incident.IssueId);
            await GiveUpAsync(incident, incident.Attempt + 1, "fallo inesperado", cancellationToken).ConfigureAwait(false);
        }
    }

    private void Track(Task task)
    {
        _waiting[task] = 0;
        _ = task.ContinueWith(t => _waiting.TryRemove(t, out _), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task RequeueAfterAsync(SentryIncident incident, TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, timeProvider, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Finish(incident);
            return;
        }

        if (!ingestion.TryRequeue(incident))
        {
            await GiveUpAsync(incident, incident.Attempt, "la cola de webhooks está llena", cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Abandona la entrega dejando un Evento "no procesada" visible en el Panel (nunca lanza).</summary>
    private async Task GiveUpAsync(SentryIncident incident, int attempts, string reason, CancellationToken cancellationToken)
    {
        var delivery = incident.DeliveryId ?? "-";
        LogGaveUp(delivery, incident.IssueId, attempts, reason);
        try
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                var recorded = await recorder.RecordUnprocessedAsync(incident, UnprocessedReason(reason), cancellationToken).ConfigureAwait(false);
                LogIgnored(delivery, recorded.EventId, incident.IssueId, UnprocessedReason(reason));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogFailed(ex, delivery, incident.IssueId);
        }
        catch (OperationCanceledException)
        {
            // Parada.
        }
        finally
        {
            Finish(incident);
        }
    }

    private void Finish(SentryIncident incident)
    {
        ingestion.Complete(incident);
        Interlocked.Increment(ref _completed);
    }

    private async Task<(bool Done, string? Reason, bool RateLimited, TimeSpan? RetryAfter)> TryOnceAsync(SentryIncident incident, CancellationToken cancellationToken)
    {
        var delivery = incident.DeliveryId ?? "-";
        var key = await recorder.ResolveAsync(incident, cancellationToken).ConfigureAwait(false);
        if (key.ExistingEventId is { } existing)
        {
            LogExisting(delivery, incident.IssueId, key.DedupKey, existing);
            return (true, null, false, null);
        }

        if (!options.Value.Projects.TryGetValue(incident.ProjectSlug, out var target))
        {
            // La correspondencia no cambia en caliente; por si acaso, se registra como sin correspondencia.
            var reason = SentryEventRecorder.UnmappedReason(incident.ProjectSlug);
            var unmapped = await recorder.RecordAsync(incident, null, EventStatus.Ignored, reason, cancellationToken).ConfigureAwait(false);
            LogIgnored(delivery, unmapped.EventId, incident.IssueId, reason);
            return (true, null, false, null);
        }

        // Buscar o asegurar la issue de seguimiento y registrar el Evento, bajo el cerrojo de la incidencia compartido con
        // fix <url-de-sentry> (historia 7.4): dentro se vuelve a mirar si otro (p. ej. un fix) ya la ha registrado.
        using var _ = await locks.AcquireAsync(incident.IssueId, cancellationToken).ConfigureAwait(false);

        long number;
        var previous = key.TrackingIssueIn(target.Owner, target.Repo)
            ?? await recorder.FindTrackingIssueAsync(incident.IssueId, target.Owner, target.Repo, cancellationToken).ConfigureAwait(false);
        if (previous is { } found)
        {
            number = found;
        }
        else
        {
            var ensured = await trackingIssues.EnsureAsync(
                new SentryTrackingIssueRequest(
                    target.Owner,
                    target.Repo,
                    incident.IssueId,
                    incident.ShortId,
                    incident.Title,
                    incident.Culprit,
                    incident.Level,
                    incident.ProjectSlug,
                    incident.Permalink),
                cancellationToken).ConfigureAwait(false);

            switch (ensured.Status)
            {
                case SentryTrackingIssueStatus.Unavailable:
                    return (false, ensured.Failure!.Reason, ensured.Failure.RateLimited, ensured.Failure.RetryAfter);
                case SentryTrackingIssueStatus.Rejected:
                    var reason = $"no se ha podido crear la issue de seguimiento en {target.FullName}: {ensured.Failure!.Reason}";
                    var rejected = await recorder.RecordAsync(incident, null, EventStatus.Ignored, reason, cancellationToken).ConfigureAwait(false);
                    LogIgnored(delivery, rejected.EventId, incident.IssueId, reason);
                    return (true, null, false, null);
                case SentryTrackingIssueStatus.Created:
                    LogTrackingIssueCreated(delivery, incident.IssueId, target.FullName, ensured.Number!.Value);
                    break;
            }

            number = ensured.Number!.Value;
        }

        var result = await recorder.RecordAsync(incident, (target, number), EventStatus.Accepted, null, cancellationToken).ConfigureAwait(false);
        if (result.Status == SentryRecordStatus.Existing)
        {
            LogExisting(delivery, incident.IssueId, result.DedupKey, result.EventId);
        }
        else
        {
            LogAccepted(delivery, result.EventId, result.DedupKey, target.FullName, number);
        }

        return (true, null, false, null);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook de Sentry: entrega {DeliveryId} → Evento {EventId} aceptado ({DedupKey}) sobre {Repository}#{Number}.")]
    private partial void LogAccepted(string deliveryId, Guid eventId, string dedupKey, string repository, long number);

    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook de Sentry: entrega {DeliveryId} → Evento {EventId} ignorado para la incidencia {IssueId}: {Reason}.")]
    private partial void LogIgnored(string deliveryId, Guid eventId, string issueId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook de Sentry: entrega {DeliveryId} de la incidencia {IssueId}: {DedupKey} ya estaba registrado (Evento {EventId}).")]
    private partial void LogExisting(string deliveryId, string issueId, string dedupKey, Guid eventId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook de Sentry: entrega {DeliveryId}: issue de seguimiento de la incidencia {IssueId} creada en {Repository}#{Number}.")]
    private partial void LogTrackingIssueCreated(string deliveryId, string issueId, string repository, long number);

    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook de Sentry: entrega {DeliveryId} de la incidencia {IssueId} se reintenta en {Delay}: {Reason}.")]
    private partial void LogRetrying(string deliveryId, string issueId, string reason, TimeSpan delay);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Webhook de Sentry: entrega {DeliveryId} de la incidencia {IssueId} abandonada tras {Attempts} intento(s) ({Reason}); queda como Evento ignorado «no procesada».")]
    private partial void LogGaveUp(string deliveryId, string issueId, int attempts, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Webhook de Sentry: fallo inesperado con la entrega {DeliveryId} de la incidencia {IssueId}.")]
    private partial void LogFailed(Exception exception, string deliveryId, string issueId);
}
