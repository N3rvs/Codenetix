using CodeNetix.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CodeNetix.GitHub.Data;

/// <summary>
/// <c>DbContext</c> del módulo GitHub (AD-11): dueño de <c>gh_notifications</c> y <c>gh_pull_requests</c>, con su propio historial
/// <see cref="MigrationsHistoryTable"/>. Las migraciones se generan con <c>dotnet-ef</c> y no se editan a mano.
/// </summary>
internal sealed class GitHubDbContext(DbContextOptions<GitHubDbContext> options) : DbContext(options)
{
    public const string Prefix = "gh";
    public const string MigrationsHistoryTable = "__ef_migrations_" + Prefix;
    public const string NotificationsTable = Prefix + "_notifications";
    public const string RunKindIndex = "ux_" + NotificationsTable + "_run_id_kind";
    public const string PullRequestsTable = Prefix + "_pull_requests";
    public const string PullRequestsRunIndex = "ux_" + PullRequestsTable + "_run_id";

    public DbSet<NotificationRecord> Notifications => Set<NotificationRecord>();

    public DbSet<PullRequestRecord> PullRequests => Set<PullRequestRecord>();

    /// <summary>Configura SQLite con el historial de migraciones del módulo.</summary>
    public static void Configure(DbContextOptionsBuilder builder, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseSqlite(connectionString, sqlite => sqlite.MigrationsHistoryTable(MigrationsHistoryTable));
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var notifications = modelBuilder.Entity<NotificationRecord>();
        notifications.ToTable(NotificationsTable);
        notifications.HasKey(n => n.Id).HasName("pk_" + NotificationsTable);
        notifications.Property(n => n.Id).HasColumnName("id").ValueGeneratedNever();
        // Sin clave foránea a run_runs: cada módulo es dueño solo de sus tablas (AD-11).
        notifications.Property(n => n.RunId).HasColumnName("run_id");
        notifications.Property(n => n.Kind).HasColumnName("kind").HasConversion<string>().IsRequired();
        notifications.Property(n => n.Status).HasColumnName("status").HasConversion<string>().IsRequired();
        notifications.Property(n => n.CommentId).HasColumnName("comment_id");
        notifications.Property(n => n.CommentUrl).HasColumnName("comment_url");
        notifications.Property(n => n.Error).HasColumnName("error");

        // EF Sqlite no ordena ni compara DateTimeOffset guardado como TEXT: ticks UTC (INTEGER), igual que el resto.
        notifications.Property(n => n.CreatedAt).HasColumnName("created_at").HasConversion(v => v.UtcTicks, v => new DateTimeOffset(v, TimeSpan.Zero));
        notifications.Property(n => n.UpdatedAt).HasColumnName("updated_at").HasConversion(v => v.UtcTicks, v => new DateTimeOffset(v, TimeSpan.Zero));

        // La idempotencia por (run_id, kind) la garantiza el índice, no una consulta previa.
        notifications.HasIndex(n => new { n.RunId, n.Kind }).IsUnique().HasDatabaseName(RunKindIndex);

        var pullRequests = modelBuilder.Entity<PullRequestRecord>();
        pullRequests.ToTable(PullRequestsTable);
        pullRequests.HasKey(p => p.Id).HasName("pk_" + PullRequestsTable);
        pullRequests.Property(p => p.Id).HasColumnName("id").ValueGeneratedNever();
        pullRequests.Property(p => p.RunId).HasColumnName("run_id");
        pullRequests.Property(p => p.Number).HasColumnName("number");
        pullRequests.Property(p => p.Url).HasColumnName("url").IsRequired();
        pullRequests.Property(p => p.BranchName).HasColumnName("branch_name").IsRequired();
        pullRequests.Property(p => p.CommitSha).HasColumnName("commit_sha").IsRequired();
        pullRequests.Property(p => p.Title).HasColumnName("title").IsRequired();
        pullRequests.Property(p => p.CreatedAt).HasColumnName("created_at").HasConversion(v => v.UtcTicks, v => new DateTimeOffset(v, TimeSpan.Zero));
        pullRequests.HasIndex(p => p.RunId).IsUnique().HasDatabaseName(PullRequestsRunIndex);
    }
}

/// <summary>Fila de <c>gh_pull_requests</c>: como mucho un PR publicado por Ejecución (AD-11).</summary>
internal sealed class PullRequestRecord
{
    public Guid Id { get; set; }

    public Guid RunId { get; set; }

    public long Number { get; set; }

    public string Url { get; set; } = string.Empty;

    public string BranchName { get; set; } = string.Empty;

    public string CommitSha { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Fila de <c>gh_notifications</c>: un aviso en una issue, como mucho uno por (<c>run_id</c>, <c>kind</c>).</summary>
internal sealed class NotificationRecord
{
    public Guid Id { get; set; }

    public Guid RunId { get; set; }

    public NotificationKind Kind { get; set; }

    public NotificationStatus Status { get; set; }

    /// <summary>Id del comentario en GitHub (solo en <c>Posted</c>).</summary>
    public long? CommentId { get; set; }

    public string? CommentUrl { get; set; }

    /// <summary>Motivo redactado del último fallo (solo en <c>Failed</c>).</summary>
    public string? Error { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Solo para <c>dotnet-ef migrations add</c>: no se usa en tiempo de ejecución.</summary>
internal sealed class GitHubDesignTimeDbContextFactory : IDesignTimeDbContextFactory<GitHubDbContext>
{
    public GitHubDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<GitHubDbContext>();
        GitHubDbContext.Configure(builder, "Data Source=design-time.db");
        return new GitHubDbContext(builder.Options);
    }
}

/// <summary>Registro del módulo GitHub ante el orquestador de migraciones de Persistence.</summary>
internal sealed class GitHubModuleDatabase(IDbContextFactory<GitHubDbContext> contextFactory) : IModuleDatabase
{
    /// <summary>Después de Pipeline (<c>run</c>, 20).</summary>
    public const int MigrationOrder = 30;

    public string Prefix => GitHubDbContext.Prefix;

    public int Order => MigrationOrder;

    public async Task MigrateAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }
}
