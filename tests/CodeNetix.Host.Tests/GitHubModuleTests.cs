using System.Diagnostics;
using CodeNetix.Contracts;
using CodeNetix.GitHub;
using Microsoft.Extensions.Options;

namespace CodeNetix.Host.Tests;

/// <summary>Módulo GitHub contra la API falsa: identidad, permisos, timeout y que el token no se filtre.</summary>
public sealed class GitHubModuleTests : IAsyncLifetime
{
    private const string Token = "ghp_moduletesttoken0123456789abcdef";

    private FakeGitHub _gitHub = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _gitHub = await FakeGitHub.StartAsync(Ct);

    public async ValueTask DisposeAsync() => await _gitHub.DisposeAsync();

    private GitHubApiClient Client(TimeSpan? timeout = null, string? apiUrl = null, string? token = Token) => new(
        Options.Create(new GitHubOptions { Token = token, ApiUrl = apiUrl ?? _gitHub.BaseUrl }),
        timeout ?? GitHubApiClient.DefaultTimeout);

    [Fact]
    public void Defaults_point_to_github_com_with_a_10_second_timeout()
    {
        Assert.Equal("https://api.github.com", new GitHubOptions().ApiUrl);
        Assert.Equal(TimeSpan.FromSeconds(10), GitHubApiClient.DefaultTimeout);
    }

    [Theory]
    [InlineData("admin", WriteAccessStatus.Granted)]
    [InlineData("maintain", WriteAccessStatus.Granted)]
    [InlineData("write", WriteAccessStatus.Granted)]
    [InlineData("read", WriteAccessStatus.Denied)]
    [InlineData("triage", WriteAccessStatus.Denied)]
    [InlineData("none", WriteAccessStatus.Denied)]
    public async Task Permission_maps_to_write_access(string permission, WriteAccessStatus expected)
    {
        _gitHub.Permission = permission;
        using var client = Client();

        var access = await new RepositoryPermissions(client).GetWriteAccessAsync("o", "r", "bot", Ct);

        Assert.Equal(expected, access.Status);
    }

    [Theory]
    [InlineData(404, WriteAccessStatus.Denied, null)]
    [InlineData(401, WriteAccessStatus.Unavailable, "el token de GitHub no es válido o no tiene permisos")]
    [InlineData(403, WriteAccessStatus.Unavailable, "el token de GitHub no es válido o no tiene permisos")]
    [InlineData(500, WriteAccessStatus.Unavailable, "GitHub no responde")]
    [InlineData(502, WriteAccessStatus.Unavailable, "GitHub no responde")]
    [InlineData(422, WriteAccessStatus.Unavailable, "GitHub no responde")]
    [InlineData(429, WriteAccessStatus.Unavailable, GitHubApiClient.RateLimitedReason)]
    public async Task Permission_status_codes_map_to_write_access(int status, WriteAccessStatus expected, string? reason)
    {
        _gitHub.PermissionStatus = status;
        using var client = Client();

        var access = await new RepositoryPermissions(client).GetWriteAccessAsync("o", "r", "bot", Ct);

        Assert.Equal(new WriteAccess(expected, reason), access);
    }

    [Fact]
    public async Task Unknown_permission_is_Denied()
    {
        _gitHub.Permission = "custom";
        using var client = Client();

        Assert.Equal(WriteAccess.Denied, await new RepositoryPermissions(client).GetWriteAccessAsync("o", "r", "bot", Ct));
    }

    [Fact]
    public async Task Ok_without_permission_field_is_Unavailable()
    {
        _gitHub.OmitPermission = true;
        using var client = Client();

        var access = await new RepositoryPermissions(client).GetWriteAccessAsync("o", "r", "bot", Ct);

        Assert.Equal(WriteAccess.Unavailable("GitHub no responde"), access);
    }

    [Fact]
    public async Task Forbidden_with_exhausted_rate_limit_is_rate_limited_not_invalid_token()
    {
        _gitHub.PermissionStatus = 403;
        _gitHub.UserStatus = 403;
        _gitHub.RateLimitExhausted = true;
        using var client = Client();

        var access = await new RepositoryPermissions(client).GetWriteAccessAsync("o", "r", "bot", Ct);
        var ex = await Assert.ThrowsAsync<GitHubUnavailableException>(() => new GitHubIdentity(client).GetLoginAsync(Ct));

        Assert.Equal(WriteAccess.Unavailable(GitHubApiClient.RateLimitedReason), access);
        Assert.Equal(GitHubApiClient.RateLimitedReason, ex.Message);
    }

    [Fact]
    public async Task Too_many_requests_on_user_is_rate_limited()
    {
        _gitHub.UserStatus = 429;
        using var client = Client();

        var ex = await Assert.ThrowsAsync<GitHubUnavailableException>(() => new GitHubIdentity(client).GetLoginAsync(Ct));

        Assert.Equal("GitHub ha limitado las peticiones (rate limit); vuelve a intentarlo más tarde", ex.Message);
    }

    [Fact]
    public async Task Same_host_redirect_is_followed_once_with_the_auth_headers()
    {
        _gitHub.Redirects["o/r"] = "/repos/o/new/collaborators/bot/permission";
        using var client = Client();

        var access = await new RepositoryPermissions(client).GetWriteAccessAsync("o", "r", "bot", Ct);

        Assert.Equal(WriteAccess.Granted, access);
        Assert.Equal(["permission:o/r:bot", "permission:o/new:bot"], _gitHub.Requests.Select(r => r.Kind));
        Assert.All(_gitHub.Requests, r => Assert.Equal("Bearer " + Token, r.Authorization));
    }

    [Fact]
    public async Task Redirect_to_another_host_is_Unavailable_and_not_followed()
    {
        // Mismo servidor, otro host ("localhost" frente a "127.0.0.1"): no se sigue ni se envía el token.
        var port = new Uri(_gitHub.BaseUrl).Port;
        _gitHub.Redirects["o/r"] = $"http://localhost:{port}/repos/o/new/collaborators/bot/permission";
        using var client = Client();

        var access = await new RepositoryPermissions(client).GetWriteAccessAsync("o", "r", "bot", Ct);

        Assert.Equal(WriteAccess.Unavailable("GitHub no responde"), access);
        Assert.Single(_gitHub.Requests);
    }

    [Fact]
    public async Task Redirect_loop_is_followed_only_once()
    {
        _gitHub.Redirects["o/r"] = "/repos/o/r2/collaborators/bot/permission";
        _gitHub.Redirects["o/r2"] = "/repos/o/r3/collaborators/bot/permission";
        using var client = Client();

        var access = await new RepositoryPermissions(client).GetWriteAccessAsync("o", "r", "bot", Ct);

        Assert.Equal(WriteAccess.Unavailable("GitHub no responde"), access);
        Assert.Equal(2, _gitHub.Requests.Count);
    }

    [Fact]
    public async Task Concurrent_callers_during_a_slow_failure_share_a_single_user_call()
    {
        _gitHub.UserStatus = 500;
        _gitHub.Delay = TimeSpan.FromMilliseconds(500);
        using var client = Client();
        var identity = new GitHubIdentity(client);

        var calls = Enumerable.Range(0, 6).Select(_ => identity.GetLoginAsync(Ct)).ToList();
        foreach (var call in calls)
        {
            var ex = await Assert.ThrowsAsync<GitHubUnavailableException>(() => call);
            Assert.Equal("GitHub no responde", ex.Message);
        }

        Assert.Equal(1, _gitHub.UserCalls);

        // Tras el fallo compartido, la siguiente llamada vuelve a preguntar una vez.
        _gitHub.UserStatus = 200;
        _gitHub.Delay = TimeSpan.Zero;
        Assert.Equal(FakeGitHub.DefaultLogin, await identity.GetLoginAsync(Ct));
        Assert.Equal(2, _gitHub.UserCalls);
    }

    [Fact]
    public async Task Timeout_is_GitHub_no_responde()
    {
        _gitHub.Delay = TimeSpan.FromSeconds(5);
        using var client = Client(timeout: TimeSpan.FromMilliseconds(200));

        var stopwatch = Stopwatch.StartNew();
        var access = await new RepositoryPermissions(client).GetWriteAccessAsync("o", "r", "bot", Ct);
        var ex = await Assert.ThrowsAsync<GitHubUnavailableException>(() => new GitHubIdentity(client).GetLoginAsync(Ct));

        Assert.Equal(WriteAccess.Unavailable("GitHub no responde"), access);
        Assert.Equal("GitHub no responde", ex.Message);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(4), $"Tardó {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task Identity_returns_the_login_and_caches_it()
    {
        _gitHub.Login = "octo-bot";
        using var client = Client();
        var identity = new GitHubIdentity(client);

        var first = await identity.GetLoginAsync(Ct);
        var second = await identity.GetLoginAsync(Ct);

        Assert.Equal(("octo-bot", "octo-bot"), (first, second));
        Assert.Equal(1, _gitHub.UserCalls);
    }

    [Fact]
    public async Task Concurrent_identity_calls_hit_user_once()
    {
        using var client = Client();
        var identity = new GitHubIdentity(client);

        var logins = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => identity.GetLoginAsync(Ct)));

        Assert.All(logins, l => Assert.Equal(FakeGitHub.DefaultLogin, l));
        Assert.Equal(1, _gitHub.UserCalls);
    }

    [Fact]
    public async Task Missing_token_is_unavailable_without_calling_GitHub()
    {
        using var client = Client(token: null);

        var ex = await Assert.ThrowsAsync<GitHubUnavailableException>(() => new GitHubIdentity(client).GetLoginAsync(Ct));

        Assert.Equal("el token de GitHub no es válido o no tiene permisos", ex.Message);
        Assert.Equal(0, _gitHub.TotalCalls);
    }

    [Fact]
    public async Task Requests_carry_the_GitHub_headers()
    {
        using var client = Client();

        await new GitHubIdentity(client).GetLoginAsync(Ct);

        var request = Assert.Single(_gitHub.Requests);
        Assert.Equal("Bearer " + Token, request.Authorization);
        Assert.Equal("application/vnd.github+json", request.Accept);
        Assert.Equal("2022-11-28", request.ApiVersion);
    }

    [Fact]
    public async Task ApiUrl_with_a_path_prefix_is_respected()
    {
        // GitHub Enterprise: https://ghe.example.com/api/v3 -> la ruta se resuelve bajo el prefijo.
        using var client = Client(apiUrl: _gitHub.BaseUrl + FakeGitHub.EnterprisePrefix);
        var identity = new GitHubIdentity(client);

        var login = await identity.GetLoginAsync(Ct);
        var access = await new RepositoryPermissions(client).GetWriteAccessAsync("o", "r", login, Ct);

        Assert.Equal(FakeGitHub.DefaultLogin, login);
        Assert.Equal(WriteAccess.Granted, access);
        Assert.Equal(2, _gitHub.TotalCalls);
        Assert.All(_gitHub.Requests, r => Assert.StartsWith(FakeGitHub.EnterprisePrefix + "/", r.Path, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Network_errors_do_not_leak_the_token()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        using var client = Client(apiUrl: $"http://127.0.0.1:{port}");

        var ex = await Assert.ThrowsAsync<GitHubUnavailableException>(() => new GitHubIdentity(client).GetLoginAsync(Ct));

        Assert.Equal("GitHub no responde", ex.Message);
        Assert.DoesNotContain(Token, ex.ToString(), StringComparison.Ordinal);
        Assert.Null(ex.InnerException);
    }
}
