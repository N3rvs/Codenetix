using System.Net;
using System.Text.Json;
using CodeNetix.Contracts;

namespace CodeNetix.GitHub;

/// <summary>
/// <c>GET /repos/{owner}/{repo}/collaborators/{login}/permission</c>: <c>admin</c>, <c>maintain</c> o
/// <c>write</c> → <c>Granted</c>; <c>read</c>, <c>triage</c>, <c>none</c> u otro valor → <c>Denied</c>;
/// 404 (repositorio invisible o no colaborador) → <c>Denied</c>; 401/403, red o 5xx → <c>Unavailable</c>.
/// </summary>
internal sealed class RepositoryPermissions(GitHubApiClient client) : IRepositoryPermissions
{
    private static readonly HashSet<string> WritePermissions = new(StringComparer.OrdinalIgnoreCase) { "admin", "maintain", "write" };

    public async Task<WriteAccess> GetWriteAccessAsync(string owner, string repo, string login, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);
        ArgumentException.ThrowIfNullOrWhiteSpace(login);

        var path = $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/collaborators/{Uri.EscapeDataString(login)}/permission";

        GitHubResponse response;
        try
        {
            response = await client.GetAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (GitHubUnavailableException ex)
        {
            return WriteAccess.Unavailable(ex.Message);
        }

        if (response.Status == HttpStatusCode.NotFound)
        {
            return WriteAccess.Denied;
        }

        if (response.Status != HttpStatusCode.OK)
        {
            return WriteAccess.Unavailable(GitHubApiClient.FailureReason(response)) with { RateLimited = response.RateLimited };
        }

        if (response.Body is not { ValueKind: JsonValueKind.Object } json
            || !json.TryGetProperty("permission", out var permission)
            || permission.ValueKind != JsonValueKind.String)
        {
            return WriteAccess.Unavailable(GitHubApiClient.NotRespondingReason);
        }

        return WritePermissions.Contains(permission.GetString()!) ? WriteAccess.Granted : WriteAccess.Denied;
    }
}
