using System.Text.Json;
using CodeNetix.Contracts;
using CodeNetix.Ingestion.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CodeNetix.Ingestion.Features.SubmitManualEvent;

/// <summary>
/// Entrada manual (FR-5, FR-26, AD-4, AD-5): normaliza la URL a un <see cref="EventEnvelope"/> con
/// <see cref="EventSource.Manual"/>, autoriza al actor y lo persiste en <c>ing_events</c> antes de responder.
/// Orden: parsear → buscar si ya existe (un reintento no vuelve a llamar a GitHub) → identidad del token de
/// la instancia → permiso de escritura → persistir. Sin permiso, el Evento queda <see cref="EventStatus.Ignored"/>
/// con <see cref="UnauthorizedReason"/>; si GitHub no responde, no se persiste nada (falla cerrado).
/// La deduplicación por <c>manual:&lt;url-canónica&gt;:&lt;requestId&gt;</c> la garantiza el índice único: si
/// una inserción concurrente choca, se devuelve el Evento existente. Nunca crea Ejecuciones ni usa el token:
/// tras persistir un <c>Accepted</c> solo avisa al Dispatcher con <see cref="IEventDispatchSignal"/>.
/// </summary>
internal sealed class ManualEventIngestion(
    IDbContextFactory<IngestionDbContext> contextFactory,
    IGitHubIdentity identity,
    IRepositoryPermissions permissions,
    IEventDispatchSignal dispatchSignal,
    TimeProvider timeProvider,
    ManualSentryEventIngestion? sentry = null) : IManualEventIngestion
{
    public const string UnauthorizedReason = "no autorizado";

    private const int SqliteConstraint = 19;
    private const int SqliteConstraintUnique = 2067;

    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web);

    public async Task<ManualEventResult> SubmitAsync(string url, Guid requestId, CancellationToken cancellationToken)
    {
        if (requestId == Guid.Empty)
        {
            throw new ArgumentException("requestId no puede ser Guid.Empty.", nameof(requestId));
        }

        var parsed = ManualUrlParser.Parse(url);
        switch (parsed.Kind)
        {
            case ManualUrlKind.Sentry:
                // Historia 7.4: incidencia de Sentry → Repositorio objetivo, issue de seguimiento y Evento.
                return sentry is null
                    ? ManualEventResult.Unprocessable(CodeNetix.Ingestion.Features.ReadSentryContext.SentryApiOptions.NotConfiguredReason)
                    : await sentry.SubmitAsync(parsed, requestId, cancellationToken).ConfigureAwait(false);
            case ManualUrlKind.Unsupported:
                return ManualEventResult.Rejected(ManualUrlParser.UnsupportedMessage);
        }

        var canonicalUrl = parsed.CanonicalUrl!;
        var owner = parsed.Owner!;
        var repo = parsed.Repo!;
        var repository = $"{owner}/{repo}";
        var dedupKey = DedupKey(canonicalUrl, requestId);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var existing = await FindAsync(context, dedupKey, repository, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return ManualEventResult.Existing(existing);
        }

        // Autorización (FR-26): el actor del Evento manual es la identidad del token de la instancia.
        string actor;
        try
        {
            actor = await identity.GetLoginAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (GitHubUnavailableException ex)
        {
            return ManualEventResult.Unavailable(ex.Message);
        }

        var access = await permissions.GetWriteAccessAsync(owner, repo, actor, cancellationToken).ConfigureAwait(false);
        if (access.Status == WriteAccessStatus.Unavailable)
        {
            return ManualEventResult.Unavailable(access.Reason ?? "GitHub no responde");
        }

        var granted = access.Status == WriteAccessStatus.Granted;
        var envelope = new EventEnvelope(
            Id: Guid.CreateVersion7(),
            Source: EventSource.Manual,
            ObjectKey: parsed.ObjectKey!,
            DedupKey: dedupKey,
            Actor: actor,
            Payload: JsonSerializer.Serialize(new ManualEventPayload(canonicalUrl, requestId), PayloadJson),
            Status: granted ? EventStatus.Accepted : EventStatus.Ignored,
            IgnoredReason: granted ? null : UnauthorizedReason,
            ReceivedAt: timeProvider.GetUtcNow());

        context.Events.Add(EventRecord.FromEnvelope(envelope));
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (granted)
            {
                // El Dispatcher (Pipeline) lo convierte en Ejecución; ningún endpoint crea Ejecuciones.
                dispatchSignal.Notify();
            }

            return ManualEventResult.Created(new ManualEventInfo(envelope.Id, envelope.Status, envelope.Actor, repository, envelope.IgnoredReason));
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Otra petición con el mismo requestId ganó la carrera: se devuelve su Evento.
            context.ChangeTracker.Clear();
            var winner = await FindAsync(context, dedupKey, repository, cancellationToken).ConfigureAwait(false);
            if (winner is not null)
            {
                return ManualEventResult.Existing(winner);
            }

            throw;
        }
    }

    /// <summary><c>manual:&lt;url-canónica&gt;:&lt;requestId&gt;</c> con el GUID en minúsculas y guiones.</summary>
    internal static string DedupKey(string canonicalUrl, Guid requestId) =>
        $"manual:{canonicalUrl}:{requestId:D}";

    private static async Task<ManualEventInfo?> FindAsync(
        IngestionDbContext context,
        string dedupKey,
        string repository,
        CancellationToken cancellationToken)
    {
        var row = await context.Events
            .AsNoTracking()
            .Where(e => e.DedupKey == dedupKey)
            .Select(e => new { e.Id, e.Status, e.Actor, e.IgnoredReason })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return row is null ? null : new ManualEventInfo(row.Id, row.Status, row.Actor, repository, row.IgnoredReason);
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is SqliteException { SqliteErrorCode: SqliteConstraint, SqliteExtendedErrorCode: SqliteConstraintUnique };

    /// <summary>Lo único que se guarda del Evento manual: datos, nunca el token ni otros secretos.</summary>
    private sealed record ManualEventPayload(string Url, Guid RequestId);
}
