using System.Net;
using System.Text.Json;
using CodeNetix.Contracts;

namespace CodeNetix.GitHub;

/// <summary>
/// <c>GET /repos/{owner}/{repo}/issues/{number}</c> → título y cuerpo como <see cref="UntrustedText"/>.
/// 404/410 → la issue no existe o no es visible; el resto de errores, con el motivo de <see cref="GitHubApiClient"/>.
/// Un pull request también es una issue para esta API: se rechaza.
/// </summary>
internal sealed class IssueReader(GitHubApiClient client) : IIssueReader
{
    public const string NotFoundReason = "la issue no existe o el token no puede verla";
    public const string PullRequestReason = "el objeto es un pull request, no una issue";

    public async Task<GitHubIssue> GetAsync(string owner, string repo, long number, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(number);

        var path = $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/issues/{number.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        var response = await client.GetAsync(path, cancellationToken).ConfigureAwait(false);

        if (response.Status is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            throw new GitHubUnavailableException($"{NotFoundReason} (HTTP {(int)response.Status})");
        }

        if (response.Status != HttpStatusCode.OK)
        {
            throw new GitHubUnavailableException($"{GitHubApiClient.FailureReason(response)} (HTTP {(int)response.Status})");
        }

        if (response.Body is not { ValueKind: JsonValueKind.Object } json
            || !json.TryGetProperty("title", out var title)
            || title.ValueKind != JsonValueKind.String)
        {
            throw new GitHubUnavailableException(GitHubApiClient.NotRespondingReason);
        }

        if (json.TryGetProperty("pull_request", out var pullRequest) && pullRequest.ValueKind == JsonValueKind.Object)
        {
            throw new GitHubUnavailableException(PullRequestReason);
        }

        // Una issue sin descripción tiene "body": null.
        var body = json.TryGetProperty("body", out var bodyElement) && bodyElement.ValueKind == JsonValueKind.String
            ? bodyElement.GetString()!
            : string.Empty;

        return new GitHubIssue(UntrustedText.From(title.GetString()!), UntrustedText.From(body));
    }
}
