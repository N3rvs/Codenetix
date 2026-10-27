using Microsoft.Extensions.DependencyInjection;

namespace CodeNetix.Persistence;

public static class PersistenceServiceCollectionExtensions
{
    /// <summary>
    /// Registra <see cref="SqliteDatabase"/>. Las opciones las configura Host
    /// (solo Host lee el entorno).
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
        return services;
    }
}
