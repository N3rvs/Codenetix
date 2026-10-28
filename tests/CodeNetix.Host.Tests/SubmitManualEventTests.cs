using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CodeNetix.Contracts;
using CodeNetix.Host.Configuration;
using CodeNetix.Host.Features.SubmitManualEvent;
using CodeNetix.Ingestion.Data;
using CodeNetix.Ingestion.Features.SubmitManualEvent;
using CodeNetix.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeNetix.Host.Tests;

/// <summary>Matriz de la historia 1.4 contra el host: <c>POST /api/events/manual</c> e <c>ing_events</c>.</summary>
public sealed class SubmitManualEventTests : IDisposable
{
    private const string Route = SubmitManualEventEndpoint.Route;
    private const string IssueUrl = "https://github.com/O/R/issues/12";
    private const string CanonicalUrl = "https://github.com/o/r/issues/12";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "codenetix-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _dataDirectory;
    private readonly string _webRoot;

    public SubmitManualEventTests()
    {
        _dataDirectory = Path.Combine(_root, "data");
        _webRoot = Path.Combine(_root, "wwwroot");
        Directory.CreateDirectory(_webRoot);
        File.WriteAllText(Path.Combine(_webRoot, "index.html"), "<!doctype html><html><body>Panel</body></html>");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string DatabasePath => Path.Combine(_dataDirectory, PersistenceOptions.DatabaseFileName);

    private CodeNetixFactory CreateReadyFactory() =>
        new(_dataDirectory, _webRoot, new Dictionary<string, string?>(CodeNetixFactory.CompleteSettings));

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string? url, string? requestId) =>
        client.PostAsJsonAsync(Route, new { url, requestId }, Ct);

    // --- Matriz ------------------------------------------------------------------

    [Fact]
    public async Task Valid_issue_creates_one_accepted_event()
    {
        await using var factory = CreateReadyFactory();
        using var client = factory.CreateAuthorizedClient();
        var requestId = Guid.NewGuid();

        using var response = await PostAsync(client, IssueUrl, requestId.ToString());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<SubmitBody>(Ct);
        Assert.NotNull(body);
        Assert.True(body.Created);
        Assert.Equal(7, body.EventId.Version);
        Assert.Equal("Accepted", body.Status);
        Assert.Null(body.Reason);

        var row = Assert.Single(await ReadEventsAsync());
        Assert.Equal(body.EventId, Guid.Parse(row.Id));
        Assert.Equal("Manual", row.Source);
        Assert.Equal("github:o/r#12", row.ObjectKey);
        Assert.Equal($"manual:{CanonicalUrl}:{requestId:D}", row.DedupKey);
        Assert.Equal(FakeGitHub.DefaultLogin, row.Actor);
        Assert.Equal("Accepted", row.Status);
        Assert.Null(row.IgnoredReason);
        var receivedAt = new DateTimeOffset(row.ReceivedAtTicks, TimeSpan.Zero); // ticks UTC
        Assert.InRange(receivedAt, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));

        using var payload = JsonDocument.Parse(row.PayloadJson);
        Assert.Equal(CanonicalUrl, payload.RootElement.GetProperty("url").GetString());
        Assert.Equal(requestId, payload.RootElement.GetProperty("requestId").GetGuid());
        Assert.DoesNotContain(CodeNetixFactory.TestAccessToken, row.PayloadJson, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://www.github.com/O/R/issues/12")]
    [InlineData("https://github.com/O/R/issues/12/")]
    [InlineData("https://github.com/O/R/issues/12?x=1")]
    [InlineData("https://github.com/O/R/issues/12#c")]
    public async Task Url_variants_share_the_canonical_url(string variant)
    {
        await using var factory = CreateReadyFactory();
        using var client = factory.CreateAuthorizedClient();
        var requestId = Guid.NewGuid().ToString();

        using var first = await PostAsync(client, IssueUrl, requestId);
        using var second = await PostAsync(client, variant, requestId);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode); // misma clave canónica
        var row = Assert.Single(await ReadEventsAsync());
        Assert.StartsWith($"manual:{CanonicalUrl}:", row.DedupKey, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Same_requestId_returns_200_with_the_same_event()
    {
        await using var factory = CreateReadyFactory();
        using var client = factory.CreateAuthorizedClient();
        var requestId = Guid.NewGuid().ToString();

        using var first = await PostAsync(client, IssueUrl, requestId);
        using var second = await PostAsync(client, IssueUrl, requestId);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var a = await first.Content.ReadFromJsonAsync<SubmitBody>(Ct);
        var b = await second.Content.ReadFromJsonAsync<SubmitBody>(Ct);
        Assert.False(b!.Created);
        Assert.Equal(a!.EventId, b.EventId);
        Assert.Single(await ReadEventsAsync());
    }

    [Fact]
    public async Task Different_requestId_creates_a_second_event()
    {
        await using var factory = CreateReadyFactory();
        using var client = factory.CreateAuthorizedClient();

        using var first = await PostAsync(client, IssueUrl, Guid.NewGuid().ToString());
        using var second = await PostAsync(client, IssueUrl, Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(2, (await ReadEventsAsync()).Count);
    }

    [Theory]
    [InlineData("https://github.com/o/r/pull/3")]
    [InlineData("https://gitlab.com/o/r/-/issues/1")]
    [InlineData("foo")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Unsupported_url_is_400_with_explanation_and_persists_nothing(string? url)
    {
        await using var factory = CreateReadyFactory();
        using var client = factory.CreateAuthorizedClient();

        using var response = await PostAsync(client, url, Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ReadProblemAsync(response);
        Assert.Contains("https://github.com/<owner>/<repo>/issues/<número>", problem.Detail, StringComparison.Ordinal);
        Assert.Empty(await ReadEventsAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Missing_or_invalid_requestId_is_400(string? requestId)
    {
        await using var factory = CreateReadyFactory();
        using var client = factory.CreateAuthorizedClient();

        using var response = await PostAsync(client, IssueUrl, requestId);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ReadProblemAsync(response);
        Assert.Contains("requestId", problem.Detail, StringComparison.Ordinal);
        Assert.Empty(await ReadEventsAsync());
    }

    [Theory]
    [InlineData("{ not json", "application/json")]
    [InlineData("{\"url\":\"https://github.com/o/r/issues/1\",\"requestId\":42}", "application/json")]
    [InlineData("url=https://github.com/o/r/issues/1", "text/plain")]
    public async Task Malformed_body_is_400_ProblemDetails(string content, string contentType)
    {
        await using var factory = CreateReadyFactory();
        using var client = factory.CreateAuthorizedClient();

        using var response = await client.PostAsync(Route, new StringContent(content, Encoding.UTF8, contentType), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Empty(await ReadEventsAsync());
    }

    [Fact]
    public async Task Sentry_url_is_501_and_persists_nothing()
    {
        await using var factory = CreateReadyFactory();
        using var client = factory.CreateAuthorizedClient();

        using var response = await PostAsync(client, "https://acme.sentry.io/issues/123/", Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        var problem = await ReadProblemAsync(response);
        Assert.Equal("Soporte de Sentry disponible próximamente.", problem.Detail);
        Assert.Empty(await ReadEventsAsync());
    }

    [Fact]
    public async Task Diagnostic_instance_is_503_with_reasons_and_persists_nothing()
    {
        await using var factory = new CodeNetixFactory(_dataDirectory, _webRoot); // sin keys -> Diagnostic
        using var client = factory.CreateAuthorizedClient();

        using var response = await PostAsync(client, IssueUrl, Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var problem = await ReadProblemAsync(response);
        Assert.Contains("CODENETIX__GitHub__Token", problem.Detail, StringComparison.Ordinal);
        Assert.Equal("Diagnostic", problem.InstanceStatus);
        Assert.Equal(2, problem.Reasons?.Count);
        Assert.Empty(await ReadEventsAsync());
    }

    [Fact]
    public async Task Without_token_is_401()
    {
        await using var factory = CreateReadyFactory();
        using var client = factory.CreateClient();

        using var response = await PostAsync(client, IssueUrl, Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await ReadEventsAsync());
    }

    [Fact]
    public async Task Concurrent_posts_with_the_same_requestId_create_one_row()
    {
        await using var factory = CreateReadyFactory();
        using var client = factory.CreateAuthorizedClient();
        var requestId = Guid.NewGuid().ToString();

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => PostAsync(client, IssueUrl, requestId), Ct)));
        try
        {
            Assert.All(responses, r => Assert.True(r.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK, r.StatusCode.ToString()));
            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
            var ids = await Task.WhenAll(responses.Select(async r => (await r.Content.ReadFromJsonAsync<SubmitBody>(Ct))!.EventId));
            Assert.Single(ids.Distinct());
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }

        Assert.Single(await ReadEventsAsync());
    }

    [Fact]
    public async Task Concurrent_submissions_resolve_unique_violations_to_the_existing_event()
    {
        // Directo contra IManualEventIngestion: muchas inserciones a la vez fuerzan el camino de la violación UNIQUE.
        await using var factory = CreateReadyFactory();
        _ = factory.Server;
        var ingestion = factory.Services.GetRequiredService<IManualEventIngestion>();
        var requestId = Guid.NewGuid();

        var results = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() => ingestion.SubmitAsync(IssueUrl, requestId, Ct), Ct)));

        Assert.Single(results, r => r.Outcome == ManualEventOutcome.Created);
        Assert.All(results, r => Assert.True(r.Outcome is ManualEventOutcome.Created or ManualEventOutcome.Existing));
        Assert.Single(results.Select(r => r.EventId).Distinct());
        Assert.Single(await ReadEventsAsync());
    }

    [Fact]
    public async Task Unique_violation_after_lookup_returns_the_winners_event()
    {
        await using var factory = CreateReadyFactory();
        _ = factory.Server; // arranca y migra
        var connectionString = factory.Services.GetRequiredService<IDatabaseConnection>().ConnectionString;
        var winnerId = Guid.CreateVersion7();

        var options = new DbContextOptionsBuilder<IngestionDbContext>();
        IngestionDbContext.Configure(options, connectionString);
        options.AddInterceptors(new ConflictingInsertInterceptor(connectionString, winnerId));
        var ingestion = new ManualEventIngestion(
            new FixedContextFactory(options.Options),
            factory.Services.GetRequiredService<IGitHubIdentity>(),
            factory.Services.GetRequiredService<IRepositoryPermissions>(),
            factory.Services.GetRequiredService<IEventDispatchSignal>(),
            TimeProvider.System);

        var result = await ingestion.SubmitAsync(IssueUrl, Guid.NewGuid(), Ct);

        Assert.Equal(ManualEventOutcome.Existing, result.Outcome);
        Assert.Equal(winnerId, result.EventId);
        var row = Assert.Single(await ReadEventsAsync());
        Assert.Equal(winnerId, Guid.Parse(row.Id));
    }

    [Fact]
    public async Task Manual_event_while_Starting_is_503()
    {
        // Host mínimo: InstanceHealth sin marcar (Starting), sin StartupReconciler.
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<InstanceHealth>();
        var ingestion = new RecordingIngestion();
        builder.Services.AddSingleton<IManualEventIngestion>(ingestion);
        builder.Services.AddProblemDetails();
        await using var app = builder.Build();
        app.MapSubmitManualEvent();
        await app.StartAsync(Ct);
        using var client = app.GetTestClient();

        using var response = await PostAsync(client, IssueUrl, Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var problem = await ReadProblemAsync(response);
        Assert.Equal("Starting", problem.InstanceStatus);
        Assert.Equal(0, ingestion.Calls);
    }

    [Fact]
    public async Task Received_at_can_be_filtered_and_ordered_through_EF()
    {
        await using var factory = CreateReadyFactory();
        _ = factory.Server;
        var contexts = factory.Services.GetRequiredService<IDbContextFactory<IngestionDbContext>>();
        var t0 = new DateTimeOffset(2026, 10, 27, 10, 0, 0, TimeSpan.Zero);

        await using (var context = await contexts.CreateDbContextAsync(Ct))
        {
            // Mismo instante expresado con otros desplazamientos: se compara por instante UTC.
            context.Events.AddRange(
                Record("late", t0.AddHours(2).ToOffset(TimeSpan.FromHours(5))),
                Record("early", t0.AddHours(-1)),
                Record("middle", t0.AddMinutes(30).ToOffset(TimeSpan.FromHours(-3))));
            await context.SaveChangesAsync(Ct);
        }

        await using (var context = await contexts.CreateDbContextAsync(Ct))
        {
            var after = await context.Events
                .Where(e => e.ReceivedAt >= t0)
                .OrderBy(e => e.ReceivedAt)
                .Select(e => new { e.ObjectKey, e.ReceivedAt })
                .ToListAsync(Ct);

            Assert.Equal(["middle", "late"], after.Select(e => e.ObjectKey));
            Assert.Equal(t0.AddMinutes(30), after[0].ReceivedAt);
            Assert.All(after, e => Assert.Equal(TimeSpan.Zero, e.ReceivedAt.Offset));

            var latest = await context.Events.OrderByDescending(e => e.ReceivedAt).Select(e => e.ObjectKey).FirstAsync(Ct);
            Assert.Equal("late", latest);
        }

        static EventRecord Record(string objectKey, DateTimeOffset receivedAt) => new()
        {
            Id = Guid.CreateVersion7(),
            Source = EventSource.Manual,
            ObjectKey = objectKey,
            DedupKey = "test:" + objectKey,
            PayloadJson = "{}",
            Status = EventStatus.Accepted,
            ReceivedAt = receivedAt,
        };
    }

    // --- Migraciones ---------------------------------------------------------------

    [Fact]
    public async Task Failing_module_migration_aborts_startup_and_logs_the_prefix()
    {
        var logs = new LevelCapturingLoggerProvider();
        await using var factory = CreateReadyFactory();
        await using var failing = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.AddSingleton<IModuleDatabase, ThrowingModuleDatabase>();
            s.AddSingleton<ILoggerProvider>(logs);
        }));

        Assert.ThrowsAny<Exception>(() => failing.CreateClient());

        var critical = logs.Entries.Where(e => e.Level == LogLevel.Critical).Select(e => e.Message).ToList();
        Assert.Contains(critical, m => m.Contains($"'{ThrowingModuleDatabase.FailingPrefix}'", StringComparison.Ordinal)
            && m.Contains("migración rota de prueba", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Duplicate_module_prefix_fails_in_MigrateAllAsync()
    {
        var migrator = new DatabaseMigrator([new ThrowingModuleDatabase(), new ThrowingModuleDatabase()]);

        var ex = await Assert.ThrowsAsync<DatabaseMigrationException>(() => migrator.MigrateAllAsync(Ct));

        Assert.Equal(ThrowingModuleDatabase.FailingPrefix, ex.Prefix);
        Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Contains("prefijo", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Database_from_1_3_is_migrated_without_losing_data()
    {
        // Base de datos como la dejaba 1.3: WAL, sin tablas de módulos. Se añade un dato para comprobar que se conserva.
        var database = new SqliteDatabase(Options.Create(new PersistenceOptions { DataDirectory = _dataDirectory }));
        await database.InitializeAsync(Ct);
        await using (var connection = await database.OpenConnectionAsync(Ct))
        {
            await ExecuteAsync(connection, "CREATE TABLE legacy_marker (value TEXT NOT NULL); INSERT INTO legacy_marker VALUES ('kept');");
        }

        SqliteConnection.ClearAllPools();

        await using (var factory = CreateReadyFactory())
        {
            using var client = factory.CreateAuthorizedClient();
            using var response = await PostAsync(client, IssueUrl, Guid.NewGuid().ToString());
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        await using var check = new SqliteConnection($"Data Source={DatabasePath}");
        await check.OpenAsync(Ct);
        Assert.Equal("kept", await ScalarAsync(check, "SELECT value FROM legacy_marker;"));
        Assert.Equal(1L, await ScalarAsync(check, "SELECT COUNT(*) FROM ing_events;"));
        Assert.Equal("wal", await ScalarAsync(check, "PRAGMA journal_mode;"));

        var migration = (string?)await ScalarAsync(check, "SELECT MigrationId FROM __ef_migrations_ing ORDER BY MigrationId LIMIT 1;");
        Assert.EndsWith("_InitialIngestion", migration, StringComparison.Ordinal);
        Assert.Equal(1L, await ScalarAsync(check, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'ux_ing_events_dedup_key';"));
    }

    [Fact]
    public async Task Restart_keeps_events_and_does_not_reapply_migrations()
    {
        Guid eventId;
        await using (var first = CreateReadyFactory())
        {
            using var client = first.CreateAuthorizedClient();
            using var response = await PostAsync(client, IssueUrl, Guid.NewGuid().ToString());
            eventId = (await response.Content.ReadFromJsonAsync<SubmitBody>(Ct))!.EventId;
        }

        await using (var second = CreateReadyFactory())
        {
            using var client = second.CreateAuthorizedClient();
            (await client.GetAsync("/api/health", Ct)).EnsureSuccessStatusCode();
        }

        var row = Assert.Single(await ReadEventsAsync());
        Assert.Equal(eventId, Guid.Parse(row.Id));
        await using var check = new SqliteConnection($"Data Source={DatabasePath}");
        await check.OpenAsync(Ct);
        Assert.Equal(2L, await ScalarAsync(check, "SELECT COUNT(*) FROM __ef_migrations_ing;")); // InitialIngestion + AddEventDispatch
        Assert.Equal(3L, await ScalarAsync(check, "SELECT COUNT(*) FROM __ef_migrations_run;")); // InitialPipeline + AddRepositoryConfig + AddRunSpecs
    }

    // --- Utilidades ----------------------------------------------------------------

    private async Task<List<EventRow>> ReadEventsAsync()
    {
        if (!File.Exists(DatabasePath))
        {
            return [];
        }

        await using var connection = new SqliteConnection($"Data Source={DatabasePath};Mode=ReadOnly");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, source, object_key, dedup_key, actor, payload_json, status, ignored_reason, received_at FROM ing_events ORDER BY id;";
        var rows = new List<EventRow>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(new EventRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.GetInt64(8)));
        }

        return rows;
    }

    private static async Task<ProblemBody> ReadProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<ProblemBody>(Ct);
        Assert.NotNull(problem);
        return problem;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(Ct);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Limpieza de mejor esfuerzo.
        }
    }

    private sealed record SubmitBody(Guid EventId, bool Created, string? Status, string? Reason);

    private sealed record ProblemBody(int Status, string? Title, string? Detail, string? InstanceStatus, List<string>? Reasons);

    private sealed record EventRow(
        string Id,
        string Source,
        string ObjectKey,
        string DedupKey,
        string? Actor,
        string PayloadJson,
        string Status,
        string? IgnoredReason,
        long ReceivedAtTicks);

    /// <summary>Simula otra petición con el mismo <c>requestId</c> que inserta justo después de la consulta previa.</summary>
    private sealed class ConflictingInsertInterceptor(string connectionString, Guid winnerId) : SaveChangesInterceptor
    {
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var pending = eventData.Context!.ChangeTracker.Entries<EventRecord>().Single().Entity;
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO ing_events (id, source, object_key, dedup_key, actor, payload_json, status, ignored_reason, received_at) "
                + "VALUES ($id, 'Manual', $objectKey, $dedupKey, NULL, '{}', 'Accepted', NULL, $receivedAt);";
            command.Parameters.AddWithValue("$id", winnerId.ToString().ToUpperInvariant());
            command.Parameters.AddWithValue("$objectKey", pending.ObjectKey);
            command.Parameters.AddWithValue("$dedupKey", pending.DedupKey);
            command.Parameters.AddWithValue("$receivedAt", DateTimeOffset.UtcNow.UtcTicks);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return result;
        }
    }

    private sealed class FixedContextFactory(DbContextOptions<IngestionDbContext> options) : IDbContextFactory<IngestionDbContext>
    {
        public IngestionDbContext CreateDbContext() => new(options);
    }

    private sealed class ThrowingModuleDatabase : IModuleDatabase
    {
        public const string FailingPrefix = "zzfail";

        public string Prefix => FailingPrefix;

        public int Order => 1000;

        public Task MigrateAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("migración rota de prueba");
    }

    private sealed class RecordingIngestion : IManualEventIngestion
    {
        public int Calls { get; private set; }

        public Task<ManualEventResult> SubmitAsync(string url, Guid requestId, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(ManualEventResult.Created(new ManualEventInfo(Guid.CreateVersion7(), EventStatus.Accepted, "bot", "o/r", null)));
        }
    }

    private sealed class LevelCapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(Entries);

        public void Dispose()
        {
        }

        private sealed class Logger(ConcurrentQueue<(LogLevel, string)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Enqueue((logLevel, formatter(state, exception)));
        }
    }
}
