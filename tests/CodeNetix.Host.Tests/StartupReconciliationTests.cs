using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using CodeNetix.Contracts;
using CodeNetix.Ingestion.Data;
using CodeNetix.Persistence;
using CodeNetix.Pipeline.Data;
using CodeNetix.Pipeline.Domain;
using CodeNetix.Pipeline.Events;
using CodeNetix.Pipeline.Features.RunPipeline;
using CodeNetix.Sandbox;
using CodeNetix.Sandbox.Docker;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeNetix.Host.Tests;

/// <summary>
/// Matriz de la historia 5.3 (AD-18, FR-24): orden estricto de <c>StartupReconciler</c>, reinicio real sobre un
/// <c>/data</c> persistente, limpieza de Docker (contra un motor falso: nunca se borra nada del Docker real, donde
/// corren en paralelo <see cref="SandboxIntegrationTests"/>), limpieza de <c>workspaces/</c> y modo <c>Diagnostic</c>.
/// </summary>
public sealed class StartupReconciliationTests : IDisposable
{
    private static readonly string[] NonTerminalStates = ["Received", "Analyzing", "Coding", "Reviewing", "Deciding"];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "codenetix-restart-" + Guid.NewGuid().ToString("N"));
    private readonly string _dataDirectory;
    private readonly string _webRoot;

    public StartupReconciliationTests()
    {
        _dataDirectory = Path.Combine(_root, "data");
        _webRoot = Path.Combine(_root, "wwwroot");
        Directory.CreateDirectory(_webRoot);
        File.WriteAllText(Path.Combine(_webRoot, "index.html"), "<!doctype html><html><body>Panel</body></html>");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string DatabasePath => Path.Combine(_dataDirectory, PersistenceOptions.DatabaseFileName);

    private string WorkspacesPath => Path.Combine(_dataDirectory, "workspaces");

    private CodeNetixFactory CreateReadyFactory(FakeGitHub? gitHub = null) =>
        new(_dataDirectory, _webRoot, new Dictionary<string, string?>(CodeNetixFactory.CompleteSettings), gitHub: gitHub);

    /// <summary>Sin keys: <c>Diagnostic</c>, así que ningún Dispatcher despacha lo que se siembra en la base de datos.</summary>
    private CodeNetixFactory CreateDiagnosticFactory(IReadOnlyDictionary<string, string?>? settings = null) =>
        new(_dataDirectory, _webRoot, settings);

    // --- 1. Orden estricto de AD-18 ------------------------------------------------------------

    [Fact]
    public async Task Startup_runs_migrations_health_run_reconciliation_orphan_cleanup_and_only_then_wakes_the_Dispatcher()
    {
        var steps = new ConcurrentQueue<string>();
        await using var baseFactory = CreateReadyFactory();
        IInstanceHealth? health = null;
        string Status() => health?.Status.ToString() ?? "?";
        baseFactory.SandboxReconciler.OnCleanup = () => steps.Enqueue("sandbox-cleanup:" + Status());

        await using var factory = baseFactory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            services.AddSingleton<IModuleDatabase>(new SpyModuleDatabase(() => steps.Enqueue("migrations")));
            services.AddSingleton<ISandboxHealthCheck>(new SpyHealthCheck(() => steps.Enqueue("health:" + Status())));
            Decorate<IRunStartupReconciler>(services, (inner, sp) =>
            {
                health = sp.GetRequiredService<IInstanceHealth>();
                return new SpyRunReconciler(inner, () => steps.Enqueue("reconcile:" + Status()));
            });
            Decorate<IRepositoryWorkspace>(services, (inner, _) => new SpyWorkspace(inner, () => steps.Enqueue("workspace-cleanup:" + Status())));
            Decorate<IAcceptedEventSource>(services, (inner, _) => new SpyEventSource(inner, () => steps.Enqueue("dispatcher:" + Status())));
        }));

        using var client = Authorized(factory);
        (await client.GetAsync("/api/health", Ct)).EnsureSuccessStatusCode();
        await WaitUntilAsync(() => steps.Any(s => s.StartsWith("dispatcher:", StringComparison.Ordinal)), "el Dispatcher no llegó a pedir Eventos");

        Assert.Equal(
            ["migrations", "health:Starting", "reconcile:Starting", "sandbox-cleanup:Starting", "workspace-cleanup:Starting", "dispatcher:Ready"],
            steps.ToArray());
    }

    [Fact]
    public async Task Migration_failure_aborts_startup_before_health_and_run_reconciliation()
    {
        var steps = new ConcurrentQueue<string>();
        await using var baseFactory = CreateReadyFactory();
        baseFactory.SandboxReconciler.OnCleanup = () => steps.Enqueue("sandbox-cleanup");
        await using var factory = baseFactory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            services.AddSingleton<IModuleDatabase>(new SpyModuleDatabase(() => throw new InvalidOperationException("migración rota")));
            services.AddSingleton<ISandboxHealthCheck>(new SpyHealthCheck(() => steps.Enqueue("health")));
            Decorate<IRunStartupReconciler>(services, (inner, _) => new SpyRunReconciler(inner, () => steps.Enqueue("reconcile")));
        }));

        Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Empty(steps);
    }

    // --- 2. Reinicio real sobre /data persistente -----------------------------------------------

    [Fact]
    public async Task Restart_fails_every_non_terminal_run_as_HostRestarted_keeps_terminal_ones_and_then_dispatches_pending_events()
    {
        var seeded = new Dictionary<string, PipelineRun>(StringComparer.Ordinal);
        PipelineRun terminal;
        var pendingEventId = Guid.CreateVersion7();
        await using (var before = CreateDiagnosticFactory())
        {
            using var client = before.CreateClient();
            (await client.GetAsync("/api/health", Ct)).EnsureSuccessStatusCode();
            for (var i = 0; i < NonTerminalStates.Length; i++)
            {
                seeded[NonTerminalStates[i]] = await SeedRunAsync(before, $"github:o/r#{i + 1}", NonTerminalStates[i]);
            }

            terminal = await SeedRunAsync(before, "github:o/r#50", "PullRequestCreated");

            // Evento aceptado sin Ejecución sobre el mismo objeto que la Ejecución en Coding (github:o/r#3).
            await InsertAcceptedEventAsync(before, pendingEventId, "github:o/r#3");
        }

        long terminalUpdatedAt;
        await using (var connection = await OpenReadOnlyAsync())
        {
            terminalUpdatedAt = (long)(await ScalarAsync(connection, $"SELECT updated_at FROM run_runs WHERE id = '{Upper(terminal.Id)}';"))!;
        }

        SqliteConnection.ClearAllPools();
        await using var restarted = CreateReadyFactory();
        using var restartedClient = restarted.CreateAuthorizedClient();

        // El arranque es síncrono: cuando la API responde, la reconciliación y los avisos ya han terminado.
        var reconciledUpdates = new List<DateTimeOffset>();
        foreach (var (state, run) in seeded)
        {
            var body = await GetRunAsync(restartedClient, run.Id);
            Assert.True(body.State == "Failed", $"{state}: {body.State}");
            Assert.Equal("HostRestarted", body.FailureReason);
            Assert.Equal(RunStartupReconciler.HostRestartedDetail, body.FailureDetail);
            reconciledUpdates.Add(body.UpdatedAt);

            var events = await GetEventsAsync(restartedClient, run.Id);
            var change = Assert.Single(events, e => e.GetProperty("type").GetString() == "StateChanged" && e.GetProperty("to").GetString() == "Failed");
            Assert.Equal(state, change.GetProperty("from").GetString());
            Assert.Equal("HostRestarted", change.GetProperty("failureReason").GetString());
            var notice = Assert.Single(events, e => e.GetProperty("type").GetString() is "IssueCommented" or "NotificationFailed");
            Assert.Equal("IssueCommented", notice.GetProperty("type").GetString());
            Assert.Equal("RunFailed", notice.GetProperty("kind").GetString());

            var number = long.Parse(run.ObjectKey[(run.ObjectKey.IndexOf('#', StringComparison.Ordinal) + 1)..], System.Globalization.CultureInfo.InvariantCulture);
            Assert.Single(restarted.GitHub!.CommentsOn("o", "r", number), c => c.Body.Contains(run.ShortId, StringComparison.Ordinal));
        }

        // La Ejecución terminal no se toca.
        var terminalBody = await GetRunAsync(restartedClient, terminal.Id);
        Assert.Equal("PullRequestCreated", terminalBody.State);
        Assert.Null(terminalBody.FailureReason);
        Assert.Equal(2, (await GetEventsAsync(restartedClient, terminal.Id)).Count);
        Assert.Empty(restarted.GitHub!.CommentsOn("o", "r", 50));

        // El Evento pendiente se despacha después del arranque y crea una Ejecución nueva (el objeto quedó libre).
        var pending = await WaitForEventAsync(restartedClient, pendingEventId, e => e.RunId is not null || e.Status == "Ignored");
        Assert.Equal("Accepted", pending.Status);
        Assert.NotEqual(seeded["Coding"].Id, pending.RunId);
        var dispatched = await WaitForRunAsync(restartedClient, pending.RunId!.Value, r => r.State is "PullRequestCreated" or "Failed" or "Cancelled");
        Assert.Equal("github:o/r#3", dispatched.ObjectKey);
        Assert.True(dispatched.CreatedAt >= reconciledUpdates.Max(), "La Ejecución nueva debe crearse después de la reconciliación.");

        await using var after = await OpenReadOnlyAsync();
        Assert.Equal(terminalUpdatedAt, await ScalarAsync(after, $"SELECT updated_at FROM run_runs WHERE id = '{Upper(terminal.Id)}';"));
        Assert.Equal(0L, await ScalarAsync(after, "SELECT COUNT(*) FROM ing_events WHERE status = 'Ignored';"));
        foreach (var run in seeded.Values)
        {
            Assert.Equal(1L, await ScalarAsync(after, $"SELECT COUNT(*) FROM gh_notifications WHERE run_id = '{Upper(run.Id)}' AND kind = 'RunFailed' AND status = 'Posted';"));
        }
    }

    [Fact]
    public async Task Run_that_cannot_be_closed_keeps_the_instance_in_Diagnostic_and_leaves_its_pending_event_untouched()
    {
        PipelineRun stuck;
        PipelineRun closable;
        var pendingEventId = Guid.CreateVersion7();
        await using (var before = CreateDiagnosticFactory())
        {
            using var client = before.CreateClient();
            (await client.GetAsync("/api/health", Ct)).EnsureSuccessStatusCode();
            stuck = await SeedRunAsync(before, "github:o/r#21", "Coding");
            closable = await SeedRunAsync(before, "github:o/r#22", "Analyzing");
            await InsertAcceptedEventAsync(before, pendingEventId, "github:o/r#21");
        }

        // Fallo persistente al cerrar una Ejecución concreta (p. ej. una fila corrupta): SQLite aborta su UPDATE.
        SqliteConnection.ClearAllPools();
        await using (var connection = new SqliteConnection($"Data Source={DatabasePath}"))
        {
            await connection.OpenAsync(Ct);
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE TRIGGER test_block_close BEFORE UPDATE ON run_runs WHEN OLD.id = '{Upper(stuck.Id)}' BEGIN SELECT RAISE(ABORT, 'fila bloqueada'); END;";
            await command.ExecuteNonQueryAsync(Ct);
        }

        SqliteConnection.ClearAllPools();
        await using var restarted = CreateReadyFactory();
        using var restartedClient = restarted.CreateAuthorizedClient();

        var health = await restartedClient.GetFromJsonAsync<HealthBody>("/api/health", Ct);
        Assert.Equal("Diagnostic", health!.Status);
        Assert.Contains(StartupReconciler.RunsStillActive(1), health.Reasons);
        Assert.Equal("Coding", (await GetRunAsync(restartedClient, stuck.Id)).State);
        Assert.Equal("Failed", (await GetRunAsync(restartedClient, closable.Id)).State);

        // Sin Ready no hay Dispatcher: el Evento del objeto aún ocupado no pasa a Ignored.
        restarted.Services.GetRequiredService<IEventDispatchSignal>().Notify();
        await Task.Delay(TimeSpan.FromSeconds(1.5), Ct);
        await using var after = await OpenReadOnlyAsync();
        Assert.Equal(
            "Accepted",
            await ScalarAsync(after, $"SELECT status FROM ing_events WHERE id = '{Upper(pendingEventId)}' AND dispatched_at IS NULL;"));
    }

    [Fact]
    public async Task Hanging_GitHub_does_not_hold_startup_beyond_the_bound_and_lost_notices_are_sent_on_the_next_start()
    {
        await using var gitHub = await FakeGitHub.StartAsync(Ct);
        var runs = new List<PipelineRun>();
        var pendingEventId = Guid.CreateVersion7();
        await using (var before = CreateDiagnosticFactory())
        {
            using var client = before.CreateClient();
            (await client.GetAsync("/api/health", Ct)).EnsureSuccessStatusCode();
            runs.Add(await SeedRunAsync(before, "github:o/r#31", "Coding"));
            runs.Add(await SeedRunAsync(before, "github:o/r#32", "Reviewing"));
            await InsertAcceptedEventAsync(before, pendingEventId, "github:o/r#33");
        }

        // 1) GitHub se cuelga al publicar: cada aviso agota su plazo y el arranque sigue dentro del límite.
        gitHub.CommentPostDelay = TimeSpan.FromMinutes(5);
        SqliteConnection.ClearAllPools();
        await using (var hanging = CreateReadyFactory(gitHub))
        {
            var stopwatch = Stopwatch.StartNew();
            using var client = hanging.CreateAuthorizedClient();
            var health = await client.GetFromJsonAsync<HealthBody>("/api/health", Ct);
            stopwatch.Stop();
            gitHub.CommentPostDelay = TimeSpan.Zero;

            Assert.Equal("Ready", health!.Status);
            Assert.True(stopwatch.Elapsed < RunStartupReconciler.NotificationsBudget + TimeSpan.FromSeconds(5), $"El arranque tardó {stopwatch.Elapsed}.");
            foreach (var run in runs)
            {
                var events = await GetEventsAsync(client, run.Id);
                var notice = Assert.Single(events, e => e.GetProperty("type").GetString() is "IssueCommented" or "NotificationFailed");
                Assert.Equal("NotificationFailed", notice.GetProperty("type").GetString());
                Assert.Equal("RunFailed", notice.GetProperty("kind").GetString());
            }

            await using (var connection = await OpenReadOnlyAsync())
            {
                // El resultado se guarda aunque el plazo del llamante haya vencido: ninguna fila queda en Pending.
                Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM gh_notifications WHERE status = 'Pending';"));
            }

            // Ready: el Evento pendiente se despacha.
            var pending = await WaitForEventAsync(client, pendingEventId, e => e.RunId is not null || e.Status == "Ignored");
            Assert.Equal("Accepted", pending.Status);
            await WaitForRunAsync(client, pending.RunId!.Value, r => r.State is "PullRequestCreated" or "Failed" or "Cancelled");
        }

        // 2) Siguiente arranque con GitHub sano: se reintentan los avisos que no llegaron, una sola vez cada uno.
        SqliteConnection.ClearAllPools();
        await using var healthy = CreateReadyFactory(gitHub);
        using var healthyClient = healthy.CreateAuthorizedClient();
        foreach (var run in runs)
        {
            var events = await GetEventsAsync(healthyClient, run.Id);
            Assert.Contains(events, e => e.GetProperty("type").GetString() == "IssueCommented" && e.GetProperty("kind").GetString() == "RunFailed");
            var number = long.Parse(run.ObjectKey[(run.ObjectKey.IndexOf('#', StringComparison.Ordinal) + 1)..], System.Globalization.CultureInfo.InvariantCulture);
            Assert.Single(gitHub.CommentsOn("o", "r", number), c => c.Body.Contains(run.ShortId, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Notices_failing_with_5xx_are_retried_on_each_start_up_to_the_limit_and_never_block_Ready()
    {
        await using var gitHub = await FakeGitHub.StartAsync(Ct);
        PipelineRun run;
        PipelineRun alreadyNotified;
        await using (var before = CreateDiagnosticFactory())
        {
            using var client = before.CreateClient();
            (await client.GetAsync("/api/health", Ct)).EnsureSuccessStatusCode();
            run = await SeedRunAsync(before, "github:o/r#41", "Coding");

            // Cerrada por un reinicio anterior y ya avisada: no se vuelve a avisar.
            alreadyNotified = await SeedRunAsync(before, "github:o/r#42", "Failed", "HostRestarted");
            await before.Services.GetRequiredService<IRunEventSink>().AppendAsync(
                alreadyNotified.Id, null, new IssueCommented(NotificationKind.RunFailed, "https://github.com/o/r/issues/42#issuecomment-1"), Ct);
        }

        gitHub.CommentPostStatus = StatusCodes.Status502BadGateway;
        for (var start = 1; start <= RunStartupReconciler.MaxNotificationAttempts + 1; start++)
        {
            SqliteConnection.ClearAllPools();
            await using var factory = CreateReadyFactory(gitHub);
            using var client = factory.CreateAuthorizedClient();
            Assert.Equal("Ready", (await client.GetFromJsonAsync<HealthBody>("/api/health", Ct))!.Status);

            var failures = (await GetEventsAsync(client, run.Id)).Count(e => e.GetProperty("type").GetString() == "NotificationFailed");
            Assert.Equal(Math.Min(start, RunStartupReconciler.MaxNotificationAttempts), failures);
            Assert.Equal("Failed", (await GetRunAsync(client, run.Id)).State);
        }

        Assert.Empty(gitHub.CommentsOn("o", "r", 42));
        await using var connection = await OpenReadOnlyAsync();
        Assert.Equal("Failed", await ScalarAsync(connection, $"SELECT status FROM gh_notifications WHERE run_id = '{Upper(run.Id)}' AND kind = 'RunFailed';"));
        Assert.Equal(0L, await ScalarAsync(connection, $"SELECT COUNT(*) FROM gh_notifications WHERE run_id = '{Upper(alreadyNotified.Id)}';"));
    }

    // --- 3. Limpieza de Docker (motor falso) ----------------------------------------------------

    [Fact]
    public async Task Sandbox_cleanup_stops_and_removes_labelled_containers_and_workspace_volumes_but_never_tools_or_foreign_volumes()
    {
        await using var engine = await FakeDockerEngine.StartAsync();
        var (container1, container2, volume1, volume2) = SeedDocker(engine, out var reader);
        using var docker = new DockerClient(DockerEndpoint.Parse(engine.Endpoint));
        var reconciler = new SandboxReconciler(docker, reader, NullLogger<SandboxReconciler>.Instance);

        var summary = await reconciler.CleanupOrphansAsync(Ct);

        Assert.Equal(new SandboxCleanupSummary(2, 2), summary);
        var requests = engine.Requests.Select(r => r.Method + " " + r.Route).ToList();
        foreach (var id in new[] { container1, container2 })
        {
            Assert.Contains($"POST /containers/{id}/stop", requests);
            Assert.Contains($"DELETE /containers/{id}", requests);
            Assert.True(
                requests.IndexOf($"POST /containers/{id}/stop") < requests.IndexOf($"DELETE /containers/{id}"),
                "Cada contenedor se detiene antes de borrarlo.");
        }

        Assert.Contains($"DELETE /volumes/{volume1}", requests);
        Assert.Contains($"DELETE /volumes/{volume2}", requests);
        Assert.DoesNotContain(requests, r => r.Contains("foreign", StringComparison.Ordinal));
        Assert.DoesNotContain(requests, r => r.Contains(SandboxManager.ToolsVolume, StringComparison.Ordinal));
        Assert.DoesNotContain(requests, r => r.Contains("postgres-data", StringComparison.Ordinal));
        Assert.Equal(
            new[] { SandboxManager.ToolsVolume, OtherInstanceVolume, ActiveRunVolume, "postgres-data" }.Order(StringComparer.Ordinal),
            engine.ListedVolumes.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(
            new[] { ActiveRunContainer, "foreign", OtherInstanceContainer }.Order(StringComparer.Ordinal),
            engine.ListedContainers.Keys.Order(StringComparer.Ordinal));
        Assert.DoesNotContain(requests, r => r.Contains(OtherInstanceContainer, StringComparison.Ordinal) || r.Contains(ActiveRunContainer, StringComparison.Ordinal));
        Assert.DoesNotContain(requests, r => r.Contains(OtherInstanceVolume, StringComparison.Ordinal) || r.Contains(ActiveRunVolume, StringComparison.Ordinal));
        Assert.Contains(engine.Requests, r => r.Route == "/containers/json" && Uri.UnescapeDataString(r.Query).Contains(SandboxManager.RunLabel, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sandbox_cleanup_tolerates_partial_failures_and_keeps_going()
    {
        await using var engine = await FakeDockerEngine.StartAsync();
        var (container1, container2, volume1, volume2) = SeedDocker(engine, out var reader);
        engine.FailingRemovals[container1] = 0;
        engine.FailingRemovals[volume1] = 0;
        using var docker = new DockerClient(DockerEndpoint.Parse(engine.Endpoint));
        var reconciler = new SandboxReconciler(docker, reader, NullLogger<SandboxReconciler>.Instance);

        var summary = await reconciler.CleanupOrphansAsync(Ct);

        Assert.Equal(new SandboxCleanupSummary(1, 1), summary);
        Assert.Contains(container1, engine.ListedContainers.Keys);
        Assert.DoesNotContain(container2, engine.ListedContainers.Keys);
        Assert.Contains(volume1, engine.ListedVolumes.Keys);
        Assert.Contains(SandboxManager.ToolsVolume, engine.ListedVolumes.Keys);
        Assert.DoesNotContain(volume2, engine.ListedVolumes.Keys);
    }

    [Fact]
    public async Task Sandbox_cleanup_never_throws_when_Docker_fails_or_does_not_respond()
    {
        await using var engine = await FakeDockerEngine.StartAsync();
        SeedDocker(engine, out var reader);
        engine.FailEverything = true;
        using (var failing = new DockerClient(DockerEndpoint.Parse(engine.Endpoint)))
        {
            Assert.Equal(new SandboxCleanupSummary(0, 0), await new SandboxReconciler(failing, reader, NullLogger<SandboxReconciler>.Instance).CleanupOrphansAsync(Ct));
        }

        using var unreachable = new DockerClient(DockerEndpoint.Parse(ClosedEndpoint()));
        Assert.Equal(new SandboxCleanupSummary(0, 0), await new SandboxReconciler(unreachable, reader, NullLogger<SandboxReconciler>.Instance).CleanupOrphansAsync(Ct));
    }

    [Theory]
    [InlineData("codenetix-ws-0190a5f4-0000-7000-8000-000000000001", true)]
    [InlineData("codenetix-tools", false)]
    [InlineData("postgres-data", false)]
    [InlineData("my-codenetix-ws-x", false)]
    [InlineData("CODENETIX-WS-x", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_codenetix_ws_volumes_are_workspace_volumes(string? name, bool expected) =>
        Assert.Equal(expected, SandboxReconciler.IsWorkspaceVolume(name));

    // --- 4. Directorios huérfanos en workspaces/ --------------------------------------------------

    [Fact]
    public async Task Restart_removes_orphan_workspace_trees_and_leaves_anything_that_is_not_a_run_tree()
    {
        var orphanA = Path.Combine(WorkspacesPath, "0123abcd");
        var orphanB = Path.Combine(WorkspacesPath, "deadbeef");
        var foreign = Path.Combine(WorkspacesPath, "not-a-run");
        Directory.CreateDirectory(Path.Combine(orphanA, ".git", "objects", "ab"));
        var readOnlyObject = Path.Combine(orphanA, ".git", "objects", "ab", "cdef");
        File.WriteAllText(readOnlyObject, "objeto");
        File.SetAttributes(readOnlyObject, FileAttributes.ReadOnly);
        Directory.CreateDirectory(orphanB);
        File.WriteAllText(Path.Combine(orphanB, "Program.cs"), "// residuo");
        Directory.CreateDirectory(foreign);
        File.WriteAllText(Path.Combine(foreign, "keep.txt"), "no es de CodeNetix");
        File.WriteAllText(Path.Combine(WorkspacesPath, "notes.txt"), "tampoco");

        // Un árbol con un archivo bloqueado (en Windows no se puede borrar): se registra y el arranque sigue.
        var locked = Path.Combine(WorkspacesPath, "cafebabe");
        Directory.CreateDirectory(locked);
        await using var handle = new FileStream(Path.Combine(locked, "locked.bin"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);

        await using var factory = CreateReadyFactory();
        using var client = factory.CreateClient();
        var health = await client.GetFromJsonAsync<HealthBody>("/api/health", Ct);

        Assert.Equal("Ready", health!.Status);
        Assert.False(Directory.Exists(orphanA));
        Assert.False(Directory.Exists(orphanB));
        Assert.True(File.Exists(Path.Combine(foreign, "keep.txt")));
        Assert.True(File.Exists(Path.Combine(WorkspacesPath, "notes.txt")));
        if (OperatingSystem.IsWindows())
        {
            Assert.True(File.Exists(Path.Combine(locked, "locked.bin")));
        }
    }

    [Fact]
    public async Task Workspace_cleanup_never_follows_a_link_named_like_a_run_tree()
    {
        var sentinel = Path.Combine(_root, "outside-sentinel");
        Directory.CreateDirectory(sentinel);
        var sentinelFile = Path.Combine(sentinel, "keep.txt");
        File.WriteAllText(sentinelFile, "fuera de workspaces/");
        File.SetAttributes(sentinelFile, FileAttributes.ReadOnly);
        Directory.CreateDirectory(WorkspacesPath);
        var link = Path.Combine(WorkspacesPath, "abcd1234");
        if (!TryCreateDirectoryLink(link, sentinel))
        {
            Assert.Skip("No se pueden crear enlaces de directorio en este sistema.");
        }

        await using var factory = CreateReadyFactory();
        using var client = factory.CreateClient();
        Assert.Equal("Ready", (await client.GetFromJsonAsync<HealthBody>("/api/health", Ct))!.Status);

        Assert.True(File.Exists(sentinelFile), "El destino del enlace no debe tocarse.");
        Assert.Equal(FileAttributes.ReadOnly, File.GetAttributes(sentinelFile) & FileAttributes.ReadOnly);
        Assert.True(Directory.Exists(link) || File.Exists(link), "El enlace no es un árbol de CodeNetix y se deja.");
    }

    /// <summary>Enlace simbólico de directorio o, en Windows sin privilegio, una junction (<c>mklink /J</c>).</summary>
    private static bool TryCreateDirectoryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }
        }

        using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, target])
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        if (mklink is null)
        {
            return false;
        }

        mklink.WaitForExit(10_000);
        return mklink.HasExited && mklink.ExitCode == 0 && Directory.Exists(link);
    }

    // --- 5. Arranque en Diagnostic con Docker caído -----------------------------------------------

    [Fact]
    public async Task Diagnostic_restart_with_Docker_down_still_fails_the_run_without_waking_the_Dispatcher()
    {
        PipelineRun coding;
        var pendingEventId = Guid.CreateVersion7();
        await using (var before = CreateDiagnosticFactory())
        {
            using var client = before.CreateClient();
            (await client.GetAsync("/api/health", Ct)).EnsureSuccessStatusCode();
            coding = await SeedRunAsync(before, "github:o/r#7", "Coding");
            await InsertAcceptedEventAsync(before, pendingEventId, "github:o/r#8");
        }

        SqliteConnection.ClearAllPools();
        var settings = new Dictionary<string, string?> { ["CODENETIX:Sandbox:DockerEndpoint"] = ClosedEndpoint() };
        await using var baseFactory = CreateDiagnosticFactory(settings);
        baseFactory.SandboxHealth.Reasons = ["Docker no responde (simulado)"];

        // La limpieza real (no la falsa de la fábrica), contra un endpoint en el que no escucha nadie.
        await using var factory = baseFactory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
            services.AddSingleton<ISandboxReconciler>(sp => ActivatorUtilities.CreateInstance<SandboxReconciler>(sp))));
        using var restartedClient = Authorized(factory);

        var health = await restartedClient.GetFromJsonAsync<HealthBody>("/api/health", Ct);
        Assert.Equal("Diagnostic", health!.Status);
        Assert.Contains("Docker no responde (simulado)", health.Reasons);

        var run = await GetRunAsync(restartedClient, coding.Id);
        Assert.Equal("Failed", run.State);
        Assert.Equal("HostRestarted", run.FailureReason);
        var events = await GetEventsAsync(restartedClient, coding.Id);
        Assert.Contains(events, e => e.GetProperty("type").GetString() == "StateChanged"
            && e.GetProperty("from").GetString() == "Coding"
            && e.GetProperty("to").GetString() == "Failed");
        Assert.Contains(events, e => e.GetProperty("type").GetString() is "IssueCommented" or "NotificationFailed"
            && e.GetProperty("kind").GetString() == "RunFailed");

        // En Diagnostic el Dispatcher no arranca: el Evento pendiente sigue sin Ejecución (y sin descartarse).
        factory.Services.GetRequiredService<IEventDispatchSignal>().Notify();
        await Task.Delay(TimeSpan.FromSeconds(1.5), Ct);
        await using var connection = await OpenReadOnlyAsync();
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM run_runs;"));
        Assert.Equal(
            "Accepted",
            await ScalarAsync(connection, $"SELECT status FROM ing_events WHERE id = '{Upper(pendingEventId)}' AND dispatched_at IS NULL;"));
    }

    // --- Utilidades -----------------------------------------------------------------------------

    private const string OtherInstanceContainer = "cccc-other-instance";
    private const string ActiveRunContainer = "dddd-active-run";
    private static readonly Guid OtherInstanceRun = Guid.Parse("0190a5f4-0000-7000-8000-00000000aaaa");
    private static readonly Guid ActiveRun = Guid.Parse("0190a5f4-0000-7000-8000-00000000bbbb");
    private static readonly string OtherInstanceVolume = SandboxManager.VolumeNameOf(OtherInstanceRun);
    private static readonly string ActiveRunVolume = SandboxManager.VolumeNameOf(ActiveRun);

    /// <summary>
    /// Motor falso con 2 contenedores y 2 volúmenes de Ejecuciones terminales de esta instancia, <c>codenetix-tools</c>,
    /// recursos ajenos, de otra instancia (runId desconocido) y de una Ejecución aún activa: solo se borran los 4 primeros.
    /// </summary>
    private static (string Container1, string Container2, string Volume1, string Volume2) SeedDocker(FakeDockerEngine engine, out FakeRunReader reader)
    {
        reader = new FakeRunReader();
        var run1 = reader.Add(RunState.Failed);
        var run2 = reader.Add(RunState.PullRequestCreated);
        var run3 = reader.Add(RunState.Cancelled);
        var run4 = reader.Add(RunState.Failed);
        reader.Add(RunState.Coding, ActiveRun);

        var container1 = "aaaa" + Guid.NewGuid().ToString("N");
        var container2 = "bbbb" + Guid.NewGuid().ToString("N");
        engine.ListedContainers[container1] = new Dictionary<string, string> { [SandboxManager.RunLabel] = run1.ToString("D") };
        engine.ListedContainers[container2] = new Dictionary<string, string> { [SandboxManager.RunLabel] = run2.ToString("D") };
        engine.ListedContainers["foreign"] = new Dictionary<string, string> { ["com.example.app"] = "postgres" };
        engine.ListedContainers[OtherInstanceContainer] = new Dictionary<string, string> { [SandboxManager.RunLabel] = OtherInstanceRun.ToString("D") };
        engine.ListedContainers[ActiveRunContainer] = new Dictionary<string, string> { [SandboxManager.RunLabel] = ActiveRun.ToString("D") };
        var volume1 = SandboxManager.VolumeNameOf(run3);
        var volume2 = SandboxManager.VolumeNameOf(run4);
        engine.ListedVolumes[volume1] = 0;
        engine.ListedVolumes[volume2] = 0;
        engine.ListedVolumes[SandboxManager.ToolsVolume] = 0;
        engine.ListedVolumes["postgres-data"] = 0;
        engine.ListedVolumes[OtherInstanceVolume] = 0;
        engine.ListedVolumes[ActiveRunVolume] = 0;
        return (container1, container2, volume1, volume2);
    }

    /// <summary><see cref="IRunReader"/> falso: solo <see cref="FindAsync"/> por GUID.</summary>
    private sealed class FakeRunReader : IRunReader
    {
        private readonly ConcurrentDictionary<Guid, RunSummary> _runs = new();

        public Guid Add(RunState state, Guid? id = null)
        {
            var runId = id ?? Guid.CreateVersion7();
            _runs[runId] = new RunSummary(runId, RunShortId.Of(runId), state, null, null, Guid.CreateVersion7(), "github:o/r#1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            return runId;
        }

        public Task<RunSummary?> FindAsync(string id, CancellationToken cancellationToken) =>
            Task.FromResult(Guid.TryParse(id, out var runId) && _runs.TryGetValue(runId, out var run) ? run : null);

        public Task<IReadOnlyList<RunSummary>> ListRecentAsync(int limit, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<RunEvent>> GetEventsAsync(Guid runId, long afterSeq, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Spec?> GetSpecAsync(Guid runId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TestReport?> GetTestReportAsync(Guid runId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<RunVerdict>> GetVerdictsAsync(Guid runId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    /// <summary><c>tcp://127.0.0.1:&lt;puerto&gt;</c> en el que no escucha nadie (Docker caído).</summary>
    private static string ClosedEndpoint()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return $"tcp://127.0.0.1:{port}";
    }

    /// <summary>Ejecución sembrada como la crea el Dispatcher y llevada a <paramref name="state"/> directamente en SQLite.</summary>
    private static async Task<PipelineRun> SeedRunAsync(WebApplicationFactory<Program> factory, string objectKey, string state, string? failureReason = null)
    {
        var contexts = factory.Services.GetRequiredService<IDbContextFactory<PipelineDbContext>>();
        var log = factory.Services.GetRequiredService<RunEventLog>();
        await using var context = await contexts.CreateDbContextAsync(Ct);
        var (run, created, initial) = PipelineRun.Create(Guid.CreateVersion7(), objectKey, DateTimeOffset.UtcNow);
        context.Runs.Add(run);
        await log.AddAsync(context, run.Id, created, Ct);
        await log.AddAsync(context, run.Id, initial, Ct);
        await context.SaveChangesAsync(Ct);
        if (state != "Received")
        {
#pragma warning disable EF1002 // Valores fijos de la prueba, no entrada externa.
            var reason = failureReason is null ? "NULL" : $"'{failureReason}'";
            await context.Database.ExecuteSqlRawAsync($"UPDATE run_runs SET state = '{state}', failure_reason = {reason} WHERE id = '{Upper(run.Id)}';", Ct);
#pragma warning restore EF1002
        }

        return run;
    }

    private static async Task InsertAcceptedEventAsync(WebApplicationFactory<Program> factory, Guid eventId, string objectKey)
    {
        var contexts = factory.Services.GetRequiredService<IDbContextFactory<IngestionDbContext>>();
        await using var context = await contexts.CreateDbContextAsync(Ct);
        context.Events.Add(new EventRecord
        {
            Id = eventId,
            Source = EventSource.Manual,
            ObjectKey = objectKey,
            DedupKey = "test:" + eventId,
            PayloadJson = "{}",
            Status = EventStatus.Accepted,
            ReceivedAt = DateTimeOffset.UtcNow,
        });
        await context.SaveChangesAsync(Ct);
    }

    /// <summary>Sustituye el último registro de <typeparamref name="T"/> por un decorador que envuelve el original.</summary>
    private static void Decorate<T>(IServiceCollection services, Func<T, IServiceProvider, T> wrap)
        where T : class
    {
        var descriptor = services.Last(d => d.ServiceType == typeof(T));
        services.Remove(descriptor);
        services.AddSingleton(sp =>
        {
            var inner = descriptor.ImplementationInstance as T
                ?? (descriptor.ImplementationFactory is { } create
                    ? (T)create(sp)
                    : (T)ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType!));
            return wrap(inner, sp);
        });
    }

    private static HttpClient Authorized(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", CodeNetixFactory.TestAccessToken);
        return client;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string message)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), message);
            await Task.Delay(25, Ct);
        }
    }

    private static async Task<RunBody> GetRunAsync(HttpClient client, Guid id)
    {
        using var response = await client.GetAsync($"/api/runs/{id}", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<RunBody>(Ct))!;
    }

    private static async Task<RunBody> WaitForRunAsync(HttpClient client, Guid id, Func<RunBody, bool> done)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            var run = await GetRunAsync(client, id);
            var types = (await GetEventsAsync(client, id)).Select(e => e.GetProperty("type").GetString()).ToList();
            var settled = !types.Contains("SandboxCreated") || types.Contains("SandboxReleased");
            if (done(run) && settled)
            {
                return run;
            }

            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15), $"La Ejecución {id} sigue en {run.State}.");
            await Task.Delay(50, Ct);
        }
    }

    private static async Task<List<JsonElement>> GetEventsAsync(HttpClient client, Guid id)
    {
        using var response = await client.GetAsync($"/api/runs/{id}/events", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return [.. json.RootElement.EnumerateArray().Select(e => e.Clone())];
    }

    private static async Task<EventBody> WaitForEventAsync(HttpClient client, Guid eventId, Func<EventBody, bool> done)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            using var response = await client.GetAsync($"/api/events/{eventId}", Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = (await response.Content.ReadFromJsonAsync<EventBody>(Ct))!;
            if (done(body))
            {
                return body;
            }

            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"El Evento {eventId} no llegó al estado esperado (status={body.Status}).");
            await Task.Delay(50, Ct);
        }
    }

    private async Task<SqliteConnection> OpenReadOnlyAsync()
    {
        var connection = new SqliteConnection($"Data Source={DatabasePath};Mode=ReadOnly");
        await connection.OpenAsync(Ct);
        return connection;
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(Ct);
        return value is DBNull ? null : value;
    }

    private static string Upper(Guid id) => id.ToString().ToUpperInvariant();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
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

    private sealed record HealthBody(string Status, IReadOnlyList<string> Reasons);

    private sealed record EventBody(Guid EventId, string? Status, string? Reason, Guid? RunId);

    private sealed record RunBody(
        Guid RunId,
        string ShortId,
        string State,
        string? FailureReason,
        string? FailureDetail,
        string ObjectKey,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);

    /// <summary>Último módulo en migrarse (orden máximo): marca el fin de las migraciones.</summary>
    private sealed class SpyModuleDatabase(Action onMigrate) : IModuleDatabase
    {
        public string Prefix => "zzspy";

        public int Order => int.MaxValue;

        public Task MigrateAsync(CancellationToken cancellationToken)
        {
            onMigrate();
            return Task.CompletedTask;
        }
    }

    private sealed class SpyHealthCheck(Action onCheck) : ISandboxHealthCheck
    {
        public Task<IReadOnlyList<string>> CheckAsync(CancellationToken cancellationToken)
        {
            onCheck();
            return Task.FromResult<IReadOnlyList<string>>([]);
        }
    }

    private sealed class SpyRunReconciler(IRunStartupReconciler inner, Action onReconcile) : IRunStartupReconciler
    {
        public Task<RunReconciliationResult> ReconcileNonTerminalRunsAsync(CancellationToken cancellationToken)
        {
            onReconcile();
            return inner.ReconcileNonTerminalRunsAsync(cancellationToken);
        }
    }

    private sealed class SpyWorkspace(IRepositoryWorkspace inner, Action onCleanup) : IRepositoryWorkspace
    {
        public Task<PreparedRepository> PrepareAsync(Guid runId, string owner, string repo, CancellationToken cancellationToken) =>
            inner.PrepareAsync(runId, owner, repo, cancellationToken);

        public Task<IReadOnlyList<string>> ListFilesAsync(Guid runId, CancellationToken cancellationToken) =>
            inner.ListFilesAsync(runId, cancellationToken);

        public Task ExportTreeAsync(Guid runId, Stream destination, CancellationToken cancellationToken) =>
            inner.ExportTreeAsync(runId, destination, cancellationToken);

        public Task<DiffResult> ComputeDiffAsync(Guid runId, Stream workspaceTar, string baseSha, CancellationToken cancellationToken) =>
            inner.ComputeDiffAsync(runId, workspaceTar, baseSha, cancellationToken);

        public Task DeleteAsync(Guid runId, CancellationToken cancellationToken) => inner.DeleteAsync(runId, cancellationToken);

        public Task<int> CleanupOrphansAsync(CancellationToken cancellationToken)
        {
            onCleanup();
            return inner.CleanupOrphansAsync(cancellationToken);
        }
    }

    /// <summary>Registra la primera petición del Dispatcher (el inicio de su bucle de despacho).</summary>
    private sealed class SpyEventSource(IAcceptedEventSource inner, Action onFirstPoll) : IAcceptedEventSource
    {
        private int _polled;

        public Task<IReadOnlyList<PendingEvent>> GetPendingAsync(int max, IReadOnlyCollection<Guid> exclude, CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _polled, 1) == 0)
            {
                onFirstPoll();
            }

            return inner.GetPendingAsync(max, exclude, cancellationToken);
        }

        public Task MarkDispatchedAsync(Guid eventId, Guid runId, CancellationToken cancellationToken) =>
            inner.MarkDispatchedAsync(eventId, runId, cancellationToken);

        public Task MarkIgnoredAsync(Guid eventId, string reason, CancellationToken cancellationToken) =>
            inner.MarkIgnoredAsync(eventId, reason, cancellationToken);
    }
}
