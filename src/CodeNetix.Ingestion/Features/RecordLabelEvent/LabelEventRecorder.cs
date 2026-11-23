using System.Text.Json;
using System.Text.Json.Serialization;
using CodeNetix.Contracts;
using CodeNetix.Ingestion.Data;
using CodeNetix.Ingestion.Features.SubmitManualEvent;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CodeNetix.Ingestion.Features.RecordLabelEvent;

internal enum LabelEventRecordStatus
{
    /// <summary>La clave ya existía (otra vía o un ciclo anterior): nada nuevo.</summary>
    Existing,

    Accepted,

    Ignored,

    /// <summary>No se pudo autorizar (GitHub no responde): nada persistido, se reintenta.</summary>
    Unavailable,
}

/// <param name="Reason">Motivo de <c>Ignored</c> o de <c>Unavailable</c>.</param>
/// <param name="RateLimited">Solo en <c>Unavailable</c>: la consulta de permiso chocó con el rate limit.</param>
internal sealed record LabelEventRecordResult(LabelEventRecordStatus Status, Guid? EventId, string? Actor, string? Reason, bool RateLimited = false);

/// <summary>
/// Registro de un etiquetado de GitHub ya resuelto (evento <c>labeled</c> de la API de eventos de la issue), común al
/// sondeo (7.1) y al webhook (7.2), que comparten clave <c>gh:…:labeled:&lt;id&gt;</c> (AD-4) y por tanto se deduplican
/// entre sí:
/// <list type="bullet">
/// <item>clave ya en <c>ing_events</c> → <see cref="LabelEventRecordStatus.Existing"/> (ni permiso);</item>
/// <item>Evento <c>Accepted</c> de otra Fuente para la misma issue recibido después de la etiqueta → <c>Ignored</c>
/// (<see cref="AlreadyLaunchedReason"/>);</item>
/// <item>permiso de escritura de quien etiquetó (FR-26): <c>Granted</c> → <c>Accepted</c> y aviso al Dispatcher;
/// <c>Denied</c> o sin actor → <c>Ignored</c> (no autorizado); <c>Unavailable</c> → nada persistido.</item>
/// </list>
/// Nunca crea Ejecuciones ni persiste el JSON de GitHub.
/// </summary>
internal sealed class LabelEventRecorder(
    IRepositoryPermissions permissions,
    IDbContextFactory<IngestionDbContext> contextFactory,
    IEventDispatchSignal dispatchSignal,
    TimeProvider timeProvider)
{
    public const string AlreadyLaunchedReason = "ya lanzada por otra Fuente después de etiquetarla";

    private const int SqliteConstraint = 19;
    private const int SqliteConstraintUnique = 2067;

    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <param name="deliveryId"><c>X-GitHub-Delivery</c> del webhook (trazabilidad); <c>null</c> desde el sondeo.</param>
    public async Task<LabelEventRecordResult> RecordAsync(
        string owner,
        string repo,
        long number,
        string label,
        LabelEvent labelEvent,
        string? deliveryId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(labelEvent);

        var dedupKey = GitHubLabelEventKey.Format(owner, repo, number, labelEvent.Id);
        var objectKey = GitHubObjectKey.Format(owner, repo, number);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        if (await context.Events.AsNoTracking().AnyAsync(e => e.DedupKey == dedupKey, cancellationToken).ConfigureAwait(false))
        {
            return new LabelEventRecordResult(LabelEventRecordStatus.Existing, null, null, null);
        }

        var actor = IsLogin(labelEvent.Actor) ? labelEvent.Actor!.Trim() : null;
        EventStatus status;
        string? reason;
        if (labelEvent.CreatedAt is { } labeledAt
            && await context.Events.AsNoTracking().AnyAsync(
                e => e.ObjectKey == objectKey && e.Source != EventSource.GitHub && e.Status == EventStatus.Accepted && e.ReceivedAt >= labeledAt,
                cancellationToken).ConfigureAwait(false))
        {
            (status, reason) = (EventStatus.Ignored, AlreadyLaunchedReason);
        }
        else if (actor is null)
        {
            (status, reason) = (EventStatus.Ignored, ManualEventIngestion.UnauthorizedReason);
        }
        else
        {
            // FR-26: el actor es quien puso la etiqueta; el token de la instancia solo se usa para preguntar.
            var access = await permissions.GetWriteAccessAsync(owner, repo, actor, cancellationToken).ConfigureAwait(false);
            if (access.Status == WriteAccessStatus.Unavailable)
            {
                return new LabelEventRecordResult(LabelEventRecordStatus.Unavailable, null, actor, access.Reason ?? "GitHub no responde", access.RateLimited);
            }

            (status, reason) = access.Status == WriteAccessStatus.Granted
                ? (EventStatus.Accepted, (string?)null)
                : (EventStatus.Ignored, ManualEventIngestion.UnauthorizedReason);
        }

        var payload = new LabelEventPayload($"{owner.ToLowerInvariant()}/{repo.ToLowerInvariant()}", number, label, labelEvent.Id, labelEvent.CreatedAt, deliveryId);
        var envelope = new EventEnvelope(
            Id: Guid.CreateVersion7(),
            Source: EventSource.GitHub,
            ObjectKey: objectKey,
            DedupKey: dedupKey,
            Actor: actor,
            Payload: JsonSerializer.Serialize(payload, PayloadJson),
            Status: status,
            IgnoredReason: reason,
            ReceivedAt: timeProvider.GetUtcNow());

        context.Events.Add(EventRecord.FromEnvelope(envelope));
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: SqliteConstraint, SqliteExtendedErrorCode: SqliteConstraintUnique })
        {
            // La otra vía (sondeo o webhook) con la misma clave ganó la carrera: ya existe.
            return new LabelEventRecordResult(LabelEventRecordStatus.Existing, null, null, null);
        }

        if (status == EventStatus.Accepted)
        {
            dispatchSignal.Notify();
            return new LabelEventRecordResult(LabelEventRecordStatus.Accepted, envelope.Id, actor, null);
        }

        return new LabelEventRecordResult(LabelEventRecordStatus.Ignored, envelope.Id, actor, reason);
    }

    /// <summary>Login de GitHub plausible: no vacío, ≤ 100 caracteres, sin caracteres de control ni <c>/</c>.</summary>
    public static bool IsLogin(string? login)
    {
        var trimmed = login?.Trim();
        return !string.IsNullOrEmpty(trimmed) && trimmed.Length <= 100 && !trimmed.Any(char.IsControl) && !trimmed.Contains('/');
    }

    /// <summary>Lo único que se guarda de un etiquetado: datos del disparo, nunca el JSON de GitHub, la firma ni el token.</summary>
    private sealed record LabelEventPayload(string Repository, long IssueNumber, string Label, long LabelEventId, DateTimeOffset? LabeledAt, string? DeliveryId);
}
