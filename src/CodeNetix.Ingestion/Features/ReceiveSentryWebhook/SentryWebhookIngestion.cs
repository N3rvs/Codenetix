using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using CodeNetix.Contracts;
using CodeNetix.Ingestion.Features.SubmitGitHubActionEvent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeNetix.Ingestion.Features.ReceiveSentryWebhook;

/// <summary>
/// Recepción síncrona del webhook de Sentry (AD-4, FR-7, FR-8): firma (<c>Sentry-Hook-Signature</c>), filtro (recurso
/// <c>issue</c>: <c>created</c> o <c>unresolved</c> por regresión), correspondencia proyecto → Repositorio objetivo
/// (solo la Configuración de instancia) y cola en memoria, sin llamar a GitHub. Un proyecto sin correspondencia se registra
/// aquí mismo como Evento <c>Ignored</c> (antes de responder); una incidencia aplicable la procesa después
/// <see cref="SentryWebhookProcessor"/>.
/// </summary>
internal sealed partial class SentryWebhookIngestion : ISentryWebhookIngestion
{
    public const string IssueResource = "issue";
    public const string CreatedAction = "created";
    public const string UnresolvedAction = "unresolved";
    public const string RegressedSubstatus = "regressed";
    public const string SentryActorType = "sentry";

    private readonly SentryWebhookOptions _options;
    private readonly SentryEventRecorder _recorder;
    private readonly ILogger<SentryWebhookIngestion> _logger;
    private readonly Channel<SentryIncident> _queue;
    private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.Ordinal);

    public SentryWebhookIngestion(IOptions<SentryWebhookOptions> options, SentryEventRecorder recorder, ILogger<SentryWebhookIngestion> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _recorder = recorder;
        _logger = logger;
        _queue = Channel.CreateBounded<SentryIncident>(new BoundedChannelOptions(Math.Max(1, _options.QueueCapacity))
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });
    }

    public bool Enabled => _options.Enabled;

    public int MaxBodyBytes => _options.MaxBodyBytes;

    internal ChannelReader<SentryIncident> Reader => _queue.Reader;

    public async Task<SentryWebhookReceipt> ReceiveAsync(SentryWebhookDelivery delivery, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        if (!_options.Enabled)
        {
            return new SentryWebhookReceipt(SentryWebhookOutcome.Disabled);
        }

        var deliveryId = NormalizeDeliveryId(delivery.RequestId);
        if (!IsValidSignature(_options.Secret!, delivery.Body.Span, delivery.Signature))
        {
            LogInvalidSignature(deliveryId ?? "-");
            return new SentryWebhookReceipt(SentryWebhookOutcome.InvalidSignature);
        }

        var mediaType = delivery.ContentType?.Split(';', 2)[0].Trim();
        if (!string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            return new SentryWebhookReceipt(SentryWebhookOutcome.UnsupportedContentType, "el webhook de Sentry debe enviar application/json.");
        }

        var parsed = Parse(delivery.Resource?.Trim() ?? string.Empty, delivery.Body, deliveryId);
        if (parsed.Receipt is { } receipt)
        {
            if (receipt.Outcome == SentryWebhookOutcome.Ignored)
            {
                LogIgnored(deliveryId ?? "-", receipt.Reason!);
            }

            return receipt;
        }

        var incident = parsed.Incident!;
        if (!_options.Projects.ContainsKey(incident.ProjectSlug))
        {
            // FR-8: sin correspondencia, Evento Ignored con el motivo visible en el Panel, persistido antes del 2xx.
            var reason = SentryEventRecorder.UnmappedReason(incident.ProjectSlug);
            var recorded = await _recorder.RecordAsync(incident, null, EventStatus.Ignored, reason, cancellationToken).ConfigureAwait(false);
            LogUnmapped(deliveryId ?? "-", incident.IssueId, incident.ProjectSlug, recorded.EventId);
            return new SentryWebhookReceipt(SentryWebhookOutcome.Unmapped, reason, recorded.EventId);
        }

        // Pendientes por cuerpo firmado (no por Request-ID, que no está firmado): repetir el mismo cuerpo con otro
        // Request-ID, o sin él, mientras sigue en cola es la misma entrega.
        if (!_pending.TryAdd(incident.BodySha256, 0))
        {
            LogDuplicate(deliveryId ?? "-", incident.IssueId);
            return new SentryWebhookReceipt(SentryWebhookOutcome.Duplicate);
        }

        if (!_queue.Writer.TryWrite(incident))
        {
            _pending.TryRemove(incident.BodySha256, out _);
            LogQueueFull(deliveryId ?? "-", incident.IssueId);
            return new SentryWebhookReceipt(SentryWebhookOutcome.QueueFull, "hay demasiadas entregas pendientes; vuelve a enviarla más tarde.");
        }

        LogQueued(deliveryId ?? "-", incident.IssueId, incident.ProjectSlug, incident.Kind);
        return new SentryWebhookReceipt(SentryWebhookOutcome.Queued);
    }

    /// <summary>La entrega ya se procesó (con éxito o no): una reentrega vuelve a encolarse.</summary>
    internal void Complete(SentryIncident incident) => _pending.TryRemove(incident.BodySha256, out _);

    /// <summary>Vuelve a encolar una entrega pendiente de reintento; <c>false</c> con la cola llena.</summary>
    internal bool TryRequeue(SentryIncident incident) => _queue.Writer.TryWrite(incident);

    /// <summary><c>Sentry-Hook-Signature</c>: hex (64) del HMAC SHA-256 del cuerpo crudo con el Client Secret.</summary>
    internal static bool IsValidSignature(string secret, ReadOnlySpan<byte> body, string? header)
    {
        var value = header?.Trim();
        return value is not null && WebhookHmac.MatchesHex(secret, body, value);
    }

    /// <summary>Firma que enviaría Sentry (para pruebas y documentación).</summary>
    internal static string ComputeSignature(string secret, ReadOnlySpan<byte> body) => WebhookHmac.ComputeHex(secret, body);

    private (SentryWebhookReceipt? Receipt, SentryIncident? Incident) Parse(string resource, ReadOnlyMemory<byte> body, string? deliveryId)
    {
        if (!string.Equals(resource, IssueResource, StringComparison.Ordinal))
        {
            return Ignored($"el recurso '{GitHubActionEventIngestion.Shown(resource)}' no dispara trabajo: solo 'issue' (incidencia nueva o en regresión).");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return Invalid("el cuerpo no es un JSON válido.");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Invalid("el cuerpo no es un objeto JSON.");
            }

            var action = GitHubActionEventIngestion.GetString(root, "action");
            if (action is not (CreatedAction or UnresolvedAction))
            {
                return Ignored($"la acción '{GitHubActionEventIngestion.Shown(action)}' no dispara trabajo: solo 'created' o 'unresolved' por regresión.");
            }

            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
                || !data.TryGetProperty("issue", out var issue) || issue.ValueKind != JsonValueKind.Object)
            {
                return Invalid("el webhook no trae data.issue.");
            }

            var issueId = issue.TryGetProperty("id", out var idElement) ? IdOf(idElement) : null;
            if (issueId is null)
            {
                return Invalid("el webhook no trae un data.issue.id numérico.");
            }

            var slug = issue.TryGetProperty("project", out var project) && project.ValueKind == JsonValueKind.Object
                ? GitHubActionEventIngestion.GetString(project, "slug")?.Trim().ToLowerInvariant()
                : null;
            if (!SentryWebhookOptions.IsProjectSlug(slug))
            {
                return Invalid("el webhook no trae un data.issue.project.slug válido.");
            }

            var regression = false;
            if (action == UnresolvedAction)
            {
                var substatus = GitHubActionEventIngestion.GetString(issue, "substatus");
                var actorType = root.TryGetProperty("actor", out var actor) && actor.ValueKind == JsonValueKind.Object
                    ? GitHubActionEventIngestion.GetString(actor, "type")
                    : null;
                // Sin substatus, solo es una regresión si la reabrió Sentry (actor.type = sentry); un usuario, una
                // aplicación o un actor ausente no lo son.
                regression = substatus is not null
                    ? string.Equals(substatus, RegressedSubstatus, StringComparison.Ordinal)
                    : string.Equals(actorType, SentryActorType, StringComparison.Ordinal);
                if (!regression)
                {
                    return Ignored($"la incidencia {issueId} se ha reabierto sin regresión (p. ej. a mano): no dispara trabajo.");
                }
            }

            var permalink = GitHubActionEventIngestion.GetString(issue, "web_url") ?? GitHubActionEventIngestion.GetString(issue, "permalink");
            return (null, new SentryIncident(
                issueId,
                Bounded(GitHubActionEventIngestion.GetString(issue, "shortId"), 100),
                Bounded(GitHubActionEventIngestion.GetString(issue, "title"), 1000) ?? string.Empty,
                Bounded(GitHubActionEventIngestion.GetString(issue, "culprit"), 1000),
                Bounded(GitHubActionEventIngestion.GetString(issue, "level"), 50),
                slug!,
                IsHttpsUrl(permalink) ? permalink!.Trim() : null,
                regression,
                deliveryId,
                Convert.ToHexStringLower(SHA256.HashData(body.Span))));
        }

        static (SentryWebhookReceipt?, SentryIncident?) Ignored(string reason) => (new SentryWebhookReceipt(SentryWebhookOutcome.Ignored, reason), null);

        static (SentryWebhookReceipt?, SentryIncident?) Invalid(string reason) => (new SentryWebhookReceipt(SentryWebhookOutcome.InvalidPayload, reason), null);
    }

    /// <summary>Sentry envía el id como cadena numérica (y a veces como número).</summary>
    private static string? IdOf(JsonElement element)
    {
        var value = element.ValueKind switch
        {
            JsonValueKind.String => element.GetString()?.Trim(),
            JsonValueKind.Number when element.TryGetInt64(out var number) && number > 0 => number.ToString(CultureInfo.InvariantCulture),
            _ => null,
        };
        return value is not null && IssueIdPattern().IsMatch(value) ? value : null;
    }

    /// <summary>Texto no confiable acotado y sin caracteres de control.</summary>
    private static string? Bounded(string? value, int maxLength)
    {
        if (value is null)
        {
            return null;
        }

        var clean = new string([.. value.Where(c => !char.IsControl(c))]).Trim();
        return clean.Length == 0 ? null : TextLimits.Truncate(clean, maxLength);
    }

    private static bool IsHttpsUrl(string? value) =>
        value is { Length: <= 500 }
        && Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.UserInfo.Length == 0;

    /// <summary><c>Request-ID</c> es un UUID; cualquier otra cosa se trata como ausente (nunca se registra tal cual).</summary>
    private static string? NormalizeDeliveryId(string? value) =>
        Guid.TryParse(value?.Trim(), out var id) ? id.ToString("D") : null;

    [GeneratedRegex("^[1-9][0-9]{0,19}$", RegexOptions.CultureInvariant)]
    private static partial Regex IssueIdPattern();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Webhook de Sentry: entrega {DeliveryId} rechazada (firma no válida).")]
    private partial void LogInvalidSignature(string deliveryId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook de Sentry: entrega {DeliveryId} ignorada: {Reason}")]
    private partial void LogIgnored(string deliveryId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook de Sentry: entrega {DeliveryId} de la incidencia {IssueId}: el proyecto {Project} no tiene Repositorio objetivo (Evento {EventId} ignorado).")]
    private partial void LogUnmapped(string deliveryId, string issueId, string project, Guid eventId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook de Sentry: entrega {DeliveryId} en cola para la incidencia {IssueId} del proyecto {Project} ({Kind}).")]
    private partial void LogQueued(string deliveryId, string issueId, string project, string kind);

    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook de Sentry: entrega {DeliveryId} de la incidencia {IssueId} ya está en cola.")]
    private partial void LogDuplicate(string deliveryId, string issueId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Webhook de Sentry: cola llena; entrega {DeliveryId} de la incidencia {IssueId} rechazada.")]
    private partial void LogQueueFull(string deliveryId, string issueId);
}
