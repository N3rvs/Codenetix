namespace CodeNetix.Contracts;

/// <summary>
/// Conexión al único archivo SQLite de la instancia (AD-11). La implementa Persistence; los módulos
/// la usan para configurar su propio <c>DbContext</c> sin referenciar Persistence.
/// </summary>
public interface IDatabaseConnection
{
    string ConnectionString { get; }
}

/// <summary>
/// Base de datos de un módulo: sus tablas llevan <see cref="Prefix"/> (<c>ing_</c>, <c>run_</c>, …) y su
/// historial de migraciones es <c>__ef_migrations_&lt;prefijo&gt;</c>. Persistence ejecuta
/// <see cref="MigrateAsync"/> de todos los módulos registrados en orden ascendente de <see cref="Order"/>.
/// </summary>
public interface IModuleDatabase
{
    /// <summary>Prefijo del módulo, sin guion bajo (p. ej. <c>ing</c>).</summary>
    string Prefix { get; }

    /// <summary>Orden de migración (ascendente).</summary>
    int Order { get; }

    /// <summary>Aplica las migraciones pendientes del módulo.</summary>
    Task MigrateAsync(CancellationToken cancellationToken);
}
