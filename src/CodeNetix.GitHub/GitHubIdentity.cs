using System.Net;
using System.Text.Json;
using CodeNetix.Contracts;

namespace CodeNetix.GitHub;

/// <summary>
/// <c>GET /user</c> → <c>login</c> del token de la instancia. Se cachea tras la primera respuesta con
/// éxito. Las llamadas concurrentes comparten una única consulta en curso (acotada por el timeout del
/// cliente); si falla, todas reciben el mismo error y la siguiente llamada vuelve a preguntar.
/// </summary>
internal sealed class GitHubIdentity(GitHubApiClient client) : IGitHubIdentity
{
    private readonly Lock _lock = new();
    private Task<string>? _inFlight;
    private string? _login;

    public Task<string> GetLoginAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _login) is { } cached)
        {
            return Task.FromResult(cached);
        }

        Task<string> lookup;
        lock (_lock)
        {
            if (_login is { } raced)
            {
                return Task.FromResult(raced);
            }

            lookup = _inFlight ??= FetchAsync();
        }

        // Cancelar una espera no cancela la consulta compartida.
        return lookup.WaitAsync(cancellationToken);
    }

    private async Task<string> FetchAsync()
    {
        // Siempre asíncrono: así _inFlight queda asignado antes de que el finally lo limpie.
        await Task.Yield();
        try
        {
            var response = await client.GetAsync("user", CancellationToken.None).ConfigureAwait(false);
            if (response.Status != HttpStatusCode.OK)
            {
                throw new GitHubUnavailableException(GitHubApiClient.FailureReason(response));
            }

            if (response.Body is not { ValueKind: JsonValueKind.Object } json
                || !json.TryGetProperty("login", out var loginElement)
                || loginElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(loginElement.GetString()))
            {
                throw new GitHubUnavailableException(GitHubApiClient.NotRespondingReason);
            }

            var login = loginElement.GetString()!;
            Volatile.Write(ref _login, login);
            return login;
        }
        finally
        {
            lock (_lock)
            {
                _inFlight = null;
            }
        }
    }
}
