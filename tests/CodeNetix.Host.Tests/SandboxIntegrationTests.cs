using System.Diagnostics;
using System.Text.Json;
using CodeNetix.Contracts;
using CodeNetix.GitHub;
using CodeNetix.Sandbox;
using CodeNetix.Sandbox.Docker;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CodeNetix.Host.Tests;

/// <summary>
/// Matriz de la historia 2.1 contra el Docker real (Docker Desktop en Windows, el socket en Linux). Se saltan si
/// Docker no responde o falta la imagen (<c>docker build -f docker/sandbox.Dockerfile -t codenetix-sandbox .</c>), por
/// ejemplo dentro de la etapa <c>sdk</c> de la imagen. La imagen se puede cambiar con
/// <c>CODENETIX_TEST_SANDBOX_IMAGE</c>. Todo lo que crean lleva <c>codenetix.managed=true</c> y una etiqueta
/// <c>codenetix.test=&lt;marcador&gt;</c> propia; al terminar se borra todo lo que la lleve.
/// </summary>
public sealed class SandboxIntegrationTests(SandboxDockerFixture fixture) : IAsyncLifetime, IClassFixture<SandboxDockerFixture>
{
    private const string TestLabel = "codenetix.test";
    private const string Token = "ghp_testtoken0123456789abcdefghijklmn";
    private const string Owner = "sbx";

    private readonly string _marker = "it-" + Guid.NewGuid().ToString("N")[..12];
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codenetix-tests-" + Guid.NewGuid().ToString("N"));
    private readonly LocalGitRepos _repos = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Image => SandboxDockerFixture.Image;

    private static string Endpoint => SandboxDockerFixture.Endpoint;

    private DockerClient Docker => fixture.Docker;

    public ValueTask InitializeAsync()
    {
        if (fixture.Unavailable is { } reason)
        {
            Assert.Skip(reason);
        }

        return ValueTask.CompletedTask;
    }

    // --- Creación ---------------------------------------------------------------------------

    [Fact]
    public async Task Create_uploads_the_base_tree_without_git_into_a_labelled_limited_container_without_binds()
    {
        var (manager, runId) = await PrepareRunAsync(new Dictionary<string, string>
        {
            ["README.md"] = "# prueba\n",
            ["src/App/Program.cs"] = "Console.WriteLine(1);\n",
        });

        await using var session = await manager.CreateForRunAsync(runId, Ct);

        Assert.Equal(runId, session.RunId);
        Assert.Equal(Image, session.Image);
        Assert.True(await Docker.VolumeExistsAsync(SandboxManager.VolumeNameOf(runId), Ct));
        Assert.True(await Docker.VolumeExistsAsync(SandboxManager.ToolsVolume, Ct));

        using (var inspect = await Docker.InspectContainerAsync(session.ContainerId, Ct))
        {
            var root = inspect!.RootElement;
            Assert.Equal("/" + SandboxManager.ContainerNameOf(runId), root.GetProperty("Name").GetString());
            var labels = root.GetProperty("Config").GetProperty("Labels");
            Assert.Equal(runId.ToString("D"), labels.GetProperty("codenetix.run").GetString());
            Assert.Equal("true", labels.GetProperty("codenetix.managed").GetString());
            Assert.Equal("1000:1000", root.GetProperty("Config").GetProperty("User").GetString());

            var host = root.GetProperty("HostConfig");
            Assert.Equal(2_000_000_000L, host.GetProperty("NanoCpus").GetInt64());
            Assert.Equal(4096L * 1024 * 1024, host.GetProperty("Memory").GetInt64());
            Assert.Equal(512L, host.GetProperty("PidsLimit").GetInt64());
            Assert.Equal("none", host.GetProperty("NetworkMode").GetString());
            Assert.False(host.GetProperty("Privileged").GetBoolean());
            Assert.True(host.GetProperty("ReadonlyRootfs").GetBoolean());
            Assert.Contains("ALL", host.GetProperty("CapDrop").EnumerateArray().Select(c => c.GetString()));
            Assert.Contains("no-new-privileges", host.GetProperty("SecurityOpt").EnumerateArray().Select(c => c.GetString()));
            Assert.True(host.GetProperty("Binds").ValueKind is JsonValueKind.Null || host.GetProperty("Binds").GetArrayLength() == 0);

            // Solo montajes de tipo volume: el de trabajo en /workspace y codenetix-tools en solo lectura en /tools.
            var mounts = root.GetProperty("Mounts").EnumerateArray().ToList();
            Assert.All(mounts, m => Assert.Equal("volume", m.GetProperty("Type").GetString()));
            var workspace = Assert.Single(mounts, m => m.GetProperty("Destination").GetString() == "/workspace");
            Assert.Equal(SandboxManager.VolumeNameOf(runId), workspace.GetProperty("Name").GetString());
            Assert.True(workspace.GetProperty("RW").GetBoolean());
            var tools = Assert.Single(mounts, m => m.GetProperty("Destination").GetString() == "/tools");
            Assert.Equal(SandboxManager.ToolsVolume, tools.GetProperty("Name").GetString());
            Assert.False(tools.GetProperty("RW").GetBoolean());
        }

        // /workspace: los archivos del BaseSha, del usuario sandbox y sin .git.
        var listing = await session.ExecAsync(Sh("cd /workspace && find . -mindepth 1 | sort && stat -c '%u:%g' README.md src/App/Program.cs"), Ct);
        Assert.Equal(0, listing.ExitCode);
        var lines = listing.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("./README.md", lines);
        Assert.Contains("./src/App/Program.cs", lines);
        Assert.DoesNotContain(lines, l => l.Contains(".git", StringComparison.Ordinal));
        Assert.Equal(["1000:1000", "1000:1000"], lines[^2..]);

        // La raíz /workspace es de sandbox: se puede escribir en ella.
        var write = await session.ExecAsync(Sh("touch /workspace/x && mkdir /workspace/d && stat -c '%u:%g' /workspace"), Ct);
        Assert.Equal(0, write.ExitCode);
        Assert.Equal("1000:1000\n", write.StdOut);

        // Ni el token de GitHub ni nada de .git en el entorno del Sandbox; sin red.
        var env = await session.ExecAsync(new SandboxCommand("env"), Ct);
        Assert.DoesNotContain(Token, env.StdOut, StringComparison.Ordinal);
        Assert.Contains("HOME=/tmp/home", env.StdOut, StringComparison.Ordinal);
        var network = await session.ExecAsync(Sh("cat /proc/net/route | wc -l; ls /sys/class/net"), Ct);
        Assert.DoesNotContain("eth0", network.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exec_dotnet_version_returns_zero_and_the_version_on_stdout_and_separates_stderr()
    {
        var (manager, runId) = await PrepareRunAsync();
        await using var session = await manager.CreateForRunAsync(runId, Ct);

        var version = await session.ExecAsync(new SandboxCommand("dotnet", ["--version"]), Ct);
        Assert.Equal(0, version.ExitCode);
        Assert.Matches(@"^10\.\d+\.\d+", version.StdOut.Trim());
        Assert.False(version.TimedOut);
        Assert.False(version.Truncated);

        var mixed = await session.ExecAsync(Sh("echo salida; echo error 1>&2; pwd; exit 3") with
        {
            WorkingDirectory = "/tmp",
            Environment = System.Collections.Immutable.ImmutableDictionary<string, string>.Empty.Add("CODENETIX_PRUEBA", "1"),
        }, Ct);
        Assert.Equal(3, mixed.ExitCode);
        Assert.Equal("salida\n/tmp\n", mixed.StdOut);
        Assert.Equal("error\n", mixed.StdErr);
    }

    [Fact]
    public async Task Huge_output_is_truncated_to_one_MiB_per_stream()
    {
        var (manager, runId) = await PrepareRunAsync();
        await using var session = await manager.CreateForRunAsync(runId, Ct);

        var result = await session.ExecAsync(Sh("head -c 3000000 /dev/zero | tr '\\0' 'a'; head -c 10 /dev/zero | tr '\\0' 'e' 1>&2"), Ct);

        Assert.Equal(0, result.ExitCode);
        Assert.True(result.Truncated);
        Assert.Equal(SandboxExecResult.MaxOutputBytes, result.StdOut.Length);
        Assert.All(result.StdOut, c => Assert.Equal('a', c));
        Assert.Equal("eeeeeeeeee", result.StdErr);
    }

    [Fact]
    public async Task Exec_timeout_marks_TimedOut_and_the_session_stays_usable()
    {
        var (manager, runId) = await PrepareRunAsync();
        await using var session = await manager.CreateForRunAsync(runId, Ct);

        var stopwatch = Stopwatch.StartNew();
        var result = await session.ExecAsync(new SandboxCommand("sleep", ["60"]) { Timeout = TimeSpan.FromSeconds(2) }, Ct);
        stopwatch.Stop();

        Assert.True(result.TimedOut);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(15));

        var after = await session.ExecAsync(new SandboxCommand("echo", ["sigue"]), Ct);
        Assert.Equal(0, after.ExitCode);
        Assert.Equal("sigue\n", after.StdOut);
        Assert.False(after.TimedOut);
    }

    [Fact]
    public async Task A_finished_command_whose_grandchild_keeps_the_pipe_open_reports_its_real_exit_code_and_output()
    {
        var (manager, runId) = await PrepareRunAsync();
        await using var session = await manager.CreateForRunAsync(runId, Ct);

        // sh termina al instante, pero el sleep en segundo plano hereda stdout y mantiene abierto el flujo.
        var result = await session.ExecAsync(Sh("echo hola; sleep 60 & exit 5") with { Timeout = TimeSpan.FromSeconds(1) }, Ct);

        Assert.False(result.TimedOut);
        Assert.Equal(5, result.ExitCode);
        Assert.Equal("hola\n", result.StdOut);
        Assert.Equal(0, (await session.ExecAsync(new SandboxCommand("true"), Ct)).ExitCode);
    }

    // --- Parada y liberación ----------------------------------------------------------------

    [Fact]
    public async Task Cancelling_the_run_token_during_an_exec_stops_the_container_within_ten_seconds()
    {
        var (manager, runId) = await PrepareRunAsync();
        using var run = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        await using var session = await manager.CreateForRunAsync(runId, run.Token);

        var exec = session.ExecAsync(new SandboxCommand("sleep", ["300"]), run.Token);
        await Task.Delay(1000, Ct);

        var stopwatch = Stopwatch.StartNew();
        await run.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exec);
        while (await IsRunningAsync(session.ContainerId))
        {
            Assert.True(stopwatch.Elapsed <= TimeSpan.FromSeconds(10), "El contenedor sigue vivo 10 s después de cancelar.");
            await Task.Delay(100, Ct);
        }

        Assert.True(stopwatch.Elapsed <= TimeSpan.FromSeconds(10), $"Tardó {stopwatch.Elapsed}.");
        await Assert.ThrowsAsync<SandboxUnavailableException>(() => session.ExecAsync(new SandboxCommand("true"), Ct));
    }

    [Fact]
    public async Task Max_lifetime_stops_the_container()
    {
        var (manager, runId) = await PrepareRunAsync(lifetime: TimeSpan.FromSeconds(2));
        await using var session = await manager.CreateForRunAsync(runId, Ct);
        Assert.True(await IsRunningAsync(session.ContainerId));

        var stopwatch = Stopwatch.StartNew();
        while (await IsRunningAsync(session.ContainerId))
        {
            Assert.True(stopwatch.Elapsed <= TimeSpan.FromSeconds(20), "El contenedor sigue vivo tras su vida máxima.");
            await Task.Delay(200, Ct);
        }
    }

    [Fact]
    public async Task Dispose_removes_the_container_and_work_volume_keeps_tools_and_is_idempotent()
    {
        var (manager, runId) = await PrepareRunAsync();
        var session = await manager.CreateForRunAsync(runId, Ct);
        var containerId = session.ContainerId;

        await session.DisposeAsync();
        await session.DisposeAsync();

        using (var inspect = await Docker.InspectContainerAsync(containerId, Ct))
        {
            Assert.Null(inspect);
        }

        Assert.False(await Docker.VolumeExistsAsync(SandboxManager.VolumeNameOf(runId), Ct));
        Assert.True(await Docker.VolumeExistsAsync(SandboxManager.ToolsVolume, Ct));
        await Assert.ThrowsAsync<SandboxUnavailableException>(() => session.ExecAsync(new SandboxCommand("true"), Ct));
    }

    [Fact]
    public async Task Missing_image_fails_with_a_reason_and_leaves_nothing_behind()
    {
        var (manager, runId) = await PrepareRunAsync(image: "codenetix-sandbox-inexistente:" + _marker);

        var ex = await Assert.ThrowsAsync<SandboxCreationException>(() => manager.CreateForRunAsync(runId, Ct));

        Assert.StartsWith("falta la imagen codenetix-sandbox-inexistente:", ex.Message, StringComparison.Ordinal);
        Assert.Contains(SandboxManager.BuildHint, ex.Message, StringComparison.Ordinal);
        Assert.False(await Docker.VolumeExistsAsync(SandboxManager.VolumeNameOf(runId), Ct));
        Assert.Empty(await Docker.ListContainerIdsAsync($"{TestLabel}={_marker}", Ct));
    }

    [Fact]
    public async Task Health_check_is_clean_with_the_image_and_reports_a_missing_one()
    {
        var ok = new SandboxHealthCheck(Docker, Options.Create(new SandboxOptions { Image = Image }));
        Assert.Empty(await ok.CheckAsync(Ct));

        var missing = new SandboxHealthCheck(Docker, Options.Create(new SandboxOptions { Image = "codenetix-sandbox-inexistente:" + _marker }));
        var reason = Assert.Single(await missing.CheckAsync(Ct));
        Assert.Equal(SandboxHealthCheck.MissingImage("codenetix-sandbox-inexistente:" + _marker), reason);
    }

    // --- Utilidades -------------------------------------------------------------------------

    private static SandboxCommand Sh(string script) => new("sh", ["-c", script]);

    /// <summary>Repositorio local clonado con <see cref="RepositoryWorkspace"/> y un gestor con la etiqueta de la prueba.</summary>
    private async Task<(SandboxManager Manager, Guid RunId)> PrepareRunAsync(
        IReadOnlyDictionary<string, string>? files = null,
        string? image = null,
        TimeSpan? lifetime = null)
    {
        var repo = "r" + Guid.NewGuid().ToString("N")[..8];
        _repos.Create(Owner, repo, files);
        var workspace = new RepositoryWorkspace(
            Options.Create(new GitHubOptions { Token = Token, CloneBaseUrl = _repos.BaseUrl, WorkspacesDirectory = Path.Combine(_root, "workspaces") }),
            NullLogger<RepositoryWorkspace>.Instance);
        var runId = Guid.CreateVersion7();
        await workspace.PrepareAsync(runId, Owner, repo, Ct);

        var options = new SandboxOptions { Image = image ?? Image, DockerEndpoint = Endpoint, MaxLifetimeOverride = lifetime };
        options.ExtraLabels[TestLabel] = _marker;
        return (new SandboxManager(Docker, workspace, Options.Create(options), NullLoggerFactory.Instance), runId);
    }

    private async Task<bool> IsRunningAsync(string containerId)
    {
        using var inspect = await Docker.InspectContainerAsync(containerId, Ct);
        return inspect is not null && inspect.RootElement.GetProperty("State").GetProperty("Running").GetBoolean();
    }

    public async ValueTask DisposeAsync()
    {
        if (fixture.Unavailable is null)
        {
            // Todo lo que lleve la etiqueta de esta prueba, aunque una aserción haya fallado a medias.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            foreach (var id in await Docker.ListContainerIdsAsync($"{TestLabel}={_marker}", timeout.Token))
            {
                await Docker.RemoveContainerAsync(id, timeout.Token);
            }

            foreach (var name in await Docker.ListVolumeNamesAsync($"{TestLabel}={_marker}", timeout.Token))
            {
                await Docker.RemoveVolumeAsync(name, timeout.Token);
            }
        }

        _repos.Dispose();
        try
        {
            if (Directory.Exists(_root))
            {
                foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Limpieza de mejor esfuerzo.
        }
    }
}

/// <summary>
/// Docker compartido por <see cref="SandboxIntegrationTests"/>: comprueba una vez si se puede usar y, al terminar, borra
/// el volumen <c>codenetix-tools</c> solo si no existía antes de las pruebas (nunca el de un usuario).
/// </summary>
public sealed class SandboxDockerFixture : IAsyncLifetime
{
    private DockerClient? _docker;
    private bool _toolsPreexisted = true;

    public static string Image => Environment.GetEnvironmentVariable("CODENETIX_TEST_SANDBOX_IMAGE") is { Length: > 0 } image
        ? image
        : SandboxOptions.DefaultImage;

    public static string Endpoint => Environment.GetEnvironmentVariable("DOCKER_HOST") is { Length: > 0 } host
        ? host
        : SandboxOptions.DefaultDockerEndpoint;

    /// <summary><c>null</c> si Docker responde y tiene la imagen; si no, el motivo para saltar las pruebas.</summary>
    public string? Unavailable { get; private set; }

    internal DockerClient Docker => _docker ?? throw new InvalidOperationException("Docker no está disponible.");

    public async ValueTask InitializeAsync()
    {
        try
        {
            _docker = new DockerClient(DockerEndpoint.Parse(Endpoint));
            var reasons = await new SandboxHealthCheck(_docker, Options.Create(new SandboxOptions { Image = Image })).CheckAsync(CancellationToken.None);
            if (reasons.Count > 0)
            {
                Unavailable = "Pruebas de integración del Sandbox omitidas: " + reasons[0];
                return;
            }

            _toolsPreexisted = await _docker.VolumeExistsAsync(SandboxManager.ToolsVolume, CancellationToken.None);
        }
        catch (FormatException ex)
        {
            Unavailable = "Pruebas de integración del Sandbox omitidas: " + ex.Message;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_docker is null)
        {
            return;
        }

        if (Unavailable is null && !_toolsPreexisted)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await _docker.RemoveVolumeAsync(SandboxManager.ToolsVolume, timeout.Token);
            }
            catch (DockerApiException)
            {
                // En uso por otro contenedor: se conserva.
            }
        }

        _docker.Dispose();
    }
}
