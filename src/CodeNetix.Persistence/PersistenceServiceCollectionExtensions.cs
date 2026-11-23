using CodeNetix.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace CodeNetix.Persistence;

public static class PersistenceServiceCollectionExtensions
{
    /// <summary>
    /// Registra <see cref="SqliteDatabase"/> (también como <see cref="IDatabaseConnection"/>) y
    /// <see cref="DatabaseMigrator"/>. Las opciones las configura Host (solo Host lee el entorno).
    /// </summary>
    public static IServiceCollection AddCodeNetixPersistence(
        this IServiceCollection services,
        Action<PersistenceOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = services.AddOptions<PersistenceOptions>();
        if (configure is not null)
        {
            options.Configure(configure);
        }

        services.AddSingleton<SqliteDatabase>();
        services.AddSingleton<IDatabaseConnection>(sp => sp.GetRequiredService<SqliteDatabase>());
        services.AddSingleton<DatabaseMigrator>();
        return services;
    }
}
