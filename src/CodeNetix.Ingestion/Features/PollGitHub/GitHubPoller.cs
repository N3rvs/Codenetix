using System.Text.Json;
using CodeNetix.Contracts;
using CodeNetix.Ingestion.Data;
using CodeNetix.Ingestion.Features.SubmitManualEvent;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeNetix.Ingestion.Features.PollGitHub;

/// <summary>Resultado de un ciclo de sondeo; <see cref="RetryAfter"/> solo si un rate limit cortó el ciclo.</summary>
internal sealed record PollCycleResult(int Accepted, int Ignored, int Failed, bool RateLimited, TimeSpan? RetryAfter);

/// <summary>
/// Un ciclo del sondeo de GitHub (FR-6, FR-26, AD-4), nunca dos a la vez. Por cada repositorio: lista condicional
/// (<c>ETag</c>) de las issues abiertas con la etiqueta; por cada issue que ha cambiado desde la última vez que se procesó
/// con éxito, el evento <c>labeled</c> más reciente y su clave <c>gh:…:labeled:&lt;id&gt;</c>:
/// <list type="bullet">
/// <item>clave ya en <c>ing_events</c> → nada (ni permiso);</item>
/// <item>Evento <c>Accepted</c> de otra Fuente para la misma issue recibido después de la etiqueta → <c>Ignored</c>
/// (<see cref="AlreadyLaunchedReason"/>): la etiqueta ya se consumió;</item>
/// <item>permiso de escritura de quien etiquetó: <c>Granted</c> → <c>Accepted</c> y aviso al Dispatcher; <c>Denied</c> o sin
/// actor → <c>Ignored</c> (no autorizado); <c>Unavailable</c> → nada (falla cerrado, se reintenta).</item>
/// </list>
/// Un fallo deja el repositorio pendiente (sin guardar el <c>ETag</c>) y no afecta a los demás; un rate limit corta el ciclo.
/// Nunca crea Ejecuciones.
/// </summary>
/// <summary>Un ciclo de sondeo (costura para probar <see cref="GitHubPollingService"/> con un reloj falso).</summary>
internal interface IGitHubPollCycle
{
    Task<PollCycleResult> RunCycleAsync(CancellationToken cancellationToken);
}

internal sealed partial class GitHubPoller(
    IOptions<GitHubPollingOptions> options,
    ILabeledIssueSource source,
    IRepositoryPermissions permissions,
    IDbContextFactory<IngestionDbContext> contextFactory,
    IEventDispatchSignal dispatchSignal,
    TimeProvider timeProvider,
    ILogger<GitHubPoller> logger) : IGitHubPollCycle, IDisposable
{
    public const string AlreadyLaunchedReason = "ya lanzada por otra Fuente después de etiquetarla";

    private const int SqliteConstraint = 19;
    private const int SqliteConstraintUnique = 2067;

    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, RepositoryState> _states = new(StringComparer.Ordinal);
    private int _completedCycles;
    private int _disposed;

    /// <summary>Ciclos terminados (con éxito o no) desde el arranque.</summary>
    public int CompletedCycles => Volatile.Read(ref _completedCycles);

    public async Task<PollCycleResult> RunCycleAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var settings = options.Value;
            var totals = new Counters();
            foreach (var repository in settings.Repositories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var failure = await PollRepositoryAsync(repository, settings.Label, totals, cancellationToken).ConfigureAwait(false);
                if (failure is { RateLimited: true })
                {
                    // El límite es del token: seguir con otros repositorios solo gastaría más peticiones.
                    LogRateLimited(repository.FullName, failure.RetryAfter ?? settings.Interval);
                    return new PollCycleResult(totals.Accepted, totals.Ignored, totals.Failed + 1, true, failure.RetryAfter);
                }

                if (failure is not null)
                {
                    totals.Failed++;
                }
            }

            return new PollCycleResult(totals.Accepted, totals.Ignored, totals.Failed, false, null);
        }
        finally
        {
            Interlocked.Increment(ref _completedCycles);
            _gate.Release();
        }
    }

    /// <summary>
    /// Idempotente. El semáforo solo se libera si no hay un ciclo en curso (si lo hay, se deja al recolector: liberarlo
    /// haría fallar el <c>Release</c> del ciclo). Después, <see cref="RunCycleAsync"/> lanza <see cref="ObjectDisposedException"/>.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_gate.Wait(0))
        {
            _gate.Dispose();
        }
    }

    /// <summary>Sondea un repositorio; devuelve el fallo que lo dejó pendiente, o <c>null</c> si terminó.</summary>
    private async Task<GitHubQueryFailure?> PollRepositoryAsync(PolledRepository repository, string label, Counters totals, CancellationToken cancellationToken)
    {
        if (!_states.TryGetValue(repository.FullName, out var state))
        {
            state = new RepositoryState();
            _states[repository.FullName] = state;
        }

        try
        {
            var list = await source.ListOpenAsync(repository.Owner, repository.Repo, label, state.ETag, cancellationToken).ConfigureAwait(false);
            if (list.Status == LabeledIssueListStatus.NotModified)
            {
                return null;
            }

            if (list.Status == LabeledIssueListStatus.Unavailable)
            {
                state.ETag = null;
                LogRepositoryUnavailable(repository.FullName, list.Failure!.Reason);
                return list.Failure;
            }

            if (list.Truncated)
            {
                LogTruncated(repository.FullName, list.Issues.Count);
            }

            GitHubQueryFailure? pending = null;
            foreach (var issue in list.Issues)
            {
                if (issue.UpdatedAt.Length > 0
                    && state.Processed.TryGetValue(issue.Number, out var processedAt)
                    && string.Equals(processedAt, issue.UpdatedAt, StringComparison.Ordinal))
                {
                    continue;
                }

                GitHubQueryFailure? failure;
                try
                {
                    failure = await ProcessIssueAsync(repository, issue, label, state, totals, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    // Base de datos ocupada u otro fallo inesperado con esta issue: queda pendiente y se sigue con las demás.
                    LogIssueFailed(ex, repository.FullName, issue.Number);
                    failure = new GitHubQueryFailure("fallo inesperado al sondear", false, null);
                }

                if (failure is null)
                {
                    state.Processed[issue.Number] = issue.UpdatedAt;
                    continue;
                }

                LogIssueUnavailable(repository.FullName, issue.Number, failure.Reason);
                if (failure.RateLimited)
                {
                    // El límite es del token: el resto de issues fallaría igual.
                    pending = failure;
                    break;
                }

                // Cualquier otro fallo no bloquea a las demás issues: el repositorio queda pendiente (sin ETag).
                pending ??= failure;
            }

            // Lo que ya no tiene la etiqueta (o se cerró) se olvida: si vuelve, se mira de nuevo.
            var listed = list.Issues.Select(i => i.Number).ToHashSet();
            foreach (var number in state.Processed.Keys.Where(n => !listed.Contains(n)).ToList())
            {
                state.Processed.Remove(number);
            }

            foreach (var number in state.WarnedWithoutLabelEvent.Keys.Where(n => !listed.Contains(n)).ToList())
            {
                state.WarnedWithoutLabelEvent.Remove(number);
            }

            // Solo un repositorio sin pendientes guarda el ETag: un 304 nunca debe tapar un reintento.
            state.ETag = pending is null ? list.ETag : null;
            return pending;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Base de datos ocupada u otro fallo inesperado: el repositorio queda pendiente y los demás siguen.
            state.ETag = null;
            LogRepositoryFailed(ex, repository.FullName);
            return new GitHubQueryFailure("fallo inesperado al sondear", false, null);
        }
    }

    /// <summary><c>null</c> si la issue quedó resuelta (Evento creado, ya existente o sin evento <c>labeled</c>).</summary>
    private async Task<GitHubQueryFailure?> ProcessIssueAsync(
        PolledRepository repository,
        LabeledIssue issue,
        string label,
        RepositoryState state,
        Counters totals,
        CancellationToken cancellationToken)
    {
        var number = issue.Number;
        var lookup = await source.FindLatestLabelEventAsync(repository.Owner, repository.Repo, number, label, cancellationToken).ConfigureAwait(false);
        if (lookup.Status == LabelEventLookupStatus.Unavailable)
        {
            return lookup.Failure;
        }

        if (lookup.Status == LabelEventLookupStatus.NotFound)
        {
            // Un aviso por (issue, updated_at): no se repite en cada ciclo mientras la issue no cambie.
            if (!state.WarnedWithoutLabelEvent.TryGetValue(number, out var warnedAt) || !string.Equals(warnedAt, issue.UpdatedAt, StringComparison.Ordinal))
            {
                state.WarnedWithoutLabelEvent[number] = issue.UpdatedAt;
                LogNoLabelEvent(repository.FullName, number, label);
            }

            return null;
        }

        var labelEvent = lookup.Event!;
        var dedupKey = GitHubLabelEventKey.Format(repository.Owner, repository.Repo, number, labelEvent.Id);
        var objectKey = GitHubObjectKey.Format(repository.Owner, repository.Repo, number);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        if (await context.Events.AsNoTracking().AnyAsync(e => e.DedupKey == dedupKey, cancellationToken).ConfigureAwait(false))
        {
            return null;
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
            var access = await permissions.GetWriteAccessAsync(repository.Owner, repository.Repo, actor, cancellationToken).ConfigureAwait(false);
            if (access.Status == WriteAccessStatus.Unavailable)
            {
                return new GitHubQueryFailure(access.Reason ?? "GitHub no responde", false, null);
            }

            (status, reason) = access.Status == WriteAccessStatus.Granted
                ? (EventStatus.Accepted, (string?)null)
                : (EventStatus.Ignored, ManualEventIngestion.UnauthorizedReason);
        }

        var payload = new PolledEventPayload(repository.FullName, number, label, labelEvent.Id, labelEvent.CreatedAt);
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
            // Otra Fuente con la misma clave (webhook, 7.2) ganó la carrera: ya existe.
            return null;
        }

        if (status == EventStatus.Accepted)
        {
            totals.Accepted++;
            dispatchSignal.Notify();
            LogAccepted(repository.FullName, number, envelope.Id, actor!);
        }
        else
        {
            totals.Ignored++;
            LogIgnored(repository.FullName, number, envelope.Id, reason!);
        }

        return null;
    }

    /// <summary>Login de GitHub plausible: no vacío, ≤ 100 caracteres, sin caracteres de control ni <c>/</c>.</summary>
    private static bool IsLogin(string? login)
    {
        var trimmed = login?.Trim();
        return !string.IsNullOrEmpty(trimmed) && trimmed.Length <= 100 && !trimmed.Any(char.IsControl) && !trimmed.Contains('/');
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Sondeo: Evento {EventId} aceptado para {Repository}#{Number} (etiquetada por {Actor}).")]
    private partial void LogAccepted(string repository, long number, Guid eventId, string actor);

    [LoggerMessage(Level = LogLevel.Information, Message = "Sondeo: Evento {EventId} ignorado para {Repository}#{Number}: {Reason}.")]
    private partial void LogIgnored(string repository, long number, Guid eventId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sondeo: {Repository}#{Number} lleva la etiqueta '{Label}' pero no se encuentra su evento labeled; se mirará cuando cambie.")]
    private partial void LogNoLabelEvent(string repository, long number, string label);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sondeo: no se ha podido listar {Repository}: {Reason}. Se reintenta en el siguiente ciclo.")]
    private partial void LogRepositoryUnavailable(string repository, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sondeo: {Repository}#{Number} queda pendiente: {Reason}. Se reintenta en el siguiente ciclo.")]
    private partial void LogIssueUnavailable(string repository, long number, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sondeo: GitHub ha limitado las peticiones al sondear {Repository}; el siguiente ciclo espera {Wait}.")]
    private partial void LogRateLimited(string repository, TimeSpan wait);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sondeo: {Repository} tiene más issues etiquetadas de las que se revisan por ciclo; solo se procesan las {Count} actualizadas más recientemente.")]
    private partial void LogTruncated(string repository, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Sondeo: fallo inesperado al sondear {Repository}; se reintenta en el siguiente ciclo.")]
    private partial void LogRepositoryFailed(Exception exception, string repository);

    [LoggerMessage(Level = LogLevel.Error, Message = "Sondeo: fallo inesperado con {Repository}#{Number}; queda pendiente y se sigue con las demás issues.")]
    private partial void LogIssueFailed(Exception exception, string repository, long number);

    private sealed class RepositoryState
    {
        public string? ETag { get; set; }

        /// <summary>Número → <c>updated_at</c> de la última vez que la issue se resolvió con éxito.</summary>
        public Dictionary<long, string> Processed { get; } = [];

        /// <summary>Número → <c>updated_at</c> con el que ya se avisó de que falta el evento <c>labeled</c>.</summary>
        public Dictionary<long, string> WarnedWithoutLabelEvent { get; } = [];
    }

    private sealed class Counters
    {
        public int Accepted { get; set; }

        public int Ignored { get; set; }

        public int Failed { get; set; }
    }

    /// <summary>Lo único que se guarda del sondeo: datos del disparo, nunca el JSON de GitHub ni el token.</summary>
    private sealed record PolledEventPayload(string Repository, long IssueNumber, string Label, long LabelEventId, DateTimeOffset? LabeledAt);
}
