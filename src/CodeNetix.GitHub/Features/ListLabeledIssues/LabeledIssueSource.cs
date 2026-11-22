using System.Globalization;
using System.Net;
using System.Text.Json;
using CodeNetix.Contracts;

namespace CodeNetix.GitHub.Features.ListLabeledIssues;

/// <summary>
/// Issues etiquetadas para el sondeo (FR-6, AD-4) con la API REST de GitHub:
/// <list type="bullet">
/// <item><c>GET /repos/{o}/{r}/issues?state=open&amp;labels=…&amp;sort=updated&amp;direction=desc&amp;per_page=100</c>,
/// hasta <see cref="MaxListPages"/> páginas por <c>rel="next"</c>; la primera, condicional con <c>If-None-Match</c>. Al
/// ordenar por <c>updated</c>, cualquier issue que cambie (etiquetada o reetiquetada) sube a la primera página y cambia su
/// <c>ETag</c>.</item>
/// <item><c>GET /repos/{o}/{r}/issues/{n}/events?per_page=100</c> (API de eventos de la issue, no la de timeline: solo
/// eventos y los mismos <c>id</c>), de la última página hacia atrás, hasta <see cref="MaxEventPages"/> páginas.</item>
/// </list>
/// Nunca lanza por errores de GitHub: los devuelve como <c>Unavailable</c> con un motivo fijo (sin el token).
/// </summary>
internal sealed class LabeledIssueSource(GitHubApiClient client) : ILabeledIssueSource
{
    public const int PageSize = 100;
    public const int MaxListPages = 10;
    public const int MaxEventPages = 5;
    public const string RepositoryNotFoundReason = "el repositorio no existe o el token no puede verlo";

    public async Task<LabeledIssueList> ListOpenAsync(string owner, string repo, string label, string? etag, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        var basePath = string.Create(
            CultureInfo.InvariantCulture,
            $"{RepoPath(owner, repo)}/issues?state=open&labels={Uri.EscapeDataString(label.Trim())}&sort=updated&direction=desc&per_page={PageSize}");
        var issues = new List<LabeledIssue>();
        var seen = new HashSet<long>();
        string? firstETag = null;

        for (var page = 1; page <= MaxListPages; page++)
        {
            var path = string.Create(CultureInfo.InvariantCulture, $"{basePath}&page={page}");
            GitHubResponse response;
            try
            {
                response = page == 1
                    ? await client.GetAsync(path, etag, cancellationToken).ConfigureAwait(false)
                    : await client.GetAsync(path, cancellationToken).ConfigureAwait(false);
            }
            catch (GitHubUnavailableException ex)
            {
                return LabeledIssueList.Unavailable(new GitHubQueryFailure(ex.Message, false, null));
            }

            if (page == 1 && response.Status == HttpStatusCode.NotModified)
            {
                return LabeledIssueList.NotModified(response.ETag ?? etag);
            }

            if (response.Status != HttpStatusCode.OK)
            {
                return LabeledIssueList.Unavailable(Failure(response, RepositoryNotFoundReason));
            }

            if (response.Body is not { ValueKind: JsonValueKind.Array } items)
            {
                return LabeledIssueList.Unavailable(new GitHubQueryFailure(GitHubApiClient.NotRespondingReason, false, null));
            }

            if (page == 1)
            {
                firstETag = response.ETag;
            }

            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || (item.TryGetProperty("pull_request", out var pullRequest) && pullRequest.ValueKind == JsonValueKind.Object)
                    || !item.TryGetProperty("number", out var numberElement)
                    || !numberElement.TryGetInt64(out var number)
                    || number <= 0
                    || !seen.Add(number))
                {
                    continue;
                }

                var updatedAt = item.TryGetProperty("updated_at", out var updated) && updated.ValueKind == JsonValueKind.String
                    ? updated.GetString() ?? string.Empty
                    : string.Empty;
                issues.Add(new LabeledIssue(number, updatedAt));
            }

            if (!GitHubLinkHeader.HasNext(response.Link))
            {
                // El ETag de la primera página solo describe la lista entera si no hay más páginas: con varias, un 304
                // de la primera no garantiza que las demás no hayan cambiado, así que no se devuelve.
                return new LabeledIssueList(LabeledIssueListStatus.Ok, issues, page == 1 ? firstETag : null, false, null);
            }
        }

        // Más de MaxListPages páginas: lista incompleta (las más recientes primero) y sin ETag, para no fiarse de un 304.
        return new LabeledIssueList(LabeledIssueListStatus.Ok, issues, null, true, null);
    }

    public async Task<LabelEventLookup> FindLatestLabelEventAsync(string owner, string repo, long issueNumber, string label, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(issueNumber);

        var wanted = label.Trim();
        var basePath = string.Create(CultureInfo.InvariantCulture, $"{RepoPath(owner, repo)}/issues/{issueNumber}/events?per_page={PageSize}");
        try
        {
            var first = await client.GetAsync($"{basePath}&page=1", cancellationToken).ConfigureAwait(false);
            if (first.Status is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                return LabelEventLookup.NotFound;
            }

            if (!IsPage(first, out var failure))
            {
                return LabelEventLookup.Unavailable(failure!);
            }

            var lastPage = GitHubLinkHeader.LastPage(first.Link) ?? 1;
            var firstPage = Math.Max(1, lastPage - MaxEventPages + 1);
            for (var page = lastPage; page >= firstPage; page--)
            {
                var response = page == 1
                    ? first
                    : await client.GetAsync(string.Create(CultureInfo.InvariantCulture, $"{basePath}&page={page}"), cancellationToken).ConfigureAwait(false);
                if (page != 1 && !IsPage(response, out failure))
                {
                    return LabelEventLookup.Unavailable(failure!);
                }

                // GitHub los lista en orden cronológico: el más reciente es el último.
                var items = response.Body!.Value.EnumerateArray().ToList();
                for (var i = items.Count - 1; i >= 0; i--)
                {
                    if (ReadLabelEvent(items[i], wanted) is { } found)
                    {
                        return LabelEventLookup.Found(found);
                    }
                }
            }

            return LabelEventLookup.NotFound;
        }
        catch (GitHubUnavailableException ex)
        {
            return LabelEventLookup.Unavailable(new GitHubQueryFailure(ex.Message, false, null));
        }
    }

    private static LabelEvent? ReadLabelEvent(JsonElement item, string wanted)
    {
        if (item.ValueKind != JsonValueKind.Object
            || !item.TryGetProperty("event", out var kind)
            || kind.ValueKind != JsonValueKind.String
            || !string.Equals(kind.GetString(), "labeled", StringComparison.Ordinal)
            || !item.TryGetProperty("label", out var labelElement)
            || labelElement.ValueKind != JsonValueKind.Object
            || !labelElement.TryGetProperty("name", out var name)
            || name.ValueKind != JsonValueKind.String
            || !string.Equals(name.GetString()?.Trim(), wanted, StringComparison.OrdinalIgnoreCase)
            || !item.TryGetProperty("id", out var idElement)
            || !idElement.TryGetInt64(out var id)
            || id <= 0)
        {
            return null;
        }

        var actor = item.TryGetProperty("actor", out var actorElement)
            && actorElement.ValueKind == JsonValueKind.Object
            && actorElement.TryGetProperty("login", out var login)
            && login.ValueKind == JsonValueKind.String
                ? login.GetString()
                : null;

        DateTimeOffset? createdAt = item.TryGetProperty("created_at", out var created)
            && created.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(created.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed.ToUniversalTime()
                : null;

        return new LabelEvent(id, actor, createdAt);
    }

    private static bool IsPage(GitHubResponse response, out GitHubQueryFailure? failure)
    {
        if (response.Status != HttpStatusCode.OK)
        {
            failure = Failure(response, IssueReader.NotFoundReason);
            return false;
        }

        if (response.Body is not { ValueKind: JsonValueKind.Array })
        {
            failure = new GitHubQueryFailure(GitHubApiClient.NotRespondingReason, false, null);
            return false;
        }

        failure = null;
        return true;
    }

    private static GitHubQueryFailure Failure(GitHubResponse response, string notFoundReason)
    {
        var reason = (response.Status is HttpStatusCode.NotFound or HttpStatusCode.Gone) && !response.RateLimited
            ? notFoundReason
            : GitHubApiClient.FailureReason(response);
        return new GitHubQueryFailure(
            string.Create(CultureInfo.InvariantCulture, $"{reason} (HTTP {(int)response.Status})"),
            response.RateLimited,
            response.RetryAfter);
    }

    private static string RepoPath(string owner, string repo) =>
        $"repos/{Uri.EscapeDataString(owner.Trim())}/{Uri.EscapeDataString(repo.Trim())}";
}
