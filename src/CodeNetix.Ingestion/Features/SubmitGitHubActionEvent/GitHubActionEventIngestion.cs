using System.Globalization;
using System.Text;
using System.Text.Json;
using CodeNetix.Contracts;
using CodeNetix.Ingestion.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CodeNetix.Ingestion.Features.SubmitGitHubActionEvent;

/// <summary>
/// Fuente <see cref="EventSource.GitHubAction"/> (AD-4, AD-12, FR-25, FR-26): el evento del runner de GitHub Actions
/// (<c>GITHUB_EVENT_PATH</c>, que Host lee y pasa como texto) se normaliza a un <see cref="EventEnvelope"/>. Solo aplica
/// <c>issues</c>/<c>labeled</c> con la etiqueta de disparo sobre una issue abierta (nunca PR). No hay firma: la autorización
/// es el permiso de escritura de <c>sender</c> (quien puso la etiqueta), no la identidad del token. Orden de
/// <see cref="SubmitAsync"/>: buscar si ya existe → permiso → persistir; sin permiso, <see cref="EventStatus.Ignored"/>;
/// si GitHub no responde, nada (falla cerrado). Nunca crea Ejecuciones: tras un <c>Accepted</c> avisa al Dispatcher.
/// </summary>
internal sealed class GitHubActionEventIngestion(
    IDbContextFactory<IngestionDbContext> contextFactory,
    IRepositoryPermissions permissions,
    IEventDispatchSignal dispatchSignal,
    TimeProvider timeProvider) : IGitHubActionEventIngestion
{
    public const string IssuesEvent = "issues";
    public const string LabeledAction = "labeled";
    public const string DedupKeyPrefix = "gh-action:";

    private const int SqliteConstraint = 19;
    private const int SqliteConstraintUnique = 2067;

    /// <summary>Texto del evento que se muestra en un motivo: acotado y sin caracteres de control.</summary>
    private const int MaxShownLength = 100;

    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web);

    public GitHubActionEventParse Parse(GitHubActionEventInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(input.EventPayloadJson ?? string.Empty);
        }
        catch (JsonException)
        {
            return GitHubActionEventParse.Invalid("el archivo de GITHUB_EVENT_PATH no contiene un JSON válido.");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return GitHubActionEventParse.Invalid("el archivo de GITHUB_EVENT_PATH no contiene un objeto JSON.");
            }

            var eventName = input.EventName?.Trim() ?? string.Empty;
            if (!string.Equals(eventName, IssuesEvent, StringComparison.Ordinal))
            {
                return GitHubActionEventParse.NotApplicable(
                    $"el evento '{Shown(eventName)}' no dispara trabajo: codenetix action solo actúa al etiquetar una issue (issues/labeled).");
            }

            var action = GetString(root, "action");
            if (!string.Equals(action, LabeledAction, StringComparison.Ordinal))
            {
                return GitHubActionEventParse.NotApplicable(
                    $"la acción '{Shown(action)}' del evento issues no dispara trabajo: solo 'labeled'.");
            }

            var triggerLabel = string.IsNullOrWhiteSpace(input.TriggerLabel) ? "codenetix" : input.TriggerLabel.Trim();
            var label = root.TryGetProperty("label", out var labelElement) && labelElement.ValueKind == JsonValueKind.Object
                ? GetString(labelElement, "name")
                : null;
            if (!string.Equals(label?.Trim(), triggerLabel, StringComparison.OrdinalIgnoreCase))
            {
                return GitHubActionEventParse.NotApplicable(
                    $"la etiqueta '{Shown(label)}' no es la de disparo ('{Shown(triggerLabel)}').");
            }

            if (!root.TryGetProperty("issue", out var issue) || issue.ValueKind != JsonValueKind.Object
                || !issue.TryGetProperty("number", out var numberElement) || !numberElement.TryGetInt64(out var number) || number <= 0)
            {
                return GitHubActionEventParse.Invalid("el evento issues/labeled no trae un número de issue válido.");
            }

            if (issue.TryGetProperty("pull_request", out var pullRequest) && pullRequest.ValueKind == JsonValueKind.Object)
            {
                return GitHubActionEventParse.NotApplicable($"#{number} es un pull request, no una issue.");
            }

            if (string.Equals(GetString(issue, "state"), "closed", StringComparison.OrdinalIgnoreCase))
            {
                return GitHubActionEventParse.NotApplicable($"la issue #{number} está cerrada.");
            }

            var fullName = root.TryGetProperty("repository", out var repository) && repository.ValueKind == JsonValueKind.Object
                ? GetString(repository, "full_name")
                : null;
            if (!TrySplitRepository(fullName, out var owner, out var repo))
            {
                return GitHubActionEventParse.Invalid("el evento no trae un repository.full_name válido (owner/repo).");
            }

            if (!string.IsNullOrWhiteSpace(input.Repository)
                && !string.Equals(input.Repository.Trim(), $"{owner}/{repo}", StringComparison.OrdinalIgnoreCase))
            {
                return GitHubActionEventParse.Invalid(
                    $"GITHUB_REPOSITORY ('{Shown(input.Repository)}') no coincide con el repositorio del evento ('{owner}/{repo}').");
            }

            var sender = root.TryGetProperty("sender", out var senderElement) && senderElement.ValueKind == JsonValueKind.Object
                ? GetString(senderElement, "login")?.Trim()
                : null;
            if (string.IsNullOrEmpty(sender) || sender.Length > 100 || sender.Any(char.IsControl) || sender.Contains('/'))
            {
                return GitHubActionEventParse.Invalid("el evento no trae un sender.login válido.");
            }

            owner = owner.ToLowerInvariant();
            repo = repo.ToLowerInvariant();
            var runId = Normalize(input.WorkflowRunId);
            var attempt = Normalize(input.WorkflowRunAttempt) ?? "1";
            var n = number.ToString(CultureInfo.InvariantCulture);

            // Un reintento del mismo job (o una segunda llamada en el mismo paso) no duplica; sin GITHUB_RUN_ID no hay
            // con qué deduplicar y cada llamada es un Evento nuevo.
            var dedupKey = runId is null
                ? $"{DedupKeyPrefix}{owner}/{repo}#{n}:local:{Guid.CreateVersion7():D}"
                : $"{DedupKeyPrefix}{owner}/{repo}#{n}:run:{runId}:{attempt}";

            return GitHubActionEventParse.Applicable(new GitHubActionTrigger(
                Owner: owner,
                Repo: repo,
                IssueNumber: number,
                Sender: sender,
                Label: triggerLabel,
                ObjectKey: GitHubObjectKey.Format(owner, repo, number),
                DedupKey: dedupKey,
                WorkflowRunId: runId,
                WorkflowRunAttempt: runId is null ? null : attempt));
        }
    }

    public async Task<ManualEventResult> SubmitAsync(GitHubActionTrigger trigger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(trigger);

        var repository = $"{trigger.Owner}/{trigger.Repo}";
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var existing = await FindAsync(context, trigger.DedupKey, repository, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return ManualEventResult.Existing(existing);
        }

        // FR-26: el actor es quien puso la etiqueta; el token del runner solo se usa para preguntar.
        var access = await permissions.GetWriteAccessAsync(trigger.Owner, trigger.Repo, trigger.Sender, cancellationToken).ConfigureAwait(false);
        if (access.Status == WriteAccessStatus.Unavailable)
        {
            return ManualEventResult.Unavailable(access.Reason ?? "GitHub no responde");
        }

        var granted = access.Status == WriteAccessStatus.Granted;
        var payload = new GitHubActionEventPayload(
            IssuesEvent,
            LabeledAction,
            repository,
            trigger.IssueNumber,
            trigger.Label,
            trigger.WorkflowRunId,
            trigger.WorkflowRunAttempt);
        var envelope = new EventEnvelope(
            Id: Guid.CreateVersion7(),
            Source: EventSource.GitHubAction,
            ObjectKey: trigger.ObjectKey,
            DedupKey: trigger.DedupKey,
            Actor: trigger.Sender,
            Payload: JsonSerializer.Serialize(payload, PayloadJson),
            Status: granted ? EventStatus.Accepted : EventStatus.Ignored,
            IgnoredReason: granted ? null : SubmitManualEvent.ManualEventIngestion.UnauthorizedReason,
            ReceivedAt: timeProvider.GetUtcNow());

        context.Events.Add(EventRecord.FromEnvelope(envelope));
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (granted)
            {
                dispatchSignal.Notify();
            }

            return ManualEventResult.Created(new ManualEventInfo(envelope.Id, envelope.Status, envelope.Actor, repository, envelope.IgnoredReason));
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: SqliteConstraint, SqliteExtendedErrorCode: SqliteConstraintUnique })
        {
            context.ChangeTracker.Clear();
            var winner = await FindAsync(context, trigger.DedupKey, repository, cancellationToken).ConfigureAwait(false);
            if (winner is not null)
            {
                return ManualEventResult.Existing(winner);
            }

            throw;
        }
    }

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

    internal static bool TrySplitRepository(string? fullName, out string owner, out string repo)
    {
        owner = string.Empty;
        repo = string.Empty;
        if (string.IsNullOrWhiteSpace(fullName))
        {
            return false;
        }

        var parts = fullName.Trim().Split('/');
        if (parts.Length != 2 || !GitHubObjectKey.IsName(parts[0]) || !GitHubObjectKey.IsName(parts[1]))
        {
            return false;
        }

        owner = parts[0];
        repo = parts[1];
        return true;
    }

    /// <summary><c>GITHUB_RUN_ID</c>/<c>GITHUB_RUN_ATTEMPT</c>: solo dígitos; cualquier otra cosa se ignora.</summary>
    private static string? Normalize(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) || trimmed.Length > 20 || !trimmed.All(char.IsAsciiDigit) ? null : trimmed;
    }

    internal static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>Texto no confiable mostrado en un motivo: sin caracteres de control y como mucho <see cref="MaxShownLength"/>.</summary>
    internal static string Shown(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(Math.Min(value.Length, MaxShownLength));
        foreach (var c in value)
        {
            if (builder.Length == MaxShownLength)
            {
                builder.Append('…');
                break;
            }

            builder.Append(char.IsControl(c) ? ' ' : c);
        }

        return builder.ToString();
    }

    /// <summary>Lo único que se guarda del evento del runner: datos del disparo, nunca el JSON completo ni el token.</summary>
    private sealed record GitHubActionEventPayload(
        string Event,
        string Action,
        string Repository,
        long IssueNumber,
        string Label,
        string? WorkflowRunId,
        string? WorkflowRunAttempt);
}
