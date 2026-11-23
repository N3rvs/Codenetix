using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using CodeNetix.Contracts;
using CodeNetix.Ingestion.Features.RecordLabelEvent;
using CodeNetix.Ingestion.Features.SubmitGitHubActionEvent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeNetix.Ingestion.Features.ReceiveGitHubWebhook;

/// <summary>Disparo de un webhook pendiente de resolver su evento <c>labeled</c>.</summary>
/// <param name="IssueUpdatedAt"><c>issue.updated_at</c> del payload: el momento del etiquetado, para descartar eventos antiguos.</param>
/// <param name="Attempt">Intentos ya hechos (0 en la primera lectura de la cola).</param>
internal sealed record GitHubWebhookWorkItem(
    string? DeliveryId,
    string Owner,
    string Repo,
    long IssueNumber,
    string Label,
    string Sender,
    DateTimeOffset? IssueUpdatedAt,
    int Attempt = 0);

/// <summary>
/// Recepción síncrona del webhook de GitHub (AD-4, FR-7): firma, filtro y cola en memoria, sin E/S, para que el endpoint
/// responda en milisegundos. Solo <c>issues</c>/<c>labeled</c> con la etiqueta de disparo sobre una issue abierta (nunca
/// un PR) de un repositorio permitido se encola; <see cref="GitHubWebhookProcessor"/> lo procesa después.
/// </summary>
internal sealed partial class GitHubWebhookIngestion : IGitHubWebhookIngestion
{
    public const string PingEvent = "ping";

    private readonly GitHubWebhookOptions _options;
    private readonly ILogger<GitHubWebhookIngestion> _logger;
    private readonly Channel<GitHubWebhookWorkItem> _queue;
    private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.OrdinalIgnoreCase);

    public GitHubWebhookIngestion(IOptions<GitHubWebhookOptions> options, ILogger<GitHubWebhookIngestion> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _logger = logger;
        _queue = Channel.CreateBounded<GitHubWebhookWorkItem>(new BoundedChannelOptions(Math.Max(1, _options.QueueCapacity))
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });
    }

    public bool Enabled => _options.Enabled;

    public int MaxBodyBytes => _options.MaxBodyBytes;

    /// <summary>Entregas en cola o en proceso.</summary>
    internal ChannelReader<GitHubWebhookWorkItem> Reader => _queue.Reader;

    public GitHubWebhookReceipt Receive(GitHubWebhookDelivery delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        if (!_options.Enabled)
        {
            return new GitHubWebhookReceipt(GitHubWebhookOutcome.Disabled);
        }

        var deliveryId = NormalizeDeliveryId(delivery.DeliveryId);
        if (!GitHubWebhookSignature.IsValid(_options.Secret!, delivery.Body.Span, delivery.Signature256))
        {
            LogInvalidSignature(deliveryId ?? "-");
            return new GitHubWebhookReceipt(GitHubWebhookOutcome.InvalidSignature);
        }

        if (!IsJson(delivery.ContentType))
        {
            return new GitHubWebhookReceipt(
                GitHubWebhookOutcome.UnsupportedContentType,
                "el webhook debe enviar JSON: configura 'Content type' como application/json en GitHub.");
        }

        var eventName = delivery.EventName?.Trim() ?? string.Empty;
        if (string.Equals(eventName, PingEvent, StringComparison.Ordinal))
        {
            LogPing(deliveryId ?? "-");
            return new GitHubWebhookReceipt(GitHubWebhookOutcome.Pong);
        }

        var parsed = Parse(eventName, delivery.Body, deliveryId);
        if (parsed.Receipt is { } receipt)
        {
            if (receipt.Outcome == GitHubWebhookOutcome.Ignored)
            {
                LogIgnored(deliveryId ?? "-", receipt.Reason!);
            }

            return receipt;
        }

        var item = parsed.Item!;
        var repository = $"{item.Owner}/{item.Repo}";
        if (deliveryId is not null && !_pending.TryAdd(deliveryId, 0))
        {
            LogDuplicate(deliveryId, repository, item.IssueNumber);
            return new GitHubWebhookReceipt(GitHubWebhookOutcome.Duplicate);
        }

        if (!_queue.Writer.TryWrite(item))
        {
            if (deliveryId is not null)
            {
                _pending.TryRemove(deliveryId, out _);
            }

            LogQueueFull(deliveryId ?? "-", repository, item.IssueNumber);
            return new GitHubWebhookReceipt(GitHubWebhookOutcome.QueueFull, "hay demasiadas entregas pendientes; reenvíala más tarde.");
        }

        LogQueued(deliveryId ?? "-", repository, item.IssueNumber, item.Sender);
        return new GitHubWebhookReceipt(GitHubWebhookOutcome.Queued);
    }

    /// <summary>La entrega ya se procesó (con éxito o no): una reentrega vuelve a encolarse.</summary>
    internal void Complete(GitHubWebhookWorkItem item)
    {
        if (item.DeliveryId is { } id)
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>Vuelve a encolar una entrega pendiente de reintento (sigue contando como pendiente); <c>false</c> con la cola llena.</summary>
    internal bool TryRequeue(GitHubWebhookWorkItem item) => _queue.Writer.TryWrite(item);

    private (GitHubWebhookReceipt? Receipt, GitHubWebhookWorkItem? Item) Parse(string eventName, ReadOnlyMemory<byte> body, string? deliveryId)
    {
        if (!string.Equals(eventName, GitHubActionEventIngestion.IssuesEvent, StringComparison.Ordinal))
        {
            return Ignored($"el evento '{GitHubActionEventIngestion.Shown(eventName)}' no dispara trabajo: solo issues/labeled (y ping).");
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
            if (!string.Equals(action, GitHubActionEventIngestion.LabeledAction, StringComparison.Ordinal))
            {
                return Ignored($"la acción '{GitHubActionEventIngestion.Shown(action)}' del evento issues no dispara trabajo: solo 'labeled'.");
            }

            var label = root.TryGetProperty("label", out var labelElement) && labelElement.ValueKind == JsonValueKind.Object
                ? GitHubActionEventIngestion.GetString(labelElement, "name")
                : null;
            if (!string.Equals(label?.Trim(), _options.Label, StringComparison.OrdinalIgnoreCase))
            {
                return Ignored($"la etiqueta '{GitHubActionEventIngestion.Shown(label)}' no es la de disparo ('{_options.Label}').");
            }

            if (!root.TryGetProperty("issue", out var issue) || issue.ValueKind != JsonValueKind.Object
                || !issue.TryGetProperty("number", out var numberElement) || !numberElement.TryGetInt64(out var number) || number <= 0)
            {
                return Invalid("el evento issues/labeled no trae un número de issue válido.");
            }

            var fullName = root.TryGetProperty("repository", out var repository) && repository.ValueKind == JsonValueKind.Object
                ? GitHubActionEventIngestion.GetString(repository, "full_name")
                : null;
            if (!GitHubActionEventIngestion.TrySplitRepository(fullName, out var owner, out var repo))
            {
                return Invalid("el evento no trae un repository.full_name válido (owner/repo).");
            }

            owner = owner.ToLowerInvariant();
            repo = repo.ToLowerInvariant();
            if (_options.Repositories.Count > 0 && !_options.Repositories.Contains($"{owner}/{repo}"))
            {
                return Ignored($"el repositorio {owner}/{repo} no está en CODENETIX__GitHub__WebhookRepositories.");
            }

            if (issue.TryGetProperty("pull_request", out var pullRequest) && pullRequest.ValueKind == JsonValueKind.Object)
            {
                return Ignored($"#{number} es un pull request, no una issue.");
            }

            if (string.Equals(GitHubActionEventIngestion.GetString(issue, "state"), "closed", StringComparison.OrdinalIgnoreCase))
            {
                return Ignored($"la issue #{number} está cerrada.");
            }

            var sender = root.TryGetProperty("sender", out var senderElement) && senderElement.ValueKind == JsonValueKind.Object
                ? GitHubActionEventIngestion.GetString(senderElement, "login")?.Trim()
                : null;
            if (!LabelEventRecorder.IsLogin(sender))
            {
                return Invalid("el evento no trae un sender.login válido.");
            }

            DateTimeOffset? updatedAt = DateTimeOffset.TryParse(
                GitHubActionEventIngestion.GetString(issue, "updated_at"),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsedUpdatedAt)
                ? parsedUpdatedAt
                : null;

            return (null, new GitHubWebhookWorkItem(deliveryId, owner, repo, number, _options.Label, sender!, updatedAt));
        }

        static (GitHubWebhookReceipt?, GitHubWebhookWorkItem?) Ignored(string reason) => (new GitHubWebhookReceipt(GitHubWebhookOutcome.Ignored, reason), null);

        static (GitHubWebhookReceipt?, GitHubWebhookWorkItem?) Invalid(string reason) => (new GitHubWebhookReceipt(GitHubWebhookOutcome.InvalidPayload, reason), null);
    }

    private static bool IsJson(string? contentType)
    {
        var mediaType = contentType?.Split(';', 2)[0].Trim();
        return string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary><c>X-GitHub-Delivery</c> es un GUID; cualquier otra cosa se trata como ausente (nunca se registra tal cual).</summary>
    private static string? NormalizeDeliveryId(string? value) =>
        Guid.TryParseExact(value?.Trim(), "D", out var id) ? id.ToString("D") : null;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Webhook de GitHub: entrega {DeliveryId} rechazada (firma no válida).")]
    private partial void LogInvalidSignature(string deliveryId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook de GitHub: ping {DeliveryId} recibido.")]
    private partial void LogPing(string deliveryId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook de GitHub: entrega {DeliveryId} ignorada: {Reason}")]
    private partial void LogIgnored(string deliveryId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook de GitHub: entrega {DeliveryId} en cola para {Repository}#{Number} (etiquetada por {Sender}).")]
    private partial void LogQueued(string deliveryId, string repository, long number, string sender);

    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook de GitHub: entrega {DeliveryId} para {Repository}#{Number} ya está en cola.")]
    private partial void LogDuplicate(string deliveryId, string repository, long number);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Webhook de GitHub: cola llena; entrega {DeliveryId} para {Repository}#{Number} rechazada (reenvíala desde GitHub).")]
    private partial void LogQueueFull(string deliveryId, string repository, long number);
}
