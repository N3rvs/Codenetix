using CodeNetix.Contracts;
using CodeNetix.Pipeline.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CodeNetix.Pipeline.Data;

/// <summary>
/// <c>DbContext</c> de Pipeline (AD-11): dueño de <c>run_runs</c>, <c>run_events</c> y <c>run_specs</c>, con su propio historial
/// <see cref="MigrationsHistoryTable"/>. Las migraciones se generan con <c>dotnet-ef</c> y no se editan a mano.
/// </summary>
internal sealed class PipelineDbContext(DbContextOptions<PipelineDbContext> options) : DbContext(options)
{
    public const string Prefix = "run";
    public const string MigrationsHistoryTable = "__ef_migrations_" + Prefix;
    public const string RunsTable = Prefix + "_runs";
    public const string EventsTable = Prefix + "_events";
    public const string SpecsTable = Prefix + "_specs";
    public const string ActiveObjectKeyIndex = "ux_" + RunsTable + "_active_object_key";
    public const string ShortIdIndex = "ux_" + RunsTable + "_short_id";
    public const string EventIdIndex = "ux_" + RunsTable + "_event_id";

    /// <summary>
    /// Filtro del índice UNIQUE parcial: como mucho una Ejecución no terminal por objeto (AD-4).
    /// </summary>
    public static readonly string ActiveFilter =
        $"state NOT IN ('{RunState.PullRequestCreated}', '{RunState.Failed}', '{RunState.Cancelled}')";

    public DbSet<PipelineRun> Runs => Set<PipelineRun>();

    public DbSet<RunEventRecord> Events => Set<RunEventRecord>();

    public DbSet<RunSpecRecord> Specs => Set<RunSpecRecord>();

    /// <summary>Configura SQLite con el historial de migraciones del módulo.</summary>
    public static void Configure(DbContextOptionsBuilder builder, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseSqlite(connectionString, sqlite => sqlite.MigrationsHistoryTable(MigrationsHistoryTable));
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var runs = modelBuilder.Entity<PipelineRun>();
        runs.ToTable(RunsTable);
        runs.HasKey(r => r.Id).HasName("pk_" + RunsTable);
        runs.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();
        runs.Property(r => r.ShortId).HasColumnName("short_id").IsRequired();
        runs.Property(r => r.EventId).HasColumnName("event_id");
        runs.Property(r => r.ObjectKey).HasColumnName("object_key").IsRequired();
        runs.Property(r => r.State).HasColumnName("state").HasConversion<string>().IsRequired();
        runs.Property(r => r.FailureReason).HasColumnName("failure_reason").HasConversion<string>();
        runs.Property(r => r.FailureDetail).HasColumnName("failure_detail");
        runs.Property(r => r.BaseSha).HasColumnName("base_sha");
        runs.Property(r => r.EffectiveConfigJson).HasColumnName("effective_config_json");

        // EF Sqlite no ordena ni compara DateTimeOffset guardado como TEXT: ticks UTC (INTEGER), igual que ing_events.
        runs.Property(r => r.CreatedAt).HasColumnName("created_at").HasConversion(v => v.UtcTicks, v => new DateTimeOffset(v, TimeSpan.Zero));
        runs.Property(r => r.UpdatedAt).HasColumnName("updated_at").HasConversion(v => v.UtcTicks, v => new DateTimeOffset(v, TimeSpan.Zero));

        runs.HasIndex(r => r.ShortId).IsUnique().HasDatabaseName(ShortIdIndex);
        // Un Evento produce como mucho una Ejecución: el Dispatcher puede reintentar sin duplicar.
        runs.HasIndex(r => r.EventId).IsUnique().HasDatabaseName(EventIdIndex);
        runs.HasIndex(r => r.ObjectKey).IsUnique().HasFilter(ActiveFilter).HasDatabaseName(ActiveObjectKeyIndex);

        var events = modelBuilder.Entity<RunEventRecord>();
        events.ToTable(EventsTable);
        events.HasKey(e => new { e.RunId, e.Seq }).HasName("pk_" + EventsTable);
        events.Property(e => e.RunId).HasColumnName("run_id");
        events.Property(e => e.Seq).HasColumnName("seq").ValueGeneratedNever();
        events.Property(e => e.Type).HasColumnName("type").IsRequired();
        events.Property(e => e.PayloadJson).HasColumnName("payload_json").IsRequired();
        events.Property(e => e.At).HasColumnName("at").HasConversion(v => v.UtcTicks, v => new DateTimeOffset(v, TimeSpan.Zero));
        events.HasOne<PipelineRun>()
            .WithMany()
            .HasForeignKey(e => e.RunId)
            .HasConstraintName("fk_" + EventsTable + "_" + RunsTable)
            .OnDelete(DeleteBehavior.Cascade);

        var specs = modelBuilder.Entity<RunSpecRecord>();
        specs.ToTable(SpecsTable);
        specs.HasKey(s => s.RunId).HasName("pk_" + SpecsTable);
        specs.Property(s => s.RunId).HasColumnName("run_id").ValueGeneratedNever();
        specs.Property(s => s.SpecJson).HasColumnName("spec_json").IsRequired();
        specs.Property(s => s.Model).HasColumnName("model").IsRequired();
        specs.Property(s => s.Attempts).HasColumnName("attempts");
        specs.Property(s => s.CreatedAt).HasColumnName("created_at").HasConversion(v => v.UtcTicks, v => new DateTimeOffset(v, TimeSpan.Zero));
        specs.HasOne<PipelineRun>()
            .WithOne()
            .HasForeignKey<RunSpecRecord>(s => s.RunId)
            .HasConstraintName("fk_" + SpecsTable + "_" + RunsTable)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>Fila de <c>run_events</c>: un <see cref="RunEvent"/> serializado (y redactado).</summary>
internal sealed class RunEventRecord
{
    public Guid RunId { get; set; }

    public long Seq { get; set; }

    /// <summary>Discriminador de la unión (<c>RunCreated</c>, <c>StateChanged</c>, …).</summary>
    public string Type { get; set; } = string.Empty;

    public string PayloadJson { get; set; } = string.Empty;

    public DateTimeOffset At { get; set; }
}

/// <summary>Fila de <c>run_specs</c>: la <see cref="Spec"/> de una Ejecución (como mucho una).</summary>
internal sealed class RunSpecRecord
{
    public Guid RunId { get; set; }

    /// <summary>JSON camelCase de la <see cref="Spec"/>.</summary>
    public string SpecJson { get; set; } = string.Empty;

    /// <summary>Modelo que la generó.</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>Intentos que necesitó (1 + reintentos de esquema).</summary>
    public int Attempts { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Solo para <c>dotnet-ef migrations add</c>: no se usa en tiempo de ejecución.</summary>
internal sealed class PipelineDesignTimeDbContextFactory : IDesignTimeDbContextFactory<PipelineDbContext>
{
    public PipelineDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<PipelineDbContext>();
        PipelineDbContext.Configure(builder, "Data Source=design-time.db");
        return new PipelineDbContext(builder.Options);
    }
}

/// <summary>Registro de Pipeline ante el orquestador de migraciones de Persistence.</summary>
internal sealed class PipelineModuleDatabase(IDbContextFactory<PipelineDbContext> contextFactory) : IModuleDatabase
{
    /// <summary>Después de Ingestion (<c>ing</c>, 10).</summary>
    public const int MigrationOrder = 20;

    public string Prefix => PipelineDbContext.Prefix;

    public int Order => MigrationOrder;

    public async Task MigrateAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }
}
