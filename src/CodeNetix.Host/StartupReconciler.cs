using CodeNetix.Host.Configuration;
using CodeNetix.Persistence;
using Microsoft.Extensions.Options;

namespace CodeNetix.Host;

/// <summary>
/// Arranque ordenado (AD-18): 1) base de datos; 2) validar la Configuración de instancia y
/// publicar <see cref="CodeNetix.Contracts.IInstanceHealth"/> (<c>Ready</c> o <c>Diagnostic</c>).
/// La reconciliación y el Dispatcher llegan en historias posteriores.
/// </summary>
internal sealed partial class StartupReconciler(
    SqliteDatabase database,
    IOptions<InstanceOptions> instanceOptions,
    InstanceHealth health,
    ILogger<StartupReconciler> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // 1) Base de datos.
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

        // 2) Configuración de instancia.
        var reasons = InstanceOptionsValidator.Validate(instanceOptions.Value);
        if (reasons.Count == 0)
        {
            health.MarkReady();
            LogReady();
            return;
        }

        health.MarkDiagnostic(reasons);
        LogDiagnostic(reasons.Count);
        foreach (var reason in reasons)
        {
            LogDiagnosticReason(reason);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Critical, Message = "No se puede crear o abrir la base de datos en '{Path}': {Reason}. Comprueba que el directorio existe y que el usuario del proceso puede escribir en él.")]
    private partial void LogDatabaseFailed(string path, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Base de datos lista en '{Path}' (WAL).")]
    private partial void LogDatabaseReady(string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Configuración de instancia completa: CodeNetix está listo.")]
    private partial void LogReady();

    [LoggerMessage(Level = LogLevel.Warning, Message = "CodeNetix arranca en modo diagnóstico ({Count} motivo(s)); no procesará trabajo hasta completar la configuración.")]
    private partial void LogDiagnostic(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Modo diagnóstico: {Reason}")]
    private partial void LogDiagnosticReason(string reason);
}
