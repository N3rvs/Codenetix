using CodeNetix.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace CodeNetix.Host.Tests;

public sealed class CommandLineTests
{
    [Theory]
    [InlineData("")]
    [InlineData("foo")]
    [InlineData("SERVE")]
    [InlineData("foo serve")]
    [InlineData("--urls=x")]
    [InlineData("--environment=Production")]
    [InlineData("--urls=x serve")]
    public async Task Unknown_or_missing_mode_exits_with_2(string commandLine)
    {
        var args = commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var exitCode = await CodeNetixCommandLine.RunAsync(args);

        Assert.Equal(CodeNetixCommandLine.ExitUsage, exitCode);
    }
}

/// <summary>Redirige <see cref="Console.Error"/>: no puede ejecutarse en paralelo con otros tests.</summary>
[CollectionDefinition(nameof(ConsoleCollection), DisableParallelization = true)]
public sealed class ConsoleCollection;

[Collection(nameof(ConsoleCollection))]
public sealed class UnwritableDataDirectoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codenetix-tests-" + Guid.NewGuid().ToString("N"));

    private string CreateBlockedDataDirectory()
    {
        // Un archivo ocupa la ruta del directorio de datos: no se puede crear ni escribir.
        Directory.CreateDirectory(_root);
        var blocked = Path.Combine(_root, "data");
        File.WriteAllText(blocked, "not a directory");
        return blocked;
    }

    [Fact]
    public void Startup_fails_when_data_directory_cannot_be_created()
    {
        var blocked = CreateBlockedDataDirectory();
        var webRoot = Path.Combine(_root, "wwwroot");
        Directory.CreateDirectory(webRoot);

        using var factory = new CodeNetixFactory(blocked, webRoot);

        // El host no llega a servir (nunca hay salud falsa). WebApplicationFactory envuelve el fallo
        // de arranque en su propia excepción de liberación, por eso no se puede exigir el tipo concreto;
        // el código de salida y el mensaje los comprueba Serve_exits_non_zero_and_names_database_path.
        Assert.ThrowsAny<Exception>(() => factory.CreateClient());
    }

    [Fact]
    public async Task Serve_exits_non_zero_and_names_database_path()
    {
        var blocked = CreateBlockedDataDirectory();
        var stderr = new StringWriter();
        var originalError = Console.Error;
        Console.SetError(stderr);
        int exitCode;
        try
        {
            // StartupReconciler falla antes de que Kestrel enlace :5000.
            exitCode = await CodeNetixCommandLine.RunAsync(["serve", $"--CODENETIX:Persistence:DataDirectory={blocked}"]);
        }
        finally
        {
            Console.SetError(originalError);
        }

        Assert.Equal(CodeNetixCommandLine.ExitFailure, exitCode);
        Assert.Contains(Path.Combine(blocked, "codenetix.db"), stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Database_initialization_error_names_the_path()
    {
        var blocked = CreateBlockedDataDirectory();
        var database = new SqliteDatabase(Options.Create(new PersistenceOptions { DataDirectory = blocked }));

        var ex = await Assert.ThrowsAsync<DatabaseInitializationException>(
            () => database.InitializeAsync(TestContext.Current.CancellationToken));

        Assert.Equal(Path.Combine(blocked, "codenetix.db"), ex.DatabasePath);
        Assert.Contains(ex.DatabasePath, ex.Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Limpieza de mejor esfuerzo.
        }
    }
}
