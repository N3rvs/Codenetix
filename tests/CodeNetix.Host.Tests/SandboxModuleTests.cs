using System.Buffers.Binary;
using System.Formats.Tar;
using System.Text;
using System.Text.Json;
using CodeNetix.Contracts;
using CodeNetix.GitHub;
using CodeNetix.Sandbox;
using CodeNetix.Sandbox.Docker;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CodeNetix.Host.Tests;

/// <summary>
/// Historia 2.1 sin Docker: el cliente, el gestor y la sesión contra un motor falso (<see cref="FakeDockerEngine"/>):
/// forma de las peticiones (etiquetas, límites, sin binds, red <c>none</c>, orden archive → start), demultiplexado,
/// recorte de la salida, fallos y liberación; más <c>ExportTreeAsync</c> del módulo GitHub.
/// </summary>
public sealed class SandboxModuleTests : IAsyncLifetime
{
    private const string Token = "ghp_testtoken0123456789abcdefghijklmn";
    private const string Owner = "sbxunit";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "codenetix-tests-" + Guid.NewGuid().ToString("N"));
    private readonly LocalGitRepos _repos = new();
    private FakeDockerEngine _engine = null!;
    private DockerClient _docker = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _engine = await FakeDockerEngine.StartAsync();
        _docker = new DockerClient(DockerEndpoint.Parse(_engine.Endpoint));
    }

    public async ValueTask DisposeAsync()
    {
        _docker.Dispose();
        await _engine.DisposeAsync();
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

    // --- Endpoint ---------------------------------------------------------------------------

    [Theory]
    [InlineData("unix:///var/run/docker.sock", "Unix", "/var/run/docker.sock")]
    [InlineData("npipe://./pipe/docker_engine", "NamedPipe", "docker_engine")]
    [InlineData("npipe:////./pipe/docker_engine", "NamedPipe", "docker_engine")]
    [InlineData("tcp://127.0.0.1:2375", "Tcp", "127.0.0.1:2375")]
    public void Docker_endpoints_are_parsed(string value, string transport, string address)
    {
        var endpoint = DockerEndpoint.Parse(value);

        Assert.Equal(transport, endpoint.Transport.ToString());
        Assert.Equal(address, endpoint.Address);
    }

    [Theory]
    [InlineData("ssh://user@host")]
    [InlineData("unix://")]
    [InlineData("npipe://./docker_engine")]
    public void Unknown_endpoints_are_rejected_but_never_break_startup(string value)
    {
        Assert.Throws<FormatException>(() => DockerEndpoint.Parse(value));
        Assert.Equal(DockerTransport.Invalid, DockerEndpoint.ParseOrInvalid(value).Transport);
    }

    [Fact]
    public void Default_endpoint_depends_on_the_os()
    {
        Assert.Equal(
            OperatingSystem.IsWindows() ? "npipe://./pipe/docker_engine" : "unix:///var/run/docker.sock",
            new SandboxOptions().EffectiveDockerEndpoint);
    }

    // --- Creación ---------------------------------------------------------------------------

    [Fact]
    public async Task Create_sends_labels_limits_named_volumes_no_binds_and_network_none_and_uploads_before_start()
    {
        var (manager, runId) = await PrepareRunAsync(new SandboxOptions { Cpus = 1.5, MemoryMb = 2048, PidsLimit = 256 });

        await using var session = await manager.CreateForRunAsync(runId, Ct);

        Assert.Equal(FakeDockerEngine.ContainerId, session.ContainerId);
        Assert.Equal(SandboxOptions.DefaultImage, session.Image);
        var requests = _engine.Requests.Where(r => r.Route is not "/_ping" and not "/version").ToList();
        var routes = requests.Select(r => $"{r.Method} {r.Route}").ToList();
        var ws = SandboxManager.VolumeNameOf(runId);
        var name = SandboxManager.ContainerNameOf(runId);
        Assert.Equal(
            [
                "POST /volumes/create",
                $"GET /containers/{name}/json",
                $"DELETE /volumes/{ws}",
                "POST /volumes/create",
                "POST /containers/create",
                $"PUT /containers/{FakeDockerEngine.ContainerId}/archive",
                $"POST /containers/{FakeDockerEngine.ContainerId}/start",
            ],
            routes);

        // Volúmenes: codenetix-tools y el de trabajo con las etiquetas.
        Assert.Equal(SandboxManager.ToolsVolume, requests[0].Json.GetProperty("Name").GetString());
        var volume = requests[3].Json;
        Assert.Equal(ws, volume.GetProperty("Name").GetString());
        Assert.Equal(runId.ToString("D"), volume.GetProperty("Labels").GetProperty("codenetix.run").GetString());
        Assert.Equal("true", volume.GetProperty("Labels").GetProperty("codenetix.managed").GetString());

        // Contenedor.
        var create = requests[4];
        Assert.Equal("?name=" + name, create.Query);
        Assert.Matches("^codenetix-sbx-[0-9a-f]{8}$", name);
        var body = create.Json;
        Assert.Equal(SandboxOptions.DefaultImage, body.GetProperty("Image").GetString());
        Assert.Equal(["sleep", "infinity"], body.GetProperty("Cmd").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("1000:1000", body.GetProperty("User").GetString());
        Assert.Equal("/workspace", body.GetProperty("WorkingDir").GetString());
        Assert.Contains("HOME=/tmp/home", body.GetProperty("Env").EnumerateArray().Select(e => e.GetString()));
        Assert.Contains("NUGET_PACKAGES=/tmp/nuget", body.GetProperty("Env").EnumerateArray().Select(e => e.GetString()));
        Assert.DoesNotContain(body.GetProperty("Env").EnumerateArray(), e => e.GetString()!.Contains(Token, StringComparison.Ordinal));
        Assert.Equal(runId.ToString("D"), body.GetProperty("Labels").GetProperty("codenetix.run").GetString());
        Assert.Equal("true", body.GetProperty("Labels").GetProperty("codenetix.managed").GetString());

        var host = body.GetProperty("HostConfig");
        Assert.Equal("none", host.GetProperty("NetworkMode").GetString());
        Assert.False(host.GetProperty("Privileged").GetBoolean());
        Assert.Equal(0, host.GetProperty("Binds").GetArrayLength());
        Assert.Equal(1_500_000_000L, host.GetProperty("NanoCpus").GetInt64());
        Assert.Equal(2048L * 1024 * 1024, host.GetProperty("Memory").GetInt64());
        Assert.Equal(2048L * 1024 * 1024, host.GetProperty("MemorySwap").GetInt64());
        Assert.Equal(256, host.GetProperty("PidsLimit").GetInt32());
        Assert.Equal(["ALL"], host.GetProperty("CapDrop").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["no-new-privileges"], host.GetProperty("SecurityOpt").EnumerateArray().Select(e => e.GetString()));
        Assert.True(host.GetProperty("ReadonlyRootfs").GetBoolean());
        Assert.StartsWith("rw,", host.GetProperty("Tmpfs").GetProperty("/tmp").GetString(), StringComparison.Ordinal);
        var mounts = host.GetProperty("Mounts").EnumerateArray().ToList();
        Assert.All(mounts, m => Assert.Equal("volume", m.GetProperty("Type").GetString()));
        Assert.Equal(
            [(ws, "/workspace", false), (SandboxManager.ToolsVolume, "/tools", true)],
            mounts.Select(m => (m.GetProperty("Source").GetString(), m.GetProperty("Target").GetString(), m.GetProperty("ReadOnly").GetBoolean())));

        // El tar: los archivos del BaseSha, de sandbox (1000:1000) y sin .git ni la cabecera global de git archive.
        var archive = requests[5];
        Assert.Contains("path=%2Fworkspace", archive.Query, StringComparison.Ordinal);
        var entries = ReadTar(archive.Body);
        Assert.Contains("README.md", entries.Keys);
        Assert.Contains("src/App/Program.cs", entries.Keys);
        Assert.Equal("Console.WriteLine(1);\n", entries["src/App/Program.cs"].Content);
        Assert.DoesNotContain(entries.Keys, k => k.Split('/').Contains(".git"));
        Assert.All(entries.Values, e => Assert.Equal((1000, 1000), (e.Uid, e.Gid)));
        Assert.DoesNotContain(entries.Values, e => e.Type == TarEntryType.GlobalExtendedAttributes);
    }

    [Fact]
    public async Task Missing_image_fails_with_a_hint_and_removes_the_half_created_volume()
    {
        _engine.CreateContainerError = (404, "No such image: codenetix-sandbox:latest");
        var (manager, runId) = await PrepareRunAsync();

        var ex = await Assert.ThrowsAsync<SandboxCreationException>(() => manager.CreateForRunAsync(runId, Ct));

        Assert.Equal($"falta la imagen codenetix-sandbox:latest: constrúyela con {SandboxManager.BuildHint}", ex.Message);
        var routes = _engine.Requests.Select(r => $"{r.Method} {r.Route}").ToList();
        Assert.Equal($"DELETE /volumes/{SandboxManager.VolumeNameOf(runId)}", routes[^1]);
        Assert.DoesNotContain(routes, r => r.StartsWith("DELETE /containers/", StringComparison.Ordinal) || r.EndsWith("/start", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Docker_500_on_create_is_a_creation_failure_with_the_reason()
    {
        _engine.CreateContainerError = (500, "error del motor");
        var (manager, runId) = await PrepareRunAsync();

        var ex = await Assert.ThrowsAsync<SandboxCreationException>(() => manager.CreateForRunAsync(runId, Ct));

        Assert.Equal("Docker ha respondido 500: error del motor", ex.Message);
    }

    [Fact]
    public async Task Unexpected_errors_after_creating_resources_are_wrapped_and_cleaned_up()
    {
        // 201 sin "Id": el cliente falla con una excepción inesperada (KeyNotFoundException).
        _engine.CreateContainerError = (201, "sin id");
        var (manager, runId) = await PrepareRunAsync();

        var ex = await Assert.ThrowsAsync<SandboxCreationException>(() => manager.CreateForRunAsync(runId, Ct));

        Assert.StartsWith("error inesperado (KeyNotFoundException)", ex.Message, StringComparison.Ordinal);
        Assert.Equal($"DELETE /volumes/{SandboxManager.VolumeNameOf(runId)}", _engine.Requests.Select(r => $"{r.Method} {r.Route}").Last());
    }

    [Fact]
    public async Task Without_a_work_tree_creation_fails_before_touching_docker()
    {
        var workspace = CreateWorkspace();
        var manager = new SandboxManager(_docker, workspace, Options.Create(new SandboxOptions()), NullLoggerFactory.Instance);

        var ex = await Assert.ThrowsAsync<SandboxCreationException>(() => manager.CreateForRunAsync(Guid.CreateVersion7(), Ct));

        Assert.StartsWith("no se ha podido exportar el árbol de trabajo:", ex.Message, StringComparison.Ordinal);
        Assert.Empty(_engine.Requests);
    }

    [Fact]
    public async Task Unreachable_docker_is_a_creation_failure()
    {
        var (_, runId) = await PrepareRunAsync();
        using var closed = new DockerClient(DockerEndpoint.Parse("tcp://127.0.0.1:1"));
        var manager = new SandboxManager(closed, CreateWorkspace(), Options.Create(new SandboxOptions()), NullLoggerFactory.Instance);

        var ex = await Assert.ThrowsAsync<SandboxCreationException>(() => manager.CreateForRunAsync(runId, Ct));

        Assert.StartsWith("Docker no responde", ex.Message, StringComparison.Ordinal);
    }

    // --- Exec -------------------------------------------------------------------------------

    [Fact]
    public async Task Exec_demultiplexes_stdout_and_stderr_and_reports_the_exit_code()
    {
        _engine.ExecFrames.Add((1, "hola "u8.ToArray()));
        _engine.ExecFrames.Add((2, "adiós\n"u8.ToArray()));
        _engine.ExecFrames.Add((1, "mundo\n"u8.ToArray()));
        _engine.ExecExitCode = 7;
        var (manager, runId) = await PrepareRunAsync();
        await using var session = await manager.CreateForRunAsync(runId, Ct);

        var result = await session.ExecAsync(
            new SandboxCommand("dotnet", ["test", "--no-restore"])
            {
                Timeout = TimeSpan.FromSeconds(90),
                Environment = System.Collections.Immutable.ImmutableDictionary<string, string>.Empty.Add("CI", "true"),
            },
            Ct);

        Assert.Equal(new SandboxExecResult(7, "hola mundo\n", "adiós\n", TimedOut: false, Truncated: false), result);

        var exec = _engine.Requests.Single(r => r.Route.EndsWith("/exec", StringComparison.Ordinal)).Json;
        Assert.Equal(["timeout", "--kill-after=2s", "--", "90s", "dotnet", "test", "--no-restore"], exec.GetProperty("Cmd").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("1000:1000", exec.GetProperty("User").GetString());
        Assert.Equal("/workspace", exec.GetProperty("WorkingDir").GetString());
        Assert.Equal(["CI=true"], exec.GetProperty("Env").EnumerateArray().Select(e => e.GetString()));
        Assert.False(exec.GetProperty("Tty").GetBoolean());
        Assert.False(exec.GetProperty("Privileged").GetBoolean());
    }

    [Fact]
    public async Task Exec_output_over_one_MiB_is_truncated_per_stream()
    {
        var chunk = Enumerable.Repeat((byte)'a', 700 * 1024).ToArray();
        _engine.ExecFrames.Add((1, chunk));
        _engine.ExecFrames.Add((2, "e"u8.ToArray()));
        _engine.ExecFrames.Add((1, chunk));
        var (manager, runId) = await PrepareRunAsync();
        await using var session = await manager.CreateForRunAsync(runId, Ct);

        var result = await session.ExecAsync(new SandboxCommand("cat", ["big"]), Ct);

        Assert.True(result.Truncated);
        Assert.Equal(SandboxExecResult.MaxOutputBytes, result.StdOut.Length);
        Assert.Equal("e", result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData(124)]
    [InlineData(137)]
    public async Task Exit_code_of_timeout_counts_as_TimedOut_only_after_the_deadline(int exitCode)
    {
        _engine.ExecExitCode = exitCode;
        var (manager, runId) = await PrepareRunAsync();
        await using var session = await manager.CreateForRunAsync(runId, Ct);

        // El falso responde al instante: con un plazo de 1 min, un 124/137 es del propio comando, no un timeout.
        var result = await session.ExecAsync(new SandboxCommand("sh", ["-c", $"exit {exitCode}"]) { Timeout = TimeSpan.FromMinutes(1) }, Ct);

        Assert.False(result.TimedOut);
        Assert.Equal(exitCode, result.ExitCode);
    }

    [Fact]
    public async Task Invalid_commands_are_rejected_before_calling_docker()
    {
        var (manager, runId) = await PrepareRunAsync();
        await using var session = await manager.CreateForRunAsync(runId, Ct);
        var before = _engine.Requests.Count;

        await Assert.ThrowsAsync<ArgumentException>(() => session.ExecAsync(new SandboxCommand(" "), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => session.ExecAsync(new SandboxCommand("ls") { WorkingDirectory = "relativo" }, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => session.ExecAsync(new SandboxCommand("ls") { Timeout = TimeSpan.Zero }, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => session.ExecAsync(new SandboxCommand("ls") { Timeout = TimeSpan.FromSeconds(-1) }, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => session.ExecAsync(new SandboxCommand("ls") { Timeout = TimeSpan.FromHours(24) + TimeSpan.FromSeconds(1) }, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => session.ExecAsync(
            new SandboxCommand("ls") { Environment = System.Collections.Immutable.ImmutableDictionary<string, string>.Empty.Add("A=B", "x") }, Ct));

        Assert.Equal(before, _engine.Requests.Count);
    }

    [Fact]
    public async Task Demultiplexer_handles_split_frames_and_never_cuts_a_utf8_character()
    {
        var frames = new MemoryStream();
        foreach (var (type, text) in new[] { ((byte)1, "ñandú"), ((byte)2, "err"), ((byte)0, "!"), ((byte)7, "desconocido") })
        {
            WriteFrame(frames, type, text);
        }

        frames.Position = 0;
        var stdout = new BoundedOutput(2);
        var stderr = new BoundedOutput(2);
        await MultiplexedStreamReader.ReadAsync(new OneByteStream(frames), stdout, stderr, Ct);

        // "ñ" ocupa 2 bytes: cabe entera; "a" ya no cabe. stderr "err" se recorta a "er".
        Assert.Equal("ñ", stdout.ToString());
        Assert.True(stdout.Truncated);
        Assert.Equal(8, stdout.TotalBytes); // "ñandú" (7 bytes) + "!" (stdin se trata como stdout)
        Assert.Equal("er", stderr.ToString());

        var cut = new BoundedOutput(1);
        cut.Append("ñ"u8);
        Assert.Equal(string.Empty, cut.ToString());
        Assert.True(cut.Truncated);
    }

    [Fact]
    public async Task Demultiplexer_turns_a_systemerr_frame_into_SandboxUnavailable_keeping_what_was_read()
    {
        var frames = new MemoryStream();
        WriteFrame(frames, 1, "antes\n");
        WriteFrame(frames, 3, "oci runtime exec failed");
        WriteFrame(frames, 1, "después\n");
        frames.Position = 0;
        var stdout = new BoundedOutput(1024);

        var ex = await Assert.ThrowsAsync<SandboxUnavailableException>(() => MultiplexedStreamReader.ReadAsync(frames, stdout, new BoundedOutput(1024), Ct));

        Assert.Contains("oci runtime exec failed", ex.Message, StringComparison.Ordinal);
        Assert.Equal("antes\ndespués\n", stdout.ToString());
    }

    private static void WriteFrame(Stream frames, byte type, string text)
    {
        var data = Encoding.UTF8.GetBytes(text);
        var header = new byte[8];
        header[0] = type;
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)data.Length);
        frames.Write(header);
        frames.Write(data);
    }

    // --- Tar del árbol ----------------------------------------------------------------------

    [Fact]
    public async Task Archive_rewrite_adds_an_owned_root_and_drops_escapes_absolute_paths_and_entries_under_symlinks()
    {
        var source = BuildTar(writer =>
        {
            writer.WriteEntry(new PaxGlobalExtendedAttributesTarEntry(new Dictionary<string, string> { ["comment"] = "sha" }));
            AddFile(writer, "README.md", "hola");
            AddFile(writer, "../fuera.txt", "x");
            AddFile(writer, "/etc/passwd", "x");
            AddFile(writer, "a/../../fuera.txt", "x");
            AddFile(writer, ".git/config", "x");
            writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "sub/"));
            AddFile(writer, "sub/file.txt", "dentro");
            writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "ok") { LinkName = "sub/file.txt" });
            writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "sub/up") { LinkName = "../README.md" });
            writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "abs") { LinkName = "/etc" });
            writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "esc") { LinkName = "sub/../../x" });
            AddFile(writer, "abs/shadow", "a través del enlace");
            AddFile(writer, "esc/inside.txt", "a través del enlace");
            AddFile(writer, "ok/nested", "a través del enlace");
            writer.WriteEntry(new PaxTarEntry(TarEntryType.Fifo, "fifo"));
        });

        var entries = await RewriteAsync(source);

        Assert.Equal(["", "README.md", "sub", "sub/file.txt", "ok", "sub/up"], entries.Keys);
        Assert.Equal(TarEntryType.Directory, entries[""].Type);
        Assert.All(entries.Values, e => Assert.Equal((1000, 1000), (e.Uid, e.Gid)));
        Assert.Equal("sub/file.txt", entries["ok"].LinkName);
    }

    [Fact]
    public async Task Archive_rewrite_rejects_hard_links()
    {
        var source = BuildTar(writer =>
        {
            AddFile(writer, "README.md", "hola");
            writer.WriteEntry(new PaxTarEntry(TarEntryType.HardLink, "copia") { LinkName = "README.md" });
        });

        await Assert.ThrowsAsync<InvalidDataException>(() => RewriteAsync(source));
    }

    private static MemoryStream BuildTar(Action<TarWriter> build)
    {
        var stream = new MemoryStream();
        using (var writer = new TarWriter(stream, TarEntryFormat.Pax, leaveOpen: true))
        {
            build(writer);
        }

        stream.Position = 0;
        return stream;
    }

    private static void AddFile(TarWriter writer, string name, string content) =>
        writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)) });

    private static async Task<Dictionary<string, TarInfo>> RewriteAsync(Stream source)
    {
        using var content = new WorkspaceArchiveContent(source);
        using var output = new MemoryStream();
        await content.CopyToAsync(output, Ct);
        return ReadTar(output.ToArray());
    }

    // --- Parada y liberación ----------------------------------------------------------------

    [Fact]
    public async Task Dispose_stops_removes_the_container_and_the_work_volume_once_and_never_the_tools_volume()
    {
        var (manager, runId) = await PrepareRunAsync();
        var session = await manager.CreateForRunAsync(runId, Ct);
        var before = _engine.Requests.Count;

        await session.DisposeAsync();
        await session.DisposeAsync();

        var routes = _engine.Requests.Skip(before).Select(r => $"{r.Method} {r.Route}{r.Query}").ToList();
        Assert.Equal(
            [
                $"POST /containers/{FakeDockerEngine.ContainerId}/stop?t=10",
                $"GET /containers/{FakeDockerEngine.ContainerId}/json",
                $"DELETE /containers/{FakeDockerEngine.ContainerId}?force=true",
                $"DELETE /volumes/{SandboxManager.VolumeNameOf(runId)}?force=true",
            ],
            routes);
        Assert.DoesNotContain(_engine.Requests, r => r.Method == "DELETE" && r.Route.Contains(SandboxManager.ToolsVolume, StringComparison.Ordinal));
        await Assert.ThrowsAsync<SandboxUnavailableException>(() => session.ExecAsync(new SandboxCommand("true"), Ct));
    }

    [Fact]
    public async Task Dispose_never_throws_when_docker_fails_and_kills_if_stop_fails()
    {
        var (manager, runId) = await PrepareRunAsync();
        var session = await manager.CreateForRunAsync(runId, Ct);
        _engine.FailEverything = true;

        await session.DisposeAsync();

        Assert.Contains(_engine.Requests, r => r.Route.EndsWith("/stop", StringComparison.Ordinal));
        Assert.Contains(_engine.Requests, r => r.Route.EndsWith("/kill", StringComparison.Ordinal));
        Assert.Contains(_engine.Requests, r => r.Method == "DELETE" && r.Route.StartsWith("/volumes/codenetix-ws-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cancelling_the_run_token_stops_the_container()
    {
        var (manager, runId) = await PrepareRunAsync();
        using var run = new CancellationTokenSource();
        await using var session = await manager.CreateForRunAsync(runId, run.Token);

        await run.CancelAsync();
        await session.StopAsync(); // la misma parada que ha disparado la cancelación

        Assert.Single(_engine.Requests, r => r.Route.EndsWith("/stop", StringComparison.Ordinal) && r.Query == "?t=10");
        await Assert.ThrowsAsync<SandboxUnavailableException>(() => session.ExecAsync(new SandboxCommand("true"), Ct));
    }

    // --- Salud ------------------------------------------------------------------------------

    [Fact]
    public async Task Health_check_reports_unreachable_docker_old_engines_and_missing_images()
    {
        using (var closed = new DockerClient(DockerEndpoint.Parse("tcp://127.0.0.1:1")))
        {
            var reason = Assert.Single(await new SandboxHealthCheck(closed, Options.Create(new SandboxOptions())).CheckAsync(Ct));
            Assert.StartsWith("Docker no responde en tcp://127.0.0.1:1", reason, StringComparison.Ordinal);
            Assert.Contains("-v /var/run/docker.sock:/var/run/docker.sock", reason, StringComparison.Ordinal);
        }

        using (var invalid = new DockerClient(DockerEndpoint.ParseOrInvalid("ssh://host")))
        {
            var reason = Assert.Single(await new SandboxHealthCheck(invalid, Options.Create(new SandboxOptions())).CheckAsync(Ct));
            Assert.Contains("no está admitido", reason, StringComparison.Ordinal);
        }

        Assert.Empty(await new SandboxHealthCheck(_docker, Options.Create(new SandboxOptions())).CheckAsync(Ct));

        _engine.ImageExists = false;
        Assert.Equal(
            [$"Falta la imagen codenetix-sandbox:latest del Sandbox: constrúyela con {SandboxManager.BuildHint}"],
            await new SandboxHealthCheck(_docker, Options.Create(new SandboxOptions())).CheckAsync(Ct));

        _engine.ApiVersion = "1.41";
        using var old = new DockerClient(DockerEndpoint.Parse(_engine.Endpoint));
        var tooOld = Assert.Single(await new SandboxHealthCheck(old, Options.Create(new SandboxOptions())).CheckAsync(Ct));
        Assert.StartsWith("El motor Docker de", tooOld, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Api_version_is_1_43_unless_the_engine_requires_a_newer_minimum()
    {
        Assert.Equal("/v1.43", await _docker.GetApiPrefixAsync(Ct));

        _engine.MinApiVersion = "1.44";
        using var newer = new DockerClient(DockerEndpoint.Parse(_engine.Endpoint));
        Assert.Equal("/v1.44", await newer.GetApiPrefixAsync(Ct));
    }

    // --- GitHub: ExportTreeAsync ------------------------------------------------------------

    [Fact]
    public async Task ExportTree_writes_only_the_versioned_files_of_the_base_sha_without_git()
    {
        var (_, runId, workspace) = await PrepareRunCoreAsync(new SandboxOptions());

        // Un archivo sin versionar en el árbol de trabajo no viaja.
        File.WriteAllText(Path.Combine(workspace.WorkspacePathOf(runId), "local.txt"), "no");
        using var tar = new MemoryStream();
        await workspace.ExportTreeAsync(runId, tar, Ct);

        var entries = ReadTar(tar.ToArray());
        Assert.Contains("README.md", entries.Keys);
        Assert.Contains("src/App/Program.cs", entries.Keys);
        Assert.DoesNotContain("local.txt", entries.Keys);
        Assert.DoesNotContain(entries.Keys, k => k.Split('/').Contains(".git"));
        Assert.DoesNotContain(entries.Values, e => e.Content?.Contains(Token, StringComparison.Ordinal) == true);

        await workspace.DeleteAsync(runId, Ct);
        await Assert.ThrowsAsync<RepositoryPreparationException>(() => workspace.ExportTreeAsync(runId, new MemoryStream(), Ct));
    }

    [Fact]
    public async Task ExportTree_ignores_the_repository_export_ignore_and_export_subst_attributes()
    {
        var repo = "attrs" + Guid.NewGuid().ToString("N")[..6];
        _repos.Create(Owner, repo, new Dictionary<string, string>
        {
            [".gitattributes"] = "/tests export-ignore\nversion.txt export-subst\n",
            ["tests/UnitTest.cs"] = "class T {}\n",
            ["version.txt"] = "$Format:%H$\n",
        });
        var workspace = CreateWorkspace();
        var runId = Guid.CreateVersion7();
        await workspace.PrepareAsync(runId, Owner, repo, Ct);

        using var tar = new MemoryStream();
        await workspace.ExportTreeAsync(runId, tar, Ct);

        var entries = ReadTar(tar.ToArray());
        Assert.Equal("class T {}\n", entries["tests/UnitTest.cs"].Content);
        Assert.Equal("$Format:%H$\n", entries["version.txt"].Content);
        Assert.Contains(".gitattributes", entries.Keys);
        await workspace.DeleteAsync(runId, Ct);
    }

    // --- Utilidades -------------------------------------------------------------------------

    private RepositoryWorkspace CreateWorkspace() =>
        new(
            Options.Create(new GitHubOptions { Token = Token, CloneBaseUrl = _repos.BaseUrl, WorkspacesDirectory = Path.Combine(_root, "workspaces") }),
            NullLogger<RepositoryWorkspace>.Instance);

    private async Task<(SandboxManager Manager, Guid RunId)> PrepareRunAsync(SandboxOptions? options = null)
    {
        var (manager, runId, _) = await PrepareRunCoreAsync(options ?? new SandboxOptions());
        return (manager, runId);
    }

    private async Task<(SandboxManager Manager, Guid RunId, RepositoryWorkspace Workspace)> PrepareRunCoreAsync(SandboxOptions options)
    {
        var repo = "r" + Guid.NewGuid().ToString("N")[..8];
        _repos.Create(Owner, repo, new Dictionary<string, string>
        {
            ["README.md"] = "# prueba\n",
            ["src/App/Program.cs"] = "Console.WriteLine(1);\n",
        });
        var workspace = CreateWorkspace();
        var runId = Guid.CreateVersion7();
        await workspace.PrepareAsync(runId, Owner, repo, Ct);
        return (new SandboxManager(_docker, workspace, Options.Create(options), NullLoggerFactory.Instance), runId, workspace);
    }

    private static Dictionary<string, TarInfo> ReadTar(byte[] bytes)
    {
        var entries = new Dictionary<string, TarInfo>(StringComparer.Ordinal);
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
            entries[key] = new TarInfo(entry.EntryType, entry.Uid, entry.Gid, content, entry.LinkName);
        }

        return entries;
    }

    private sealed record TarInfo(TarEntryType Type, int Uid, int Gid, string? Content, string LinkName);

    /// <summary>Flujo que entrega como mucho un byte por lectura (tramas partidas).</summary>
    private sealed class OneByteStream(Stream inner) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, Math.Min(count, 1));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer[..Math.Min(buffer.Length, 1)], cancellationToken);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
