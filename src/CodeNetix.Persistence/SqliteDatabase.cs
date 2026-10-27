using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace CodeNetix.Persistence;

/// <summary>
/// Único punto de acceso al archivo SQLite de la instancia (AD-11): conexión y ajustes.
/// Las tablas y migraciones son de cada módulo.
/// </summary>
public sealed class SqliteDatabase
{
    public const int BusyTimeoutMilliseconds = 5000;

    public SqliteDatabase(IOptions<PersistenceOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var dataDirectory = options.Value.DataDirectory;
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            throw new InvalidOperationException("Persistence: DataDirectory no puede estar vacío.");
        }

        DataDirectory = Path.GetFullPath(dataDirectory);
        DatabasePath = Path.Combine(DataDirectory, PersistenceOptions.DatabaseFileName);
        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = BusyTimeoutMilliseconds / 1000,
        }.ToString();
    }

    public string DataDirectory { get; }

    public string DatabasePath { get; }

    public string ConnectionString { get; }

    /// <summary>Abre una conexión con <c>busy_timeout</c> aplicado.</summary>
    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, $"PRAGMA busy_timeout = {BusyTimeoutMilliseconds};", cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Crea el directorio si falta, crea o abre <c>codenetix.db</c> y fija <c>journal_mode=WAL</c>.
    /// Lanza <see cref="DatabaseInitializationException"/> con la ruta si algo falla.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);

            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode = WAL;";
            var mode = (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) as string;
            if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"SQLite devolvió journal_mode '{mode}' en lugar de 'wal'.");
            }

            // Sonda de escritura: SQLite abre en solo lectura sin avisar si el archivo no es escribible.
            command.CommandText = "PRAGMA user_version;";
            var userVersion = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
            await ExecuteAsync(connection, $"PRAGMA user_version = {userVersion};", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not DatabaseInitializationException)
        {
            throw new DatabaseInitializationException(DatabasePath, ex);
        }
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

public sealed class DatabaseInitializationException(string databasePath, Exception innerException)
    : Exception($"No se pudo inicializar la base de datos en '{databasePath}': {innerException.Message}", innerException)
{
    public string DatabasePath { get; } = databasePath;
}
