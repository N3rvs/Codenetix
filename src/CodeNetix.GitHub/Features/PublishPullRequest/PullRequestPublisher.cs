using System.Globalization;
using System.Net;
using System.Text.Json;
using CodeNetix.Contracts;
using CodeNetix.GitHub.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeNetix.GitHub.Features.PublishPullRequest;

/// <summary>
/// Publicador idempotente de Pull Requests (AD-8, AD-11, FR-17):
/// <list type="number">
/// <item>Comprueba <c>gh_pull_requests</c> por <c>run_id</c>: si ya existe, devuelve el PR persistido sin repetir
/// push ni llamada a GitHub.</item>
/// <item>Crea un único commit sobre <c>BaseSha</c> y hace push de <c>codenetix/&lt;runId&gt;</c> a través de
/// <see cref="RepositoryWorkspace.CommitAndPushBranchAsync"/> (validado por <see cref="GitSafetyGuard"/>).</item>
/// <item>Abre el PR contra la rama por defecto (<c>POST /repos/{owner}/{repo}/pulls</c>) y persiste en
/// <c>gh_pull_requests</c>.</item>
/// <item>Añade la etiqueta <c>codenetix</c> (<c>POST /repos/{owner}/{repo}/issues/{number}/labels</c>); si la
/// etiqueta falla, registra un warning y devuelve el PR ya persistido sin perderlo.</item>
/// </list>
/// </summary>
internal sealed partial class PullRequestPublisher(
    IDbContextFactory<GitHubDbContext> contextFactory,
    RepositoryWorkspace workspace,
    GitHubApiClient client,
    ISecretRedactor redactor,
    TimeProvider timeProvider,
    ILogger<PullRequestPublisher> logger) : IPullRequestPublisher
{
    public const string LabelName = "codenetix";

    public const int MaxTitleLength = 256;

    public const int MaxBodyLength = 60_000;

    private const int SqliteConstraint = 19;

    private readonly Dictionary<Guid, Gate> _gates = [];
    private readonly Lock _gatesLock = new();

    public async Task<PullRequestInfo> PublishAsync(PullRequestRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RunId == Guid.Empty)
        {
            throw new ArgumentException("RunId no puede estar vacío.", nameof(request));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(request.Owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Repo);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.BaseSha);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Title);
        ArgumentNullException.ThrowIfNull(request.Body);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CommitMessage);

        var gate = Enter(request.RunId);
        try
        {
            await gate.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await PublishCoreAsync(request, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Semaphore.Release();
            }
        }
        finally
        {
            Leave(request.RunId, gate);
        }
    }

    public async Task<PullRequestInfo?> FindByRunAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var record = await context.PullRequests
            .AsNoTracking()
            .SingleOrDefaultAsync(p => p.RunId == runId, cancellationToken)
            .ConfigureAwait(false);
        return record is null ? null : ToInfo(record);
    }

    private async Task<PullRequestInfo> PublishCoreAsync(PullRequestRequest request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var existing = await context.PullRequests
            .AsNoTracking()
            .SingleOrDefaultAsync(p => p.RunId == request.RunId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            LogAlreadyPublished(RunShortId.Of(request.RunId), existing.Number);
            return ToInfo(existing);
        }

        var branchName = PullRequestBranch.For(request.RunId);
        PushedBranch pushed;
        try
        {
            pushed = await workspace.CommitAndPushBranchAsync(
                request.RunId,
                request.BaseSha,
                branchName,
                request.CommitMessage,
                cancellationToken).ConfigureAwait(false);
        }
        catch (RepositoryPreparationException ex)
        {
            throw new PullRequestPublishException(redactor.Redact(ex.Message), ex);
        }

        var safeTitle = TextLimits.Truncate(redactor.Redact(request.Title.Trim()), MaxTitleLength);
        var safeBody = TextLimits.Truncate(redactor.Redact(request.Body), MaxBodyLength);

        var pullsPath = $"repos/{Uri.EscapeDataString(request.Owner)}/{Uri.EscapeDataString(request.Repo)}/pulls";
        GitHubResponse response;
        try
        {
            response = await client.PostAsync(
                pullsPath,
                new
                {
                    title = safeTitle,
                    head = pushed.BranchName,
                    @base = pushed.DefaultBranch,
                    body = safeBody,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (GitHubUnavailableException ex)
        {
            throw new PullRequestPublishException($"no se pudo abrir el PR en GitHub: {redactor.Redact(ex.Message)}", ex);
        }

        if (response.Status != HttpStatusCode.Created)
        {
            throw new PullRequestPublishException($"no se pudo abrir el PR en GitHub: {DescribeFailure(response)}");
        }

        if (ReadPullRequest(response.Body) is not { } created)
        {
            throw new PullRequestPublishException($"no se pudo abrir el PR en GitHub: respuesta sin número ni URL ({GitHubApiClient.NotRespondingReason})");
        }

        var now = timeProvider.GetUtcNow();
        var record = new PullRequestRecord
        {
            Id = Guid.CreateVersion7(now),
            RunId = request.RunId,
            Number = created.Number,
            Url = created.Url,
            BranchName = pushed.BranchName,
            CommitSha = pushed.CommitSha,
            Title = safeTitle,
            CreatedAt = now,
        };
        context.PullRequests.Add(record);
        try
        {
            // Una vez creado el PR en GitHub (201), persistir con CancellationToken.None para no dejarlo huérfano.
            await context.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            context.Entry(record).State = EntityState.Detached;
            var persisted = await context.PullRequests
                .AsNoTracking()
                .SingleAsync(p => p.RunId == request.RunId, CancellationToken.None)
                .ConfigureAwait(false);
            return ToInfo(persisted);
        }

        LogPublished(RunShortId.Of(request.RunId), created.Number, pushed.BranchName);

        // Etiqueta 'codenetix': si falla o expira, se registra warning y se conserva el PR ya persistido.
        await TryAddLabelAsync(request.RunId, request.Owner, request.Repo, created.Number).ConfigureAwait(false);

        return ToInfo(record);
    }

    private async Task TryAddLabelAsync(Guid runId, string owner, string repo, long number)
    {
        var labelsPath = string.Create(
            CultureInfo.InvariantCulture,
            $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/issues/{number}/labels");
        using var labelCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            var response = await client.PostAsync(labelsPath, new { labels = new[] { LabelName } }, labelCts.Token).ConfigureAwait(false);
            if ((int)response.Status is < 200 or >= 300)
            {
                LogLabelFailed(RunShortId.Of(runId), number, LabelName, DescribeFailure(response));
            }
        }
        catch (Exception ex) when (ex is GitHubUnavailableException or OperationCanceledException)
        {
            var reason = ex is OperationCanceledException ? "timeout al añadir etiqueta" : redactor.Redact(ex.Message);
            LogLabelFailed(RunShortId.Of(runId), number, LabelName, reason);
        }
    }

    private string DescribeFailure(GitHubResponse response)
    {
        if (response.Status == HttpStatusCode.UnprocessableEntity)
        {
            var detail = ExtractUnprocessableDetail(response.ErrorBody);
            if (!string.IsNullOrWhiteSpace(detail))
            {
                return $"{redactor.Redact(detail)} (HTTP 422)";
            }

            return "solicitud rechazada por GitHub (HTTP 422)";
        }

        return $"{GitHubApiClient.FailureReason(response)} (HTTP {(int)response.Status})";
    }

    private static string? ExtractUnprocessableDetail(JsonElement? errorBody)
    {
        if (errorBody is not { ValueKind: JsonValueKind.Object } json)
        {
            return null;
        }

        string? message = json.TryGetProperty("message", out var msgEl) && msgEl.ValueKind == JsonValueKind.String
            ? msgEl.GetString()?.Trim()
            : null;

        string? firstError = null;
        if (json.TryGetProperty("errors", out var errorsEl) && errorsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in errorsEl.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("message", out var itemMsg)
                    && itemMsg.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(itemMsg.GetString()))
                {
                    firstError = itemMsg.GetString()!.Trim();
                    break;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(message) && !string.IsNullOrWhiteSpace(firstError))
        {
            return $"{message}: {firstError}";
        }

        return !string.IsNullOrWhiteSpace(firstError) ? firstError : message;
    }

    private static CreatedPr? ReadPullRequest(JsonElement? element) =>
        element is { ValueKind: JsonValueKind.Object } json
        && json.TryGetProperty("number", out var number) && number.TryGetInt64(out var prNumber) && prNumber > 0
        && json.TryGetProperty("html_url", out var url) && url.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(url.GetString())
            ? new CreatedPr(prNumber, url.GetString()!)
            : null;

    private static PullRequestInfo ToInfo(PullRequestRecord record) => new(
        record.Number,
        record.Url,
        record.BranchName,
        record.CommitSha,
        record.Title,
        record.CreatedAt);

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is SqliteException { SqliteErrorCode: SqliteConstraint, SqliteExtendedErrorCode: 2067 or 1555 };

    private Gate Enter(Guid runId)
    {
        lock (_gatesLock)
        {
            if (!_gates.TryGetValue(runId, out var gate))
            {
                gate = new Gate();
                _gates[runId] = gate;
            }

            gate.Users++;
            return gate;
        }
    }

    private void Leave(Guid runId, Gate gate)
    {
        lock (_gatesLock)
        {
            if (--gate.Users == 0)
            {
                _gates.Remove(runId);
                gate.Semaphore.Dispose();
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Ejecución {ShortId}: PR #{Number} abierto en la rama {Branch}.")]
    private partial void LogPublished(string shortId, long number, string branch);

    [LoggerMessage(Level = LogLevel.Information, Message = "Ejecución {ShortId}: el PR #{Number} ya existía en gh_pull_requests; no se duplica.")]
    private partial void LogAlreadyPublished(string shortId, long number);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ejecución {ShortId}: PR #{Number} creado pero no se pudo añadir la etiqueta '{Label}': {Reason}.")]
    private partial void LogLabelFailed(string shortId, long number, string label, string reason);

    private sealed record CreatedPr(long Number, string Url);

    private sealed class Gate
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int Users { get; set; }
    }
}
