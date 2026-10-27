using CodeNetix.Persistence;

namespace CodeNetix.Host;

/// <summary>
/// Arranque ordenado (AD-18). Paso 1: base de datos. Los pasos siguientes
/// (configuración e <c>IInstanceHealth</c>, reconciliación, Dispatcher) llegan en historias posteriores.
/// </summary>
internal sealed partial class StartupReconciler(
    SqliteDatabase database,
    StartupState state,
    ILogger<StartupReconciler> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await database.InitializeAsync(cancellationToken);
        }
        catch (DatabaseInitializationException ex)
        {
            LogDatabaseFailed(ex.DatabasePath, (ex.InnerException?.Message ?? ex.Message).TrimEnd('.'));
            throw;
        }

        LogDatabaseReady(database.DatabasePath);
        state.MarkCompleted();
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Critical, Message = "No se puede crear o abrir la base de datos en '{Path}': {Reason}. Comprueba que el directorio existe y que el usuario del proceso puede escribir en él.")]
    private partial void LogDatabaseFailed(string path, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Base de datos lista en '{Path}' (WAL).")]
    private partial void LogDatabaseReady(string path);
}
