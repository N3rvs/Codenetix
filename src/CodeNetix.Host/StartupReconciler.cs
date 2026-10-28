using CodeNetix.Host.Access;
using CodeNetix.Host.Configuration;
using CodeNetix.Persistence;
using CodeNetix.Sandbox;
using Microsoft.Extensions.Options;

namespace CodeNetix.Host;

/// <summary>
/// Arranque ordenado (AD-18): 1) base de datos y migraciones de todos los módulos (AD-11);
/// 1b) asegurar <c>admin.token</c> (AD-17);
/// 2) validar la Configuración de instancia y
/// publicar <see cref="CodeNetix.Contracts.IInstanceHealth"/> (<c>Ready</c> o <c>Diagnostic</c>); también es
/// <c>Diagnostic</c> si Docker no responde o falta la imagen del Sandbox (<see cref="ISandboxHealthCheck"/>).
/// El Dispatcher de Pipeline espera a <c>Ready</c> (<see cref="CodeNetix.Contracts.IInstanceHealth.WhenReadyAsync"/>);
/// la reconciliación de Ejecuciones llega en la Épica 5.
/// </summary>
internal sealed partial class StartupReconciler(
    SqliteDatabase database,
    DatabaseMigrator migrator,
    IOptions<InstanceOptions> instanceOptions,
    AdminTokenStore tokenStore,
    InstanceHealth health,
    ISandboxHealthCheck sandboxHealth,
    ILogger<StartupReconciler> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // 1) Base de datos y migraciones de todos los módulos (en el orden que fija Persistence).
        try
        {
            await database.InitializeAsync(cancellationToken);
        }
        catch (DatabaseInitializationException ex)
        {
            LogDatabaseFailed(ex.DatabasePath, (ex.InnerException?.Message ?? ex.Message).TrimEnd('.'));
            throw;
        }

        try
        {
            await migrator.MigrateAllAsync(cancellationToken);
        }
        catch (DatabaseMigrationException ex)
        {
            LogMigrationFailed(ex.Prefix, database.DatabasePath, (ex.InnerException?.Message ?? ex.Message).TrimEnd('.'));
            throw;
        }

        LogDatabaseReady(database.DatabasePath, migrator.Modules.Count);

        // 1b) admin-token: el archivo se asegura siempre, aunque haya AccessToken. Nunca se registra el valor.
        try
        {
            tokenStore.EnsureTokenFile();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            LogAdminTokenFailed(tokenStore.FilePath, ex.Message.TrimEnd('.'));
            throw;
        }

        if (tokenStore.UsesConfiguredToken)
        {
            LogAdminTokenFromEnvironment(tokenStore.FilePath);
        }
        else
        {
            LogAdminTokenFromFile(tokenStore.FilePath);
        }

        // 2) Configuración de instancia y Sandbox (Docker accesible y la imagen presente).
        var reasons = new List<string>(InstanceOptionsValidator.Validate(instanceOptions.Value));
        reasons.AddRange(await sandboxHealth.CheckAsync(cancellationToken));
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

    [LoggerMessage(Level = LogLevel.Critical, Message = "No se pueden aplicar las migraciones del módulo '{Prefix}' en '{Path}': {Reason}.")]
    private partial void LogMigrationFailed(string prefix, string path, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Base de datos lista en '{Path}' (WAL, {ModuleCount} módulo(s) migrado(s)).")]
    private partial void LogDatabaseReady(string path, int moduleCount);

    [LoggerMessage(Level = LogLevel.Critical, Message = "No se puede crear o leer el admin-token en '{Path}': {Reason}.")]
    private partial void LogAdminTokenFailed(string path, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "El admin-token para /api y /hubs está en '{Path}'.")]
    private partial void LogAdminTokenFromFile(string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "El admin-token para /api y /hubs es el de CODENETIX__Host__AccessToken (el archivo '{Path}' no se usa).")]
    private partial void LogAdminTokenFromEnvironment(string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Configuración de instancia completa: CodeNetix está listo.")]
    private partial void LogReady();

    [LoggerMessage(Level = LogLevel.Warning, Message = "CodeNetix arranca en modo diagnóstico ({Count} motivo(s)); no procesará trabajo hasta completar la configuración.")]
    private partial void LogDiagnostic(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Modo diagnóstico: {Reason}")]
    private partial void LogDiagnosticReason(string reason);
}
