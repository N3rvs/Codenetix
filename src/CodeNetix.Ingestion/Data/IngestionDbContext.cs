using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CodeNetix.Ingestion.Data;

/// <summary>
/// <c>DbContext</c> de Ingestion (AD-11): dueño de <c>ing_events</c>, con su propio historial de
/// migraciones <see cref="MigrationsHistoryTable"/>. Las migraciones se generan con <c>dotnet-ef</c>
/// y no se editan a mano.
/// </summary>
internal sealed class IngestionDbContext(DbContextOptions<IngestionDbContext> options) : DbContext(options)
{
    public const string Prefix = "ing";
    public const string MigrationsHistoryTable = "__ef_migrations_" + Prefix;
    public const string EventsTable = Prefix + "_events";
    public const string DedupKeyIndex = "ux_" + EventsTable + "_dedup_key";

    public DbSet<EventRecord> Events => Set<EventRecord>();

    /// <summary>Configura SQLite con el historial de migraciones del módulo.</summary>
    public static void Configure(DbContextOptionsBuilder builder, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseSqlite(connectionString, sqlite => sqlite.MigrationsHistoryTable(MigrationsHistoryTable));
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var events = modelBuilder.Entity<EventRecord>();
        events.ToTable(EventsTable);
        events.HasKey(e => e.Id).HasName("pk_" + EventsTable);

        events.Property(e => e.Id).HasColumnName("id");
        events.Property(e => e.Source).HasColumnName("source").HasConversion<string>().IsRequired();
        events.Property(e => e.ObjectKey).HasColumnName("object_key").IsRequired();
        events.Property(e => e.DedupKey).HasColumnName("dedup_key").IsRequired();
        events.Property(e => e.Actor).HasColumnName("actor");
        events.Property(e => e.PayloadJson).HasColumnName("payload_json").IsRequired();
        events.Property(e => e.Status).HasColumnName("status").HasConversion<string>().IsRequired();
        events.Property(e => e.IgnoredReason).HasColumnName("ignored_reason");
        // EF Sqlite no ordena ni compara DateTimeOffset guardado como TEXT: se guarda en ticks UTC (INTEGER).
        events.Property(e => e.ReceivedAt)
            .HasColumnName("received_at")
            .HasConversion(v => v.UtcTicks, v => new DateTimeOffset(v, TimeSpan.Zero));
        events.Property(e => e.DispatchedAt)
            .HasColumnName("dispatched_at")
            .HasConversion<long?>(
                v => v.HasValue ? v.Value.UtcTicks : null,
                v => v.HasValue ? new DateTimeOffset(v.Value, TimeSpan.Zero) : null);
        events.Property(e => e.RunId).HasColumnName("run_id");

        // La deduplicación la garantiza el índice, no una consulta previa (AD-4).
        events.HasIndex(e => e.DedupKey).IsUnique().HasDatabaseName(DedupKeyIndex);
    }
}

/// <summary>Solo para <c>dotnet-ef migrations add</c>: no se usa en tiempo de ejecución.</summary>
internal sealed class IngestionDesignTimeDbContextFactory : IDesignTimeDbContextFactory<IngestionDbContext>
{
    public IngestionDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<IngestionDbContext>();
        IngestionDbContext.Configure(builder, "Data Source=design-time.db");
        return new IngestionDbContext(builder.Options);
    }
}
