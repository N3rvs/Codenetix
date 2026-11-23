using System.Globalization;
using System.Net;
using System.Text.Json;
using CodeNetix.Contracts;
using CodeNetix.GitHub.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeNetix.GitHub.Features.NotifyIssue;

/// <summary>
/// Avisos en issues (AD-8), idempotentes por (<c>runId</c>, <c>kind</c>) en <c>gh_notifications</c>:
/// <list type="number">
/// <item>Inserta la fila en <c>Pending</c>. Si ya existe (UNIQUE), la relee: en <c>Posted</c> devuelve el comentario
/// existente sin llamar a GitHub; en <c>Pending</c> (caída a medias) o <c>Failed</c> (nueva llamada explícita) sigue.</item>
/// <item>Busca el marcador oculto <see cref="Marker"/> en las <see cref="MaxSearchPages"/> últimas páginas de
/// <see cref="PageSize"/> comentarios de la issue (según <c>Link: rel="last"</c>). Si lo encuentra en un comentario del
/// propio bot, lo adopta y marca <c>Posted</c>.</item>
/// <item>Si no, publica el cuerpo saneado (<see cref="IssueCommentSanitizer"/>) más el marcador y marca <c>Posted</c>,
/// o <c>Failed</c> con el error redactado.</item>
/// </list>
/// Las llamadas con la misma clave dentro del proceso se serializan: una segunda llamada concurrente espera, ve la
/// violación UNIQUE, relee la fila (ya <c>Posted</c>) y no publica. El token nunca se guarda ni se registra.
/// No hay reintentos en segundo plano: el arranque (historia 5.3) vuelve a intentar los avisos <c>RunFailed</c> de las
/// Ejecuciones cerradas por reinicio que no llegaron a publicarse. El resultado se guarda con un plazo propio.
/// </summary>
internal sealed partial class IssueNotifier(
    IDbContextFactory<GitHubDbContext> contextFactory,
    GitHubApiClient client,
    IGitHubIdentity identity,
    ISecretRedactor redactor,
    TimeProvider timeProvider,
    ILogger<IssueNotifier> logger) : IIssueNotifier
{
    public const int PageSize = 100;

    public const int MaxSearchPages = 3;

    /// <summary>Longitud máxima del cuerpo saneado (sin el marcador); GitHub admite 65 536.</summary>
    public const int MaxBodyLength = 4000;

    public const int MaxErrorLength = 500;

    private readonly Dictionary<(Guid RunId, NotificationKind Kind), Gate> _gates = [];
    private readonly Lock _gatesLock = new();

    /// <summary>Marcador oculto que identifica el comentario de (<paramref name="runId"/>, <paramref name="kind"/>).</summary>
    public static string Marker(Guid runId, NotificationKind kind) =>
        string.Create(CultureInfo.InvariantCulture, $"<!-- codenetix:run={runId:D};kind={kind} -->");

    public async Task<IssueNotification> NotifyAsync(
        Guid runId,
        string owner,
        string repo,
        long number,
        NotificationKind kind,
        string body,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(number);
        ArgumentNullException.ThrowIfNull(body);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "NotificationKind desconocido.");
        }

        var key = (runId, kind);
        var gate = Enter(key);
        try
        {
            await gate.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await NotifyCoreAsync(runId, owner, repo, number, kind, body, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Semaphore.Release();
            }
        }
        finally
        {
            Leave(key, gate);
        }
    }

    private async Task<IssueNotification> NotifyCoreAsync(
        Guid runId,
        string owner,
        string repo,
        long number,
        NotificationKind kind,
        string body,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var now = timeProvider.GetUtcNow();
        var record = new NotificationRecord
        {
            Id = Guid.CreateVersion7(now),
            RunId = runId,
            Kind = kind,
            Status = NotificationStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now,
        };
        context.Notifications.Add(record);
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            context.Entry(record).State = EntityState.Detached;
            record = await context.Notifications
                .SingleAsync(n => n.RunId == runId && n.Kind == kind, cancellationToken)
                .ConfigureAwait(false);
            if (record.Status == NotificationStatus.Posted)
            {
                LogAlreadyPosted(RunShortId.Of(runId), kind);
                return IssueNotification.Posted(record.CommentUrl ?? string.Empty);
            }
        }

        var issuePath = IssuePath(owner, repo, number);
        var marker = Marker(runId, kind);
        try
        {
            if (await FindMarkedCommentAsync(issuePath, marker, cancellationToken).ConfigureAwait(false) is { } existing)
            {
                LogAdopted(RunShortId.Of(runId), kind);
                return await MarkPostedAsync(context, record, existing).ConfigureAwait(false);
            }

            var sanitized = kind == NotificationKind.PullRequestCreated
                ? IssueCommentSanitizer.SanitizeAllowingPullRequestLinks(body, MaxBodyLength, owner, repo)
                : IssueCommentSanitizer.Sanitize(body, MaxBodyLength);
            var text = sanitized + "\n\n" + marker;
            var response = await client.PostAsync(issuePath + "/comments", new { body = text }, cancellationToken).ConfigureAwait(false);
            if (response.Status != HttpStatusCode.Created)
            {
                return await MarkFailedAsync(context, record, Describe(response)).ConfigureAwait(false);
            }

            if (ReadComment(response.Body) is not { } posted)
            {
                // Publicado pero sin id/URL legibles: una llamada posterior lo adoptará por el marcador.
                return await MarkFailedAsync(context, record, GitHubApiClient.NotRespondingReason).ConfigureAwait(false);
            }

            LogPosted(RunShortId.Of(runId), kind);
            return await MarkPostedAsync(context, record, posted).ConfigureAwait(false);
        }
        catch (GitHubUnavailableException ex)
        {
            return await MarkFailedAsync(context, record, ex.Message).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Plazo del llamante agotado (quizá con el comentario ya creado en GitHub): la fila no se queda en Pending;
            // una llamada posterior lo adoptará por el marcador o lo publicará.
            try
            {
                await MarkFailedAsync(context, record, TimedOutReason).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is DbUpdateException or OperationCanceledException or InvalidOperationException)
            {
                // Mejor esfuerzo: la fila queda en Pending y también se reintenta.
            }

            throw;
        }
    }

    public const string TimedOutReason = "tiempo agotado al publicar el aviso en GitHub";

    /// <summary>
    /// Plazo propio para guardar el resultado en SQLite: nunca el token del llamante, que puede haber vencido justo
    /// después de que GitHub creara el comentario.
    /// </summary>
    private static readonly TimeSpan PersistTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Comentario del bot (<see cref="IGitHubIdentity"/>, sin distinguir mayúsculas) con <paramref name="marker"/> en
    /// las <see cref="MaxSearchPages"/> últimas páginas de <see cref="PageSize"/>. GitHub los lista en orden de
    /// creación; la última página sale de <c>Link: rel="last"</c> de la primera (sin ella, la primera es la última).
    /// No se usa el contador <c>comments</c> de la issue, que GitHub puede servir desde caché.
    /// </summary>
    private async Task<Comment?> FindMarkedCommentAsync(string issuePath, string marker, CancellationToken cancellationToken)
    {
        var first = await GetCommentPageAsync(issuePath, 1, cancellationToken).ConfigureAwait(false);
        var lastPage = LastPage(first.Link) ?? 1;
        var firstPage = Math.Max(1, lastPage - MaxSearchPages + 1);
        string? login = null;
        for (var page = lastPage; page >= firstPage; page--)
        {
            var response = page == 1 ? first : await GetCommentPageAsync(issuePath, page, cancellationToken).ConfigureAwait(false);
            foreach (var item in response.Body!.Value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("body", out var commentBody)
                    || commentBody.ValueKind != JsonValueKind.String
                    || !commentBody.GetString()!.Contains(marker, StringComparison.Ordinal)
                    || ReadComment(item) is not { } found)
                {
                    continue;
                }

                // Solo cuenta un marcador publicado por el propio bot (otro usuario podría copiarlo).
                login ??= await identity.GetLoginAsync(cancellationToken).ConfigureAwait(false);
                if (item.TryGetProperty("user", out var user)
                    && user.ValueKind == JsonValueKind.Object
                    && user.TryGetProperty("login", out var author)
                    && author.ValueKind == JsonValueKind.String
                    && string.Equals(author.GetString(), login, StringComparison.OrdinalIgnoreCase))
                {
                    return found;
                }
            }
        }

        return null;
    }

    private async Task<GitHubResponse> GetCommentPageAsync(string issuePath, long page, CancellationToken cancellationToken)
    {
        var path = string.Create(CultureInfo.InvariantCulture, $"{issuePath}/comments?per_page={PageSize}&page={page}");
        var response = await client.GetAsync(path, cancellationToken).ConfigureAwait(false);
        if (response.Status != HttpStatusCode.OK)
        {
            throw new GitHubUnavailableException(Describe(response));
        }

        if (response.Body is not { ValueKind: JsonValueKind.Array })
        {
            throw new GitHubUnavailableException(GitHubApiClient.NotRespondingReason);
        }

        return response;
    }

    /// <summary><c>&lt;…&amp;page=5&gt;; rel="last"</c> → 5; <c>null</c> si no hay <c>rel="last"</c> legible.</summary>
    internal static long? LastPage(string? link)
    {
        if (string.IsNullOrEmpty(link))
        {
            return null;
        }

        foreach (var part in link.Split(','))
        {
            if (!part.Contains("rel=\"last\"", StringComparison.Ordinal))
            {
                continue;
            }

            var match = PageParameter().Match(part);
            return match.Success && long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var page) && page >= 1
                ? page
                : null;
        }

        return null;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"[?&]page=(\d{1,9})(?:[&>]|$)", System.Text.RegularExpressions.RegexOptions.None, 1000)]
    private static partial System.Text.RegularExpressions.Regex PageParameter();

    private async Task<IssueNotification> MarkPostedAsync(GitHubDbContext context, NotificationRecord record, Comment comment)
    {
        record.Status = NotificationStatus.Posted;
        record.CommentId = comment.Id;
        record.CommentUrl = comment.Url;
        record.Error = null;
        record.UpdatedAt = timeProvider.GetUtcNow();
        using var persist = new CancellationTokenSource(PersistTimeout);
        await context.SaveChangesAsync(persist.Token).ConfigureAwait(false);
        return IssueNotification.Posted(comment.Url);
    }

    private async Task<IssueNotification> MarkFailedAsync(GitHubDbContext context, NotificationRecord record, string error)
    {
        var redacted = TextLimits.Truncate(redactor.Redact(error), MaxErrorLength);

        record.Status = NotificationStatus.Failed;
        record.Error = redacted;
        record.UpdatedAt = timeProvider.GetUtcNow();
        using var persist = new CancellationTokenSource(PersistTimeout);
        await context.SaveChangesAsync(persist.Token).ConfigureAwait(false);
        LogFailed(RunShortId.Of(record.RunId), record.Kind, redacted);
        return IssueNotification.Failed(redacted);
    }

    private static string Describe(GitHubResponse response) =>
        response.Status is HttpStatusCode.NotFound or HttpStatusCode.Gone
            ? $"{IssueReader.NotFoundReason} (HTTP {(int)response.Status})"
            : $"{GitHubApiClient.FailureReason(response)} (HTTP {(int)response.Status})";

    private static Comment? ReadComment(JsonElement? element) =>
        element is { ValueKind: JsonValueKind.Object } json
        && json.TryGetProperty("id", out var id) && id.TryGetInt64(out var commentId)
        && json.TryGetProperty("html_url", out var url) && url.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(url.GetString())
            ? new Comment(commentId, url.GetString()!)
            : null;

    private static string IssuePath(string owner, string repo, long number) =>
        string.Create(CultureInfo.InvariantCulture, $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/issues/{number}");

    private const int SqliteConstraint = 19;

    /// <summary>Violación de UNIQUE o de clave primaria en SQLite.</summary>
    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is SqliteException { SqliteErrorCode: SqliteConstraint, SqliteExtendedErrorCode: 2067 or 1555 };

    private Gate Enter((Guid, NotificationKind) key)
    {
        lock (_gatesLock)
        {
            if (!_gates.TryGetValue(key, out var gate))
            {
                gate = new Gate();
                _gates[key] = gate;
            }

            gate.Users++;
            return gate;
        }
    }

    private void Leave((Guid, NotificationKind) key, Gate gate)
    {
        lock (_gatesLock)
        {
            if (--gate.Users == 0)
            {
                _gates.Remove(key);
                gate.Semaphore.Dispose();
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Ejecución {ShortId}: comentario {Kind} publicado en la issue.")]
    private partial void LogPosted(string shortId, NotificationKind kind);

    [LoggerMessage(Level = LogLevel.Information, Message = "Ejecución {ShortId}: el comentario {Kind} ya estaba en la issue (marcador); se adopta sin publicar.")]
    private partial void LogAdopted(string shortId, NotificationKind kind);

    [LoggerMessage(Level = LogLevel.Information, Message = "Ejecución {ShortId}: el comentario {Kind} ya estaba publicado; no se repite.")]
    private partial void LogAlreadyPosted(string shortId, NotificationKind kind);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ejecución {ShortId}: no se ha podido publicar el comentario {Kind}: {Error}")]
    private partial void LogFailed(string shortId, NotificationKind kind, string error);

    private sealed record Comment(long Id, string Url);

    private sealed class Gate
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int Users { get; set; }
    }
}
