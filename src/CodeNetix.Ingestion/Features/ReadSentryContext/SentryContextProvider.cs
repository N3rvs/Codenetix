using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeNetix.Contracts;
using CodeNetix.Ingestion.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeNetix.Ingestion.Features.ReadSentryContext;

/// <summary>
/// Contexto de Sentry del Evento de una Ejecución (historia 7.4, FR-5, AD-5). Un Evento es de Sentry si es de la Fuente
/// <c>Sentry</c> (webhook, 7.3) o un Evento manual creado desde una URL de Sentry, y su payload guarda
/// <c>sentryIssueId</c>. Lee la incidencia (frecuencia) y su último evento con <see cref="SentryApiClient"/>, con un plazo
/// total de <see cref="SentryApiOptions.TotalTimeout"/>, y los convierte con <see cref="SentryContextBuilder"/>. Nada se
/// persiste ni se cachea: el orquestador lo pide una vez por Ejecución. Cualquier fallo de Sentry es
/// <see cref="SentryContextStatus.Unavailable"/> con el motivo (el análisis sigue sin contexto).
/// </summary>
internal sealed partial class SentryContextProvider(
    IDbContextFactory<IngestionDbContext> contextFactory,
    SentryApiClient client,
    IOptions<SentryApiOptions> options,
    ISecretRedactor redactor,
    ILogger<SentryContextProvider> logger) : ISentryContextProvider
{
    public async Task<SentryContextResult> GetForEventAsync(Guid eventId, CancellationToken cancellationToken)
    {
        string? payload;
        EventSource source;
        await using (var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            var row = await context.Events.AsNoTracking()
                .Where(e => e.Id == eventId)
                .Select(e => new { e.Source, e.PayloadJson })
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            if (row is null)
            {
                return SentryContextResult.NotApplicable;
            }

            (source, payload) = (row.Source, row.PayloadJson);
        }

        if (source is not (EventSource.Sentry or EventSource.Manual) || SentryIssueIdOf(payload) is not { } issueId)
        {
            return SentryContextResult.NotApplicable;
        }

        var settings = options.Value;
        if (!settings.Configured)
        {
            return SentryContextResult.Unavailable(issueId, SentryApiOptions.NotConfiguredReason);
        }

        using var total = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        total.CancelAfter(settings.TotalTimeout);
        try
        {
            var issue = await client.GetIssueAsync(issueId, total.Token).ConfigureAwait(false);
            if (!issue.Ok)
            {
                return Unavailable(issueId, issue.Reason!);
            }

            var latest = await client.GetLatestEventAsync(issueId, total.Token).ConfigureAwait(false);
            if (!latest.Ok)
            {
                return Unavailable(issueId, latest.Reason!);
            }

            var built = SentryContextBuilder.Build(SentryIssueInfo.Parse(issueId, issue.Body!.Value, redactor), latest.Body!.Value, redactor);
            LogAttached(issueId, built.Frames, built.Breadcrumbs, built.Text.Length);
            return new SentryContextResult(
                SentryContextStatus.Attached,
                issueId,
                UntrustedText.From(built.Text),
                built.Frames,
                built.Breadcrumbs,
                built.Text.Length,
                built.Truncated);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Unavailable(issueId, string.Create(CultureInfo.InvariantCulture, $"{SentryApiClient.NotRespondingReason} (sin respuesta en {settings.TotalTimeout.TotalSeconds:0} s)"));
        }
    }

    /// <summary><c>sentryIssueId</c> numérico del payload, o <c>null</c>.</summary>
    internal static string? SentryIssueIdOf(string? payloadJson)
    {
        if (string.IsNullOrEmpty(payloadJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var id = SentryText.String(document.RootElement, "sentryIssueId");
            return id is not null && IssueIdPattern().IsMatch(id) ? id : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private SentryContextResult Unavailable(string issueId, string reason)
    {
        LogUnavailable(issueId, reason);
        return SentryContextResult.Unavailable(issueId, reason);
    }

    [GeneratedRegex("^[1-9][0-9]{0,19}$", RegexOptions.CultureInvariant)]
    private static partial Regex IssueIdPattern();

    [LoggerMessage(Level = LogLevel.Information, Message = "Contexto de Sentry de la incidencia {IssueId}: {Frames} marco(s), {Breadcrumbs} miga(s), {Characters} caracteres.")]
    private partial void LogAttached(string issueId, int frames, int breadcrumbs, int characters);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Contexto de Sentry de la incidencia {IssueId} no disponible: {Reason}. El análisis sigue sin él.")]
    private partial void LogUnavailable(string issueId, string reason);
}
