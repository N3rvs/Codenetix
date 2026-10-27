using CodeNetix.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace CodeNetix.Host.Tests;

public sealed class SqliteDatabaseTests : IDisposable
{
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "codenetix-tests-" + Guid.NewGuid().ToString("N"));

    private SqliteDatabase CreateDatabase() =>
        new(Options.Create(new PersistenceOptions { DataDirectory = _dataDirectory }));

    [Fact]
    public async Task OpenConnectionAsync_applies_busy_timeout()
    {
        var database = CreateDatabase();
        await database.InitializeAsync(TestContext.Current.CancellationToken);

        await using var connection = await database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout;";

        Assert.Equal(5000L, (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
    }

    [Fact]
    public async Task InitializeAsync_fails_on_read_only_database_file()
    {
        await CreateDatabase().InitializeAsync(TestContext.Current.CancellationToken);
        SqliteConnection.ClearAllPools();
        var databasePath = Path.Combine(_dataDirectory, PersistenceOptions.DatabaseFileName);
        File.SetAttributes(databasePath, FileAttributes.ReadOnly);

        var ex = await Assert.ThrowsAsync<DatabaseInitializationException>(
            () => CreateDatabase().InitializeAsync(TestContext.Current.CancellationToken));

        Assert.Equal(databasePath, ex.DatabasePath);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            foreach (var file in Directory.EnumerateFiles(_dataDirectory))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_dataDirectory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Limpieza de mejor esfuerzo.
        }
    }
}
