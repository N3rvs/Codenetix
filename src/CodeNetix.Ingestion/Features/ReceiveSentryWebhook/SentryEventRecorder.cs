using System.Text.Json;
using System.Text.Json.Serialization;
using CodeNetix.Contracts;
using CodeNetix.Ingestion.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CodeNetix.Ingestion.Features.ReceiveSentryWebhook;

/// <summary>
/// Incidencia de Sentry ya filtrada (recurso <c>issue</c>, nueva o en regresión). Todo salvo <see cref="IssueId"/>,
/// <see cref="ProjectSlug"/> (validados) y <see cref="BodySha256"/> es texto no confiable.
/// </summary>
/// <param name="DeliveryId"><c>Request-ID</c> normalizado, o <c>null</c> (no está firmado: solo trazabilidad).</param>
/// <param name="BodySha256">SHA-256 (hex) del cuerpo firmado: identifica la entrega aunque cambie o falte el <c>Request-ID</c>.</param>
/// <param name="Attempt">Intentos ya hechos en el procesador (0 en la primera lectura de la cola).</param>
internal sealed record SentryIncident(
    string IssueId,
    string? ShortId,
    string Title,
    string? Culprit,
    string? Level,
    string ProjectSlug,
    string? Permalink,
    bool Regression,
    string? DeliveryId,
    string BodySha256,
    int Attempt = 0)
{
    public string Kind => Regression ? "regression" : "created";
}

/// <summary>Clave resuelta: <see cref="ExistingEventId"/> si la entrega ya está registrada; si no, la clave nueva.</summary>
/// <param name="PreviousObjectKeys">Objetos de los Eventos anteriores de la misma incidencia (para reutilizar su issue de seguimiento).</param>
internal sealed record SentryKeyResolution(string DedupKey, int? Regression, Guid? ExistingEventId, IReadOnlyList<string> PreviousObjectKeys)
{
    /// <summary>Número de la issue de seguimiento en <paramref name="owner"/>/<paramref name="repo"/> de un Evento anterior, si lo hay.</summary>
    public long? TrackingIssueIn(string owner, string repo)
    {
        foreach (var objectKey in PreviousObjectKeys)
        {
            if (GitHubObjectKey.TryParse(objectKey, out var o, out var r, out var number)
                && string.Equals(o, owner, StringComparison.OrdinalIgnoreCase)
                && string.Equals(r, repo, StringComparison.OrdinalIgnoreCase))
            {
                return number;
            }
        }

        return null;
    }
}

internal enum SentryRecordStatus
{
    /// <summary>La entrega ya estaba registrada (reentrega, repetición del mismo cuerpo o carrera): nada nuevo.</summary>
    Existing,

    Accepted,

    Ignored,
}

internal sealed record SentryRecordResult(SentryRecordStatus Status, Guid EventId, string DedupKey);

/// <summary>
/// Claves y persistencia de los Eventos de Sentry (AD-4): <c>sentry:&lt;issueId&gt;</c> para una incidencia nueva y
/// <c>sentry:&lt;issueId&gt;:regression:&lt;n&gt;</c> (n = 1 + la mayor ya registrada) para una regresión. Una entrega cuyo
/// cuerpo firmado (SHA-256) o <c>Request-ID</c> coincide con el de un Evento anterior de la incidencia es ese mismo Evento:
/// repetir un cuerpo capturado nunca crea otra regresión. Resolver la clave e insertar ocurre bajo un mismo cerrojo, así dos
/// regresiones simultáneas nunca reciben el mismo <c>n</c> ni se salta ninguno. Nunca crea Ejecuciones ni guarda el JSON de
/// Sentry: solo los datos que necesita la historia 7.4 (id, shortId, proyecto, permalink, título acotado).
/// </summary>
internal sealed class SentryEventRecorder(
    IDbContextFactory<IngestionDbContext> contextFactory,
    IEventDispatchSignal dispatchSignal,
    TimeProvider timeProvider) : IDisposable
{
    public const int MaxStoredTitleLength = 200;

    /// <summary>Prefijo de los Eventos "no procesados": no consumen la clave de la incidencia (una reentrega se procesa).</summary>
    public const string UnprocessedPrefix = "sentry-unprocessed:";

    private const int SqliteConstraint = 19;
    private const int SqliteConstraintUnique = 2067;

    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);

    public static string UnmappedReason(string projectSlug) =>
        $"el proyecto de Sentry '{projectSlug}' no tiene Repositorio objetivo: define CODENETIX__Sentry__Projects__{projectSlug}=owner/repo";

    /// <summary>Lectura sin cerrojo (para salir pronto y reutilizar la issue de seguimiento); la clave definitiva la fija <see cref="RecordAsync"/>.</summary>
    public async Task<SentryKeyResolution> ResolveAsync(SentryIncident incident, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(incident);

        var baseKey = SentryEventKey.Format(incident.IssueId);
        var regressionPrefix = baseKey + SentryEventKey.RegressionSegment;
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await context.Events
            .AsNoTracking()
            .Where(e => e.Source == EventSource.Sentry && (e.DedupKey == baseKey || e.DedupKey.StartsWith(regressionPrefix)))
            .OrderBy(e => e.ReceivedAt)
            .Select(e => new { e.Id, e.DedupKey, e.ObjectKey, e.PayloadJson })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var previous = rows.Select(r => r.ObjectKey).Reverse().ToList();
        var same = rows.FirstOrDefault(r =>
        {
            var (deliveryId, bodySha256) = IdentityOf(r.PayloadJson);
            return string.Equals(bodySha256, incident.BodySha256, StringComparison.Ordinal)
                || (incident.DeliveryId is not null && string.Equals(deliveryId, incident.DeliveryId, StringComparison.Ordinal));
        });
        if (same is not null)
        {
            return new SentryKeyResolution(same.DedupKey, null, same.Id, previous);
        }

        if (!incident.Regression)
        {
            var existing = rows.FirstOrDefault(r => r.DedupKey == baseKey);
            return new SentryKeyResolution(baseKey, null, existing?.Id, previous);
        }

        var last = rows.Select(r => SentryEventKey.TryParseRegression(incident.IssueId, r.DedupKey, out var n) ? n : 0).DefaultIfEmpty(0).Max();
        var next = last + 1;
        return new SentryKeyResolution(SentryEventKey.FormatRegression(incident.IssueId, next), next, null, previous);
    }

    /// <summary>
    /// Resuelve la clave y persiste el Evento de forma atómica (cerrojo del proceso + índice único); avisa al Dispatcher si es
    /// <c>Accepted</c>. Si la entrega ya estaba registrada, <c>Existing</c> con ese Evento.
    /// </summary>
    /// <param name="target">Repositorio objetivo y número de la issue de seguimiento; <c>null</c> sin correspondencia.</param>
    public async Task<SentryRecordResult> RecordAsync(
        SentryIncident incident,
        (SentryTargetRepository Repository, long IssueNumber)? target,
        EventStatus status,
        string? reason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(incident);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var key = await ResolveAsync(incident, cancellationToken).ConfigureAwait(false);
            if (key.ExistingEventId is { } existing)
            {
                return new SentryRecordResult(SentryRecordStatus.Existing, existing, key.DedupKey);
            }

            var objectKey = target is { } t
                ? GitHubObjectKey.Format(t.Repository.Owner, t.Repository.Repo, t.IssueNumber)
                : SentryEventKey.Format(incident.IssueId);
            return await InsertAsync(incident, key.DedupKey, key.Regression, objectKey, target, status, reason, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Evento <c>Ignored</c> visible en el Panel para una entrega que no se pudo procesar (GitHub no responde, rate limit, cola
    /// llena). Su clave (<c>sentry-unprocessed:&lt;id&gt;:&lt;hash&gt;</c>) no es la de la incidencia: una reentrega posterior se
    /// procesa con normalidad; la misma entrega fallida no se registra dos veces.
    /// </summary>
    public async Task<SentryRecordResult> RecordUnprocessedAsync(SentryIncident incident, string reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(incident);
        var dedupKey = $"{UnprocessedPrefix}{incident.IssueId}:{incident.BodySha256[..16]}";
        return await InsertAsync(incident, dedupKey, null, SentryEventKey.Format(incident.IssueId), null, EventStatus.Ignored, reason, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => _gate.Dispose();

    private async Task<SentryRecordResult> InsertAsync(
        SentryIncident incident,
        string dedupKey,
        int? regression,
        string objectKey,
        (SentryTargetRepository Repository, long IssueNumber)? target,
        EventStatus status,
        string? reason,
        CancellationToken cancellationToken)
    {
        var payload = new SentryEventPayload(
            incident.IssueId,
            incident.ShortId,
            incident.ProjectSlug,
            incident.Kind,
            regression,
            TextLimits.Truncate(incident.Title, MaxStoredTitleLength),
            incident.Permalink,
            target?.Repository.FullName,
            target?.IssueNumber,
            incident.DeliveryId,
            incident.BodySha256);
        var envelope = new EventEnvelope(
            Id: Guid.CreateVersion7(),
            Source: EventSource.Sentry,
            ObjectKey: objectKey,
            DedupKey: dedupKey,
            Actor: null,
            Payload: JsonSerializer.Serialize(payload, PayloadJson),
            Status: status,
            IgnoredReason: status == EventStatus.Ignored ? reason : null,
            ReceivedAt: timeProvider.GetUtcNow());

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        context.Events.Add(EventRecord.FromEnvelope(envelope));
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: SqliteConstraint, SqliteExtendedErrorCode: SqliteConstraintUnique })
        {
            context.ChangeTracker.Clear();
            var winner = await context.Events.AsNoTracking()
                .Where(e => e.DedupKey == dedupKey)
                .Select(e => (Guid?)e.Id)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            if (winner is { } id)
            {
                return new SentryRecordResult(SentryRecordStatus.Existing, id, dedupKey);
            }

            throw;
        }

        if (status == EventStatus.Accepted)
        {
            dispatchSignal.Notify();
            return new SentryRecordResult(SentryRecordStatus.Accepted, envelope.Id, dedupKey);
        }

        return new SentryRecordResult(SentryRecordStatus.Ignored, envelope.Id, dedupKey);
    }

    private static (string? DeliveryId, string? BodySha256) IdentityOf(string payloadJson)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;
            return root.ValueKind != JsonValueKind.Object
                ? (null, null)
                : (StringOf(root, "deliveryId"), StringOf(root, "bodySha256"));
        }
        catch (JsonException)
        {
            return (null, null);
        }

        static string? StringOf(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    /// <summary>Lo único que se guarda de una incidencia de Sentry (datos para la 7.4, nunca el JSON, la firma ni el secreto).</summary>
    private sealed record SentryEventPayload(
        string SentryIssueId,
        string? ShortId,
        string Project,
        string Kind,
        int? Regression,
        string Title,
        string? Permalink,
        string? Repository,
        long? IssueNumber,
        string? DeliveryId,
        string BodySha256);
}
