using CodeNetix.Contracts;

namespace CodeNetix.Persistence;

/// <summary>
/// Orquesta las migraciones de los módulos (AD-11): ejecuta cada <see cref="IModuleDatabase"/> registrado
/// en orden ascendente de <see cref="IModuleDatabase.Order"/> (y de prefijo, para desempatar).
/// Persistence no conoce las tablas: cada módulo es dueño de su esquema y su historial.
/// </summary>
public sealed class DatabaseMigrator(IEnumerable<IModuleDatabase> modules)
{
    /// <summary>Módulos en el orden en que se migran.</summary>
    public IReadOnlyList<IModuleDatabase> Modules { get; } =
        [.. (modules ?? throw new ArgumentNullException(nameof(modules)))
            .OrderBy(m => m.Order)
            .ThenBy(m => m.Prefix, StringComparer.Ordinal)];

    /// <summary>
    /// Migra todos los módulos. Lanza <see cref="DatabaseMigrationException"/> con el prefijo del módulo
    /// que falla (o del prefijo duplicado, antes de migrar nada); los siguientes no se migran.
    /// </summary>
    public async Task MigrateAllAsync(CancellationToken cancellationToken = default)
    {
        var duplicate = Modules
            .GroupBy(m => m.Prefix, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new DatabaseMigrationException(
                duplicate.Key,
                new InvalidOperationException($"Hay más de un módulo de base de datos con el prefijo '{duplicate.Key}'."));
        }

        foreach (var module in Modules)
        {
            try
            {
                await module.MigrateAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new DatabaseMigrationException(module.Prefix, ex);
            }
        }
    }
}

public sealed class DatabaseMigrationException(string prefix, Exception innerException)
    : Exception($"No se pudieron aplicar las migraciones del módulo '{prefix}': {innerException.Message}", innerException)
{
    public string Prefix { get; } = prefix;
}
