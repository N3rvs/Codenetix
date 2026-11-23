using System.Net;
using System.Text.Json;
using CodeNetix.Contracts;
using Microsoft.Extensions.Options;

namespace CodeNetix.GitHub;

/// <summary>
/// <c>GET /user</c> → <c>login</c> del token de la instancia. Se cachea tras la primera respuesta con
/// éxito, <b>por token</b>: si el token cambia (en serve se puede rotar desde el Panel, historia 8.1) se vuelve a
/// preguntar. Las llamadas concurrentes comparten una única consulta en curso (acotada por el timeout del
/// cliente); si falla, todas reciben el mismo error y la siguiente llamada vuelve a preguntar.
/// Con <see cref="GitHubOptions.Login"/> no se llama a GitHub: un <c>GITHUB_TOKEN</c> de Actions no puede leer <c>/user</c>.
/// </summary>
internal sealed class GitHubIdentity : IGitHubIdentity
{
    private readonly GitHubApiClient client;
    private readonly IOptions<GitHubOptions>? _options;
    private readonly Lock _lock = new();
    private readonly string? _configuredLogin;
    private InFlight? _inFlight;
    private CachedLogin? _cached;

    public GitHubIdentity(GitHubApiClient client)
    {
        this.client = client;
    }

    public GitHubIdentity(GitHubApiClient client, IOptions<GitHubOptions> options)
        : this(client)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        var configured = options.Value.Login?.Trim();
        _configuredLogin = string.IsNullOrEmpty(configured) ? null : configured;
    }

    public Task<string> GetLoginAsync(CancellationToken cancellationToken)
    {
        if (_configuredLogin is { } configured)
        {
            return Task.FromResult(configured);
        }

        var token = CurrentToken();
        if (Volatile.Read(ref _cached) is { } cached && cached.Token == token)
        {
            return Task.FromResult(cached.Login);
        }

        Task<string> lookup;
        lock (_lock)
        {
            if (_cached is { } raced && raced.Token == token)
            {
                return Task.FromResult(raced.Login);
            }

            if (_inFlight is not { } inFlight || inFlight.Token != token)
            {
                inFlight = new InFlight(token, FetchAsync(token));
                _inFlight = inFlight;
            }

            lookup = inFlight.Task;
        }

        // Cancelar una espera no cancela la consulta compartida.
        return lookup.WaitAsync(cancellationToken);
    }

    /// <summary>Token vigente (solo para la clave de la caché; nunca se registra).</summary>
    private string? CurrentToken() => _options?.Value.Token?.Trim();

    private async Task<string> FetchAsync(string? token)
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

            // Si el token cambió mientras se consultaba, la respuesta podría ser de cualquiera de los dos: no se cachea.
            if (CurrentToken() == token)
            {
                Volatile.Write(ref _cached, new CachedLogin(token, login));
            }

            return login;
        }
        finally
        {
            lock (_lock)
            {
                if (_inFlight?.Token == token)
                {
                    _inFlight = null;
                }
            }
        }
    }

    private sealed record CachedLogin(string? Token, string Login)
    {
        /// <summary>Sin el token: el <c>ToString</c> de un record lo imprimiría.</summary>
        public override string ToString() => Login;
    }

    private sealed record InFlight(string? Token, Task<string> Task)
    {
        public override string ToString() => "GET /user";
    }
}
