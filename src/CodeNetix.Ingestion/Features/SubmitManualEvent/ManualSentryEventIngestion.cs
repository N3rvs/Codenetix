using System.Text.Json;
using System.Text.Json.Serialization;
using CodeNetix.Contracts;
using CodeNetix.Ingestion.Data;
using CodeNetix.Ingestion.Features.ReadSentryContext;
using CodeNetix.Ingestion.Features.ReceiveSentryWebhook;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeNetix.Ingestion.Features.SubmitManualEvent;

/// <summary>
/// <c>codenetix fix &lt;url-de-sentry&gt;</c> (historia 7.4, FR-5, AD-4). Orden: Sentry configurado → organización de la URL
/// = la configurada → Evento ya existente (mismo <c>requestId</c>, sin E/S) → incidencia en la API de Sentry (solo
/// <c>BaseUrl</c>, nunca el host de la URL) → proyecto → Repositorio objetivo (solo <c>CODENETIX__Sentry__Projects</c>) →
/// identidad del token de GitHub y permiso de escritura (equivalente de FR-26) → issue de seguimiento (la de un Evento
/// anterior o <see cref="ISentryTrackingIssues.EnsureAsync"/>) → Evento <c>Accepted</c> <c>Source = Manual</c> sobre
/// <c>github:&lt;owner&gt;/&lt;repo&gt;#&lt;n&gt;</c> con la clave <c>manual:&lt;url-canónica&gt;:&lt;requestId&gt;</c>. Sin permiso,
/// Evento <c>Ignored</c> "no autorizado" sobre <c>sentry:&lt;id&gt;</c> sin crear la issue. En cualquier error no se persiste nada.
/// </summary>
internal sealed partial class ManualSentryEventIngestion(
    IDbContextFactory<IngestionDbContext> contextFactory,
    SentryApiClient sentry,
    IOptions<SentryApiOptions> sentryOptions,
    IOptions<SentryWebhookOptions> projects,
    SentryEventRecorder recorder,
    IGitHubIdentity identity,
    IRepositoryPermissions permissions,
    ISentryTrackingIssues trackingIssues,
    IEventDispatchSignal dispatchSignal,
    TimeProvider timeProvider,
    SentryIssueLocks locks,
    ISecretRedactor redactor,
    ILogger<ManualSentryEventIngestion> logger)
{
    private const int SqliteConstraint = 19;
    private const int SqliteConstraintUnique = 2067;

    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public const string InvalidProjectReason = "la incidencia de Sentry no trae un proyecto válido";

    public static string OtherOrganizationReason(string organization) =>
        $"la URL es de la organización de Sentry '{organization}', pero esta instancia está configurada para otra (CODENETIX__Sentry__Organization)";

    public async Task<ManualEventResult> SubmitAsync(ManualUrl parsed, Guid requestId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        if (parsed.SentryIssueId is not { } issueId)
        {
            return ManualEventResult.Rejected(ManualUrlParser.MalformedSentryMessage);
        }

        var settings = sentryOptions.Value;
        if (!settings.Configured)
        {
            return ManualEventResult.Unprocessable(SentryApiOptions.NotConfiguredReason);
        }

        var organization = settings.Organization!;
        if (parsed.SentryOrganization is { } urlOrganization && !string.Equals(urlOrganization, organization, StringComparison.Ordinal))
        {
            return ManualEventResult.Unprocessable(OtherOrganizationReason(urlOrganization));
        }

        var canonicalUrl = $"{settings.NormalizedBaseUrl}/organizations/{organization}/issues/{issueId}/";
        var dedupKey = ManualEventIngestion.DedupKey(canonicalUrl, requestId);

        if (await FindAsync(dedupKey, cancellationToken).ConfigureAwait(false) is { } existing)
        {
            return ManualEventResult.Existing(existing);
        }

        var response = await sentry.GetIssueAsync(issueId, cancellationToken).ConfigureAwait(false);
        switch (response.Failure)
        {
            case SentryApiFailure.None:
                break;
            case SentryApiFailure.NotConfigured or SentryApiFailure.NotFound or SentryApiFailure.Unauthorized:
                return ManualEventResult.Unprocessable(response.Reason!);
            default:
                return ManualEventResult.SentryUnavailable(response.Reason!);
        }

        var issue = SentryIssueInfo.Parse(issueId, response.Body!.Value, redactor);
        if (issue.ProjectSlug is not { } slug)
        {
            return ManualEventResult.Unprocessable(InvalidProjectReason);
        }

        if (!projects.Value.Projects.TryGetValue(slug, out var target))
        {
            return ManualEventResult.Unprocessable(SentryEventRecorder.UnmappedReason(slug));
        }

        // Equivalente de FR-26: no hay actor de GitHub que etiquete; el actor es la identidad del token de la instancia,
        // que debe poder escribir en el Repositorio objetivo (como la entrada manual de GitHub).
        string actor;
        try
        {
            actor = await identity.GetLoginAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (GitHubUnavailableException ex)
        {
            return ManualEventResult.Unavailable(ex.Message);
        }

        var access = await permissions.GetWriteAccessAsync(target.Owner, target.Repo, actor, cancellationToken).ConfigureAwait(false);
        if (access.Status == WriteAccessStatus.Unavailable)
        {
            return ManualEventResult.Unavailable(access.Reason ?? "GitHub no responde");
        }

        if (access.Status != WriteAccessStatus.Granted)
        {
            return await InsertAsync(
                dedupKey,
                SentryEventKey.Format(issueId),
                actor,
                new ManualSentryPayload(canonicalUrl, requestId, issueId, issue.ShortId, slug, target.FullName, null),
                target.FullName,
                EventStatus.Ignored,
                ManualEventIngestion.UnauthorizedReason,
                cancellationToken).ConfigureAwait(false);
        }

        // Buscar o asegurar la issue de seguimiento y registrar el Evento, bajo el cerrojo de la incidencia que comparte con
        // el procesador del webhook: ninguna combinación de fix y entregas crea dos issues de seguimiento.
        using (await locks.AcquireAsync(issueId, cancellationToken).ConfigureAwait(false))
        {
            if (await FindAsync(dedupKey, cancellationToken).ConfigureAwait(false) is { } winner)
            {
                return ManualEventResult.Existing(winner);
            }

            long number;
            if (await recorder.FindTrackingIssueAsync(issueId, target.Owner, target.Repo, cancellationToken).ConfigureAwait(false) is { } previous)
            {
                number = previous;
            }
            else
            {
                var ensured = await trackingIssues.EnsureAsync(
                    new SentryTrackingIssueRequest(target.Owner, target.Repo, issueId, issue.ShortId, issue.Title, issue.Culprit, issue.Level, slug, issue.Permalink),
                    cancellationToken).ConfigureAwait(false);
                switch (ensured.Status)
                {
                    case SentryTrackingIssueStatus.Unavailable:
                        return ManualEventResult.Unavailable(ensured.Failure!.Reason);
                    case SentryTrackingIssueStatus.Rejected:
                        return ManualEventResult.Unprocessable($"no se ha podido crear la issue de seguimiento en {target.FullName}: {ensured.Failure!.Reason}");
                    case SentryTrackingIssueStatus.Created:
                        LogTrackingIssueCreated(issueId, target.FullName, ensured.Number!.Value);
                        break;
                }

                number = ensured.Number!.Value;
            }

            return await InsertAsync(
                dedupKey,
                GitHubObjectKey.Format(target.Owner, target.Repo, number),
                actor,
                new ManualSentryPayload(canonicalUrl, requestId, issueId, issue.ShortId, slug, target.FullName, number),
                target.FullName,
                EventStatus.Accepted,
                null,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<ManualEventResult> InsertAsync(
        string dedupKey,
        string objectKey,
        string actor,
        ManualSentryPayload payload,
        string repository,
        EventStatus status,
        string? reason,
        CancellationToken cancellationToken)
    {
        var envelope = new EventEnvelope(
            Id: Guid.CreateVersion7(),
            Source: EventSource.Manual,
            ObjectKey: objectKey,
            DedupKey: dedupKey,
            Actor: actor,
            Payload: JsonSerializer.Serialize(payload, PayloadJson),
            Status: status,
            IgnoredReason: reason,
            ReceivedAt: timeProvider.GetUtcNow());

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        context.Events.Add(EventRecord.FromEnvelope(envelope));
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: SqliteConstraint, SqliteExtendedErrorCode: SqliteConstraintUnique })
        {
            if (await FindAsync(dedupKey, cancellationToken).ConfigureAwait(false) is { } winner)
            {
                return ManualEventResult.Existing(winner);
            }

            throw;
        }

        if (status == EventStatus.Accepted)
        {
            dispatchSignal.Notify();
        }

        LogRecorded(envelope.Id, payload.SentryIssueId, status.ToString(), objectKey);
        return ManualEventResult.Created(new ManualEventInfo(envelope.Id, envelope.Status, actor, repository, reason));
    }

    private async Task<ManualEventInfo?> FindAsync(string dedupKey, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await context.Events
            .AsNoTracking()
            .Where(e => e.DedupKey == dedupKey)
            .Select(e => new { e.Id, e.Status, e.Actor, e.IgnoredReason, e.PayloadJson })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        string repository;
        try
        {
            using var document = JsonDocument.Parse(row.PayloadJson);
            repository = SentryText.String(document.RootElement, "repository") ?? string.Empty;
        }
        catch (JsonException)
        {
            repository = string.Empty;
        }

        return new ManualEventInfo(row.Id, row.Status, row.Actor, repository, row.IgnoredReason);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "fix de Sentry: issue de seguimiento de la incidencia {IssueId} creada en {Repository}#{Number}.")]
    private partial void LogTrackingIssueCreated(string issueId, string repository, long number);

    [LoggerMessage(Level = LogLevel.Information, Message = "fix de Sentry: Evento {EventId} de la incidencia {IssueId} registrado como {Status} sobre {ObjectKey}.")]
    private partial void LogRecorded(Guid eventId, string issueId, string status, string objectKey);

    /// <summary>Lo único que se guarda (datos, nunca el token de Sentry ni texto de la incidencia).</summary>
    private sealed record ManualSentryPayload(
        string Url,
        Guid RequestId,
        string SentryIssueId,
        string? ShortId,
        string Project,
        string Repository,
        long? IssueNumber);
}
