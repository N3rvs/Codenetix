using CodeNetix.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CodeNetix.Ingestion.Data;

/// <summary>Registro de Ingestion ante el orquestador de migraciones de Persistence.</summary>
internal sealed class IngestionModuleDatabase(IDbContextFactory<IngestionDbContext> contextFactory) : IModuleDatabase
{
    /// <summary>Ingestion es el primer módulo: el resto puede referenciar Eventos.</summary>
    public const int MigrationOrder = 10;

    public string Prefix => IngestionDbContext.Prefix;

    public int Order => MigrationOrder;

    public async Task MigrateAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }
}
