using System.Collections.Concurrent;
using System.Diagnostics;
using System.Formats.Tar;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeNetix.Contracts;
using CodeNetix.GitHub;
using CodeNetix.Sandbox;
using CodeNetix.Sandbox.Docker;
using CodeNetix.Sandbox.Gateway;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CodeNetix.Host.Tests;

/// <summary>
/// Historia 2.5 (FR-27, NFR-1): prueba de fuego de seguridad de extremo a extremo contra Docker real.
/// Ejecuta un repositorio hostil .NET 10 (100 % offline) dentro del Sandbox para demostrar que el código
/// solo ve la key efímera <c>cnx-run-&lt;runId&gt;-&lt;nonce&gt;</c>, nunca encuentra <c>.git/config</c> ni credenciales
/// reales, la salida TCP directa falla por la red <c>internal</c> y el intento de salida vía <c>HTTPS_PROXY</c> hacia
/// <c>evil.example.com:443</c> se rechaza con <c>403 Forbidden</c> registrando <see cref="EgressBlocked"/>.
/// </summary>
[Trait("Category", "SecurityFire")]
public sealed class SandboxSecurityFireTests(SandboxDockerFixture fixture) : IAsyncLifetime, IClassFixture<SandboxDockerFixture>
{
    private const string TestLabel = "codenetix.test";
    private const string GitHubToken = "ghp_hostilesecret0123456789abcdefghijk";
    private const string RealProviderKey = "sk-ant-realprovidersecret0123456789abcdef";
    private const string Owner = "secfire";
    private const string EvilTarget = "evil.example.com:443";

    private readonly string _marker = "secfire-" + Guid.NewGuid().ToString("N")[..12];
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codenetix-secfire-" + Guid.NewGuid().ToString("N"));
    private readonly LocalGitRepos _repos = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DockerClient Docker => fixture.Docker;

    public ValueTask InitializeAsync()
    {
        if (fixture.Unavailable is { } reason)
        {
            Assert.Skip(reason);
        }

        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Hostile_repo_inside_real_sandbox_cannot_exfiltrate_secrets_read_git_config_or_reach_external_hosts()
    {
        var sink = new SecurityFireEventSink();
        var gateway = new SandboxGatewayRegistry();
        var proxyOptions = new StaticOptionsMonitor<SandboxOptions>(new SandboxOptions
        {
            ConnectProxyPort = 0,
            AllowedNuGetHosts = [SandboxOptions.DefaultNuGetHost],
        });

        await using var proxy = new NuGetConnectProxy(gateway, sink, proxyOptions, NullLogger<NuGetConnectProxy>.Instance);
        await proxy.StartAsync(Ct);
        try
        {
            Assert.True(proxy.BoundPort > 0);

            var repo = "hostile-" + Guid.NewGuid().ToString("N")[..8];
            _repos.Create(Owner, repo, HostileProbeRepo.Files);
            var workspace = new RepositoryWorkspace(
                Options.Create(new GitHubOptions
                {
                    Token = GitHubToken,
                    CloneBaseUrl = _repos.BaseUrl,
                    WorkspacesDirectory = Path.Combine(_root, "workspaces"),
                }),
                NullLogger<RepositoryWorkspace>.Instance);
            var runId = Guid.CreateVersion7();
            await workspace.PrepareAsync(runId, Owner, repo, Ct);

            // Confirmar que el clon del host sí contiene .git/config antes de exportar al Sandbox.
            Assert.True(File.Exists(Path.Combine(workspace.WorkspacePathOf(runId), ".git", "config")));

            var relayContainerName = await StartGatewayRelayIfNeededAsync(proxy.BoundPort, Ct);

            var options = new SandboxOptions
            {
                Image = SandboxDockerFixture.Image,
                DockerEndpoint = SandboxDockerFixture.Endpoint,
                HostContainer = relayContainerName,
                ConnectProxyPort = proxy.BoundPort,
                AllowedNuGetHosts = [SandboxOptions.DefaultNuGetHost],
                Coder = new SandboxCoderEndpoint("Anthropic", "https://api.anthropic.com", RealProviderKey, "claude-sonnet-4-5"),
            };
            options.ExtraLabels[TestLabel] = _marker;

            var manager = new SandboxManager(Docker, workspace, Options.Create(options), NullLoggerFactory.Instance, gateway);
            var session = await manager.CreateForRunAsync(runId, Ct);
            string syntheticToken;
            try
            {
                var exec = await session.ExecAsync(
                    new SandboxCommand("dotnet", ["run", "--project", "/workspace/HostileProbe.csproj"])
                    {
                        Timeout = TimeSpan.FromMinutes(2),
                    },
                    Ct);

                Assert.True(exec.ExitCode == 0, $"La sonda hostil falló (exit={exec.ExitCode}).\nSTDOUT:\n{exec.StdOut}\nSTDERR:\n{exec.StdErr}");

                var report = HostileProbeRepo.ParseReport(exec.StdOut);

                // 1) ANTHROPIC_API_KEY solo contiene la key efímera cnx-run-<runId>-<nonce>; entorno de proxy configurado y sin secretos reales.
                Assert.NotNull(report.AnthropicApiKey);
                syntheticToken = report.AnthropicApiKey!;
                Assert.Matches($"^cnx-run-{runId:D}-[0-9a-f]{{32}}$", syntheticToken);
                Assert.NotNull(gateway.TryAuthenticate(syntheticToken));
                Assert.Equal($"http://gateway:{SandboxOptions.DefaultLlmGatewayPort}/llm", report.AnthropicBaseUrl);
                Assert.Equal($"http://gateway:{proxy.BoundPort}", report.HttpProxy);
                Assert.Equal($"http://gateway:{proxy.BoundPort}", report.HttpsProxy);
                Assert.Equal("gateway,localhost,127.0.0.1", report.NoProxy);

                foreach (var (key, value) in report.Environment)
                {
                    Assert.DoesNotContain(RealProviderKey, key, StringComparison.Ordinal);
                    Assert.DoesNotContain(RealProviderKey, value, StringComparison.Ordinal);
                    Assert.DoesNotContain(GitHubToken, key, StringComparison.Ordinal);
                    Assert.DoesNotContain(GitHubToken, value, StringComparison.Ordinal);
                    Assert.DoesNotContain("sk-ant-", value, StringComparison.Ordinal);
                    Assert.DoesNotContain("ghp_", value, StringComparison.Ordinal);
                }

                // 2) .git/config, .gitconfig y .git-credentials no existen en /workspace, ancestros ni $HOME, y ningún archivo contiene credenciales.
                Assert.Empty(report.GitPathsFound);
                Assert.Empty(report.SecretMatchesInWorkspace);

                // 3) Egress directo a IP externa (1.1.1.1:443) falla por la red Docker internal.
                Assert.False(report.DirectTcpSucceeded);
                Assert.False(string.IsNullOrWhiteSpace(report.DirectTcpError));

                // 4) Egress vía HTTPS_PROXY hacia evil.example.com:443 rechazado con 403 Forbidden + RunEvent EgressBlocked.
                Assert.False(report.ProxyHttpSucceeded);
                if (report.ProxyHttpStatusCode is { } statusCode)
                {
                    Assert.Equal(403, statusCode);
                }
                else
                {
                    Assert.Contains("status code '403'", report.ProxyHttpError ?? string.Empty, StringComparison.OrdinalIgnoreCase);
                }

                var blocked = await SecurityFireHelpers.WaitForBlockedAsync(sink, EvilTarget, Ct);
                Assert.Equal(runId, blocked.RunId);
                var payload = Assert.IsType<EgressBlocked>(blocked.Payload);
                Assert.Equal(EvilTarget, payload.Target);
                Assert.Contains("lista permitida de NuGet", payload.Reason, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                await session.DisposeAsync();
            }

            // 5) Al destruir la sesión se revoca la key efímera en el Gateway.
            Assert.Null(gateway.TryAuthenticate(syntheticToken));
        }
        finally
        {
            await proxy.StopAsync(Ct);
        }
    }

    /// <summary>
    /// En Docker Desktop (Windows/macOS) la IP de la puerta de enlace del bridge pertenece a la VM Linux y no al host;
    /// levantamos un contenedor mínimo etiquetado con <see cref="_marker"/> como <c>HostContainer</c> para que
    /// <see cref="SandboxManager"/> lo conecte con el alias <c>gateway</c> y reenvíe el tráfico al <see cref="NuGetConnectProxy"/>
    /// real del host. En Linux nativo el bridge alcanza al host directamente y no hace falta relevo.
    /// </summary>
    private async Task<string?> StartGatewayRelayIfNeededAsync(int targetPort, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsLinux())
        {
            return null;
        }

        var relayName = "codenetix-test-gw-" + _marker;
        var portText = targetPort.ToString(CultureInfo.InvariantCulture);
        var script =
            $"const net=require('net'),fs=require('fs');" +
            $"net.createServer(c=>{{const u=net.connect({portText},'host.docker.internal');" +
            $"c.pipe(u);u.pipe(c);" +
            $"c.on('error',()=>u.destroy());u.on('error',()=>c.destroy());}})" +
            $".listen({portText},'0.0.0.0',()=>fs.writeFileSync('/tmp/relay-ready','1'));";

        var body = new JsonObject
        {
            ["Image"] = SandboxDockerFixture.Image,
            ["Cmd"] = new JsonArray("node", "-e", script),
            ["User"] = SandboxManager.SandboxUser,
            ["Labels"] = new JsonObject
            {
                [SandboxManager.ManagedLabel] = "true",
                [TestLabel] = _marker,
            },
            ["HostConfig"] = new JsonObject
            {
                ["ExtraHosts"] = new JsonArray("host.docker.internal:host-gateway"),
                ["AutoRemove"] = false,
            },
        };

        var relayId = await Docker.CreateContainerAsync(relayName, body, cancellationToken);
        await Docker.StartContainerAsync(relayId, cancellationToken);
        await WaitForRelayReadyAsync(relayId, cancellationToken);
        return relayName;
    }

    private async Task WaitForRelayReadyAsync(string relayId, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(5))
        {
            using var inspect = await Docker.InspectContainerAsync(relayId, cancellationToken);
            var running = inspect is not null
                && inspect.RootElement.TryGetProperty("State", out var state)
                && state.TryGetProperty("Running", out var runningProp)
                && runningProp.GetBoolean();

            if (running && await IsRelayFileReadyAsync(relayId, cancellationToken))
            {
                return;
            }

            await Task.Delay(50, cancellationToken);
        }

        throw new TimeoutException($"El contenedor relevo '{relayId}' no estuvo listo en 5 s.");
    }

    private async Task<bool> IsRelayFileReadyAsync(string relayId, CancellationToken cancellationToken)
    {
        try
        {
            var execId = await Docker.CreateExecAsync(
                relayId,
                new JsonObject
                {
                    ["AttachStdout"] = false,
                    ["AttachStderr"] = false,
                    ["Tty"] = false,
                    ["User"] = SandboxManager.SandboxUser,
                    ["Cmd"] = new JsonArray("test", "-f", "/tmp/relay-ready"),
                },
                cancellationToken);
            using var response = await Docker.StartExecAsync(execId, cancellationToken);
            _ = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            var (running, exitCode) = await Docker.InspectExecAsync(execId, cancellationToken);
            return !running && exitCode == 0;
        }
        catch (DockerApiException)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (fixture.Unavailable is null)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                try
                {
                    foreach (var id in await Docker.ListContainerIdsAsync($"{TestLabel}={_marker}", timeout.Token))
                    {
                        try
                        {
                            await Docker.RemoveContainerAsync(id, timeout.Token);
                        }
                        catch (Exception ex) when (ex is DockerApiException or HttpRequestException or OperationCanceledException)
                        {
                            // Limpieza de mejor esfuerzo por contenedor.
                        }
                    }
                }
                catch (Exception ex) when (ex is DockerApiException or HttpRequestException or OperationCanceledException)
                {
                    // Limpieza de mejor esfuerzo al listar contenedores.
                }

                try
                {
                    foreach (var name in await Docker.ListVolumeNamesAsync($"{TestLabel}={_marker}", timeout.Token))
                    {
                        try
                        {
                            await Docker.RemoveVolumeAsync(name, timeout.Token);
                        }
                        catch (Exception ex) when (ex is DockerApiException or HttpRequestException or OperationCanceledException)
                        {
                            // Limpieza de mejor esfuerzo por volumen.
                        }
                    }
                }
                catch (Exception ex) when (ex is DockerApiException or HttpRequestException or OperationCanceledException)
                {
                    // Limpieza de mejor esfuerzo al listar volúmenes.
                }
            }
        }
        finally
        {
            _repos.Dispose();
            LocalGitRepos.DeleteDirectory(_root);
        }
    }
}

/// <summary>
/// Suite determinista sin Docker de la Historia 2.5 (FR-27, NFR-1): valida el escenario hostil completo usando
/// <see cref="LocalGitRepos"/>, <see cref="SandboxManager"/> sobre <see cref="FakeDockerEngine"/> y el
/// <see cref="NuGetConnectProxy"/> real para garantizar cobertura continua en entornos sin daemon Docker.
/// </summary>
[Trait("Category", "SecurityFire")]
public sealed class SandboxSecurityFireOfflineTests : IAsyncLifetime
{
    private const string GitHubToken = "ghp_hostilesecret0123456789abcdefghijk";
    private const string RealProviderKey = "sk-ant-realprovidersecret0123456789abcdef";
    private const string Owner = "secfire-offline";
    private const string EvilTarget = "evil.example.com:443";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "codenetix-secfire-offline-" + Guid.NewGuid().ToString("N"));
    private readonly LocalGitRepos _repos = new();
    private FakeDockerEngine? _engine;
    private DockerClient? _docker;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _engine = await FakeDockerEngine.StartAsync();
        _docker = new DockerClient(DockerEndpoint.Parse(_engine.Endpoint));
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _docker?.Dispose();
            if (_engine is not null)
            {
                await _engine.DisposeAsync();
            }
        }
        finally
        {
            _repos.Dispose();
            LocalGitRepos.DeleteDirectory(_root);
        }
    }

    [Fact]
    public async Task Deterministic_security_fire_verifies_tar_isolation_ephemeral_key_revocation_and_proxy_403_with_EgressBlocked()
    {
        var sink = new SecurityFireEventSink();
        var gateway = new SandboxGatewayRegistry();
        var proxyOptions = new StaticOptionsMonitor<SandboxOptions>(new SandboxOptions
        {
            ConnectProxyPort = 0,
            AllowedNuGetHosts = [SandboxOptions.DefaultNuGetHost],
        });

        await using var proxy = new NuGetConnectProxy(gateway, sink, proxyOptions, NullLogger<NuGetConnectProxy>.Instance);
        await proxy.StartAsync(Ct);
        try
        {
            Assert.True(proxy.BoundPort > 0);

            var repo = "hostile-" + Guid.NewGuid().ToString("N")[..8];
            _repos.Create(Owner, repo, HostileProbeRepo.Files);
            var workspace = new RepositoryWorkspace(
                Options.Create(new GitHubOptions
                {
                    Token = GitHubToken,
                    CloneBaseUrl = _repos.BaseUrl,
                    WorkspacesDirectory = Path.Combine(_root, "workspaces"),
                }),
                NullLogger<RepositoryWorkspace>.Instance);
            var runId = Guid.CreateVersion7();
            await workspace.PrepareAsync(runId, Owner, repo, Ct);

            // Confirmar que el clon local del host sí tiene .git/config, para verificar que el tar lo excluye.
            Assert.True(File.Exists(Path.Combine(workspace.WorkspacePathOf(runId), ".git", "config")));

            var options = new SandboxOptions
            {
                ConnectProxyPort = proxy.BoundPort,
                AllowedNuGetHosts = [SandboxOptions.DefaultNuGetHost],
                Coder = new SandboxCoderEndpoint("Anthropic", "https://api.anthropic.com", RealProviderKey, "claude-sonnet-4-5"),
            };
            var manager = new SandboxManager(_docker!, workspace, Options.Create(options), NullLoggerFactory.Instance, gateway);

            var session = await manager.CreateForRunAsync(runId, Ct);
            string syntheticToken;
            try
            {
                // 1) La red creada es internal: true.
                var netCreate = Assert.Single(_engine!.Requests, r => r.Method == "POST" && r.Route == "/networks/create");
                Assert.True(netCreate.Json.GetProperty("Internal").GetBoolean());

                // 2) El entorno del contenedor solo lleva la key efímera y nunca secretos reales.
                var create = Assert.Single(_engine.Requests, r => r.Method == "POST" && r.Route == "/containers/create");
                var env = create.Json.GetProperty("Env").EnumerateArray().Select(e => e.GetString()!).ToList();
                var apiKeyEntry = Assert.Single(env, e => e.StartsWith("ANTHROPIC_API_KEY=", StringComparison.Ordinal));
                syntheticToken = apiKeyEntry["ANTHROPIC_API_KEY=".Length..];
                Assert.Matches($"^cnx-run-{runId:D}-[0-9a-f]{{32}}$", syntheticToken);
                Assert.Contains($"ANTHROPIC_BASE_URL=http://gateway:{SandboxOptions.DefaultLlmGatewayPort}/llm", env);
                Assert.Contains($"HTTP_PROXY=http://gateway:{proxy.BoundPort}", env);
                Assert.Contains($"HTTPS_PROXY=http://gateway:{proxy.BoundPort}", env);
                Assert.Contains("NO_PROXY=gateway,localhost,127.0.0.1", env);
                Assert.DoesNotContain(env, e => e.Contains(RealProviderKey, StringComparison.Ordinal));
                Assert.DoesNotContain(env, e => e.Contains(GitHubToken, StringComparison.Ordinal));
                Assert.DoesNotContain(env, e => e.Contains("sk-ant-", StringComparison.Ordinal));
                Assert.DoesNotContain(env, e => e.Contains("ghp_", StringComparison.Ordinal));

                var registered = gateway.TryAuthenticate(syntheticToken);
                Assert.NotNull(registered);
                Assert.Equal(runId, registered.RunId);
                Assert.Equal(_engine.ContainerIp, registered.ContainerIp);

                // 3) El tar subido a /workspace incluye el proyecto hostil pero excluye .git/config y cualquier secreto.
                var archive = Assert.Single(_engine.Requests, r => r.Method == "PUT" && r.Route.EndsWith("/archive", StringComparison.Ordinal));
                var tarEntries = ReadTar(archive.Body);
                Assert.Contains("HostileProbe.csproj", tarEntries.Keys);
                Assert.Contains("Program.cs", tarEntries.Keys);
                Assert.DoesNotContain(tarEntries.Keys, k => k.Split('/').Contains(".git"));
                Assert.DoesNotContain(tarEntries.Values, content => content is not null && content.Contains(GitHubToken, StringComparison.Ordinal));
                Assert.DoesNotContain(tarEntries.Values, content => content is not null && content.Contains(RealProviderKey, StringComparison.Ordinal));

                // 4) El NuGetConnectProxy real rechaza CONNECT evil.example.com:443 con 403 y emite EgressBlocked con runId.
                using (var client = new TcpClient())
                {
                    await client.ConnectAsync(IPAddress.Loopback, proxy.BoundPort, Ct);
                    await using var stream = client.GetStream();
                    var request = Encoding.ASCII.GetBytes($"CONNECT {EvilTarget} HTTP/1.1\r\nHost: {EvilTarget}\r\n\r\n");
                    await stream.WriteAsync(request, Ct);

                    var response = await ReadHeadersAsync(stream, Ct);
                    Assert.StartsWith("HTTP/1.1 403 Forbidden", response, StringComparison.Ordinal);
                }

                var blocked = await SecurityFireHelpers.WaitForBlockedAsync(sink, EvilTarget, Ct);
                Assert.Equal(runId, blocked.RunId);
                var payload = Assert.IsType<EgressBlocked>(blocked.Payload);
                Assert.Equal(EvilTarget, payload.Target);
                Assert.Contains("lista permitida de NuGet", payload.Reason, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                await session.DisposeAsync();
            }

            // 5) Al destruir la sesión se revoca el token efímero del Gateway.
            Assert.Null(gateway.TryAuthenticate(syntheticToken));
            Assert.Null(gateway.ResolveRunId(null, _engine.ContainerIp));
        }
        finally
        {
            await proxy.StopAsync(Ct);
        }
    }

    private static Dictionary<string, string?> ReadTar(byte[] bytes)
    {
        var entries = new Dictionary<string, string?>(StringComparer.Ordinal);
        using var reader = new TarReader(new MemoryStream(bytes));
        while (reader.GetNextEntry() is { } entry)
        {
            string? content = null;
            if (entry.DataStream is { } data)
            {
                using var text = new StreamReader(data, Encoding.UTF8);
                content = text.ReadToEnd();
            }

            var key = entry.Name.TrimEnd('/');
            key = key == "." ? string.Empty : key.StartsWith("./", StringComparison.Ordinal) ? key[2..] : key;
            entries[key] = content;
        }

        return entries;
    }

    private static async Task<string> ReadHeadersAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

        var builder = new StringBuilder();
        var buffer = new byte[1];
        while (builder.Length < 4096)
        {
            var read = await stream.ReadAsync(buffer, timeoutCts.Token);
            if (read == 0)
            {
                break;
            }

            builder.Append((char)buffer[0]);
            if (builder.Length >= 4 && builder.ToString(builder.Length - 4, 4) == "\r\n\r\n")
            {
                break;
            }
        }

        return builder.ToString();
    }
}

/// <summary>Repositorio .NET 10 hostil (sin paquetes NuGet, 100 % offline) usado en la prueba de fuego de seguridad.</summary>
internal static class HostileProbeRepo
{
    public const string ReportPrefix = "HOSTILE_REPORT_JSON:";

    public static IReadOnlyDictionary<string, string> Files { get; } = new Dictionary<string, string>
    {
        ["HostileProbe.csproj"] = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """,
        ["Program.cs"] = """
            using System.Collections;
            using System.Net.Sockets;
            using System.Text.Json;

            var env = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                if (entry.Key is string k)
                {
                    env[k] = entry.Value?.ToString() ?? string.Empty;
                }
            }

            var gitPaths = new List<string>();
            var dir = new DirectoryInfo("/workspace");
            while (dir is not null)
            {
                var gitDir = Path.Combine(dir.FullName, ".git");
                var gitConfig = Path.Combine(gitDir, "config");
                if (Directory.Exists(gitDir) || File.Exists(gitDir))
                {
                    gitPaths.Add(gitDir);
                }

                if (File.Exists(gitConfig))
                {
                    gitPaths.Add(gitConfig);
                }

                dir = dir.Parent;
            }

            static void ScanGitArtifacts(string root, List<string> findings)
            {
                if (!Directory.Exists(root))
                {
                    return;
                }

                foreach (var entry in Directory.EnumerateFileSystemEntries(root, ".git*", SearchOption.AllDirectories))
                {
                    var name = Path.GetFileName(entry);
                    if (string.Equals(name, ".git", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(name, ".gitconfig", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(name, ".git-credentials", StringComparison.OrdinalIgnoreCase))
                    {
                        findings.Add(entry);
                    }
                }
            }

            ScanGitArtifacts("/workspace", gitPaths);
            var home = Environment.GetEnvironmentVariable("HOME") ?? "/tmp/home";
            ScanGitArtifacts(home, gitPaths);

            var ghPrefix = "ghp" + "_";
            var antPrefix = "sk-ant" + "-";
            var secretMatches = new List<string>();
            if (Directory.Exists("/workspace"))
            {
                foreach (var file in Directory.EnumerateFiles("/workspace", "*", SearchOption.AllDirectories))
                {
                    if (file.Contains("/bin/", StringComparison.Ordinal) || file.Contains("/obj/", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var content = File.ReadAllText(file);
                    if (content.Contains(ghPrefix, StringComparison.Ordinal) || content.Contains(antPrefix, StringComparison.Ordinal))
                    {
                        secretMatches.Add(file);
                    }
                }
            }

            var directTcpSucceeded = false;
            string? directTcpError = null;
            try
            {
                using var tcp = new TcpClient();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await tcp.ConnectAsync("1.1.1.1", 443, cts.Token);
                directTcpSucceeded = tcp.Connected;
            }
            catch (Exception ex)
            {
                directTcpError = $"{ex.GetType().Name}: {ex.Message}";
            }

            var proxyHttpSucceeded = false;
            int? proxyHttpStatusCode = null;
            string? proxyHttpError = null;
            try
            {
                using var handler = new SocketsHttpHandler
                {
                    UseProxy = true,
                    ConnectTimeout = TimeSpan.FromSeconds(5),
                };
                using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
                using var response = await http.GetAsync("https://evil.example.com/");
                proxyHttpSucceeded = response.IsSuccessStatusCode;
                proxyHttpStatusCode = (int)response.StatusCode;
                proxyHttpError = $"HTTP {(int)response.StatusCode}";
            }
            catch (HttpRequestException ex)
            {
                proxyHttpStatusCode = ex.StatusCode is { } code ? (int)code : null;
                proxyHttpError = $"{ex.GetType().Name}: {ex.Message}";
            }
            catch (Exception ex)
            {
                proxyHttpError = $"{ex.GetType().Name}: {ex.Message}";
            }

            var report = new
            {
                AnthropicApiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"),
                AnthropicBaseUrl = Environment.GetEnvironmentVariable("ANTHROPIC_BASE_URL"),
                HttpProxy = Environment.GetEnvironmentVariable("HTTP_PROXY"),
                HttpsProxy = Environment.GetEnvironmentVariable("HTTPS_PROXY"),
                NoProxy = Environment.GetEnvironmentVariable("NO_PROXY"),
                Environment = env,
                GitPathsFound = gitPaths,
                SecretMatchesInWorkspace = secretMatches,
                DirectTcpSucceeded = directTcpSucceeded,
                DirectTcpError = directTcpError,
                ProxyHttpSucceeded = proxyHttpSucceeded,
                ProxyHttpStatusCode = proxyHttpStatusCode,
                ProxyHttpError = proxyHttpError,
            };

            Console.WriteLine("HOSTILE_REPORT_JSON:" + JsonSerializer.Serialize(report));
            """,
    };

    public static HostileProbeReport ParseReport(string stdout)
    {
        var line = stdout
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(l => l.StartsWith(ReportPrefix, StringComparison.Ordinal));
        Assert.True(line is not null, $"No se encontró la salida '{ReportPrefix}' en stdout:\n{stdout}");

        var json = line![ReportPrefix.Length..];
        var report = JsonSerializer.Deserialize<HostileProbeReport>(json);
        Assert.NotNull(report);
        return report!;
    }
}

internal sealed record HostileProbeReport(
    string? AnthropicApiKey,
    string? AnthropicBaseUrl,
    string? HttpProxy,
    string? HttpsProxy,
    string? NoProxy,
    Dictionary<string, string> Environment,
    List<string> GitPathsFound,
    List<string> SecretMatchesInWorkspace,
    bool DirectTcpSucceeded,
    string? DirectTcpError,
    bool ProxyHttpSucceeded,
    int? ProxyHttpStatusCode,
    string? ProxyHttpError);

internal sealed class SecurityFireEventSink : IRunEventSink
{
    private readonly ConcurrentQueue<(Guid RunId, Guid? StageId, RunEvent Payload)> _events = new();
    private long _seq;

    public IReadOnlyList<(Guid RunId, Guid? StageId, RunEvent Payload)> Events => [.. _events];

    public Task<RunEvent> AppendAsync(Guid runId, Guid? stageId, RunEvent payload, CancellationToken cancellationToken)
    {
        var seq = Interlocked.Increment(ref _seq);
        var stamped = payload with { RunId = runId, Seq = seq, At = DateTimeOffset.UtcNow };
        _events.Enqueue((runId, stageId, stamped));
        return Task.FromResult(stamped);
    }
}

internal static class SecurityFireHelpers
{
    public static async Task<(Guid RunId, Guid? StageId, RunEvent Payload)> WaitForBlockedAsync(
        SecurityFireEventSink sink,
        string expectedTarget,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(5))
        {
            var match = sink.Events.FirstOrDefault(e => e.Payload is EgressBlocked blocked && blocked.Target == expectedTarget);
            if (match.Payload is not null)
            {
                return match;
            }

            await Task.Delay(20, cancellationToken);
        }

        var capturedTargets = sink.Events
            .Select(e => e.Payload)
            .OfType<EgressBlocked>()
            .Select(b => b.Target)
            .ToList();
        var summary = capturedTargets.Count == 0 ? "(ninguno)" : string.Join(", ", capturedTargets);
        Assert.Fail($"No se registró EgressBlocked para '{expectedTarget}' en 5 s. Destinos bloqueados capturados: {summary}.");
        throw new UnreachableException();
    }
}

internal sealed class StaticOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T>
{
    public T CurrentValue => currentValue;

    public T Get(string? name) => currentValue;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
