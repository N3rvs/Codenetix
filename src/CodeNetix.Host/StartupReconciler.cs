using CodeNetix.Contracts;
using CodeNetix.Host.Access;
using CodeNetix.Host.Configuration;
using CodeNetix.Persistence;
using CodeNetix.Sandbox;
using Microsoft.Extensions.Options;

namespace CodeNetix.Host;

/// <summary>
/// Arranque ordenado (AD-18, FR-24):
/// <list type="number">
/// <item>base de datos y migraciones de todos los módulos (AD-11), y asegurar <c>admin.token</c> (AD-17);</item>
/// <item>evaluación de salud: validar Configuración de instancia y Docker/imagen del Sandbox (<see cref="ISandboxHealthCheck"/>);</item>
/// <item>reconciliación de Ejecuciones no terminales a <c>Failed(HostRestarted)</c> (<see cref="IRunStartupReconciler"/>);</item>
/// <item>limpieza best-effort de contenedores <c>codenetix.run</c>, volúmenes <c>codenetix-ws-*</c> (<see cref="ISandboxReconciler"/>)
/// y árboles huérfanos en <c>workspaces/</c> (<see cref="IRepositoryWorkspace"/>);</item>
/// <item>publicación de <see cref="IInstanceHealth"/> (<c>Ready</c> o <c>Diagnostic</c>), despertando al <c>Dispatcher</c> solo al final.</item>
/// </list>
/// </summary>
internal sealed partial class StartupReconciler(
    SqliteDatabase database,
    DatabaseMigrator migrator,
    IOptions<InstanceOptions> instanceOptions,
    AdminTokenStore tokenStore,
    InstanceHealth health,
    ISandboxHealthCheck sandboxHealth,
    IRunStartupReconciler runReconciler,
    ISandboxReconciler sandboxReconciler,
    IRepositoryWorkspace workspace,
    ILogger<StartupReconciler> logger,
    PanelSecretStore? panelSecrets = null) : IHostedService
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

        // 1c) Claves guardadas desde el Panel (solo serve, historia 8.1): un archivo ilegible no bloquea, se avisa.
        if (panelSecrets?.LoadError is { } panelSecretsError)
        {
            LogPanelSecretsUnreadable(panelSecretsError);
        }

        // 2) Evaluación de salud: Configuración de instancia y Sandbox (Docker accesible y la imagen presente).
        var reasons = new List<string>(InstanceOptionsValidator.Validate(instanceOptions.Value));
        var startupReasons = new List<string>();
        reasons.AddRange(await sandboxHealth.CheckAsync(cancellationToken));

        // 3) Ejecuciones no terminales -> Failed(HostRestarted), también en Diagnostic (solo toca SQLite y, best-effort, GitHub).
        //    Si alguna no se pudo cerrar, la instancia no pasa a Ready: el Dispatcher marcaría como Ignored (objeto
        //    ocupado) los Eventos pendientes de ese objeto.
        try
        {
            var result = await runReconciler.ReconcileNonTerminalRunsAsync(cancellationToken);
            if (result.Closed > 0)
            {
                LogRunsReconciled(result.Closed);
            }

            if (result.StillActive > 0)
            {
                startupReasons.Add(RunsStillActive(result.StillActive));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogRunsReconcileFailed(ex);
            startupReasons.Add(ReconciliationFailed);
        }

        // Estos motivos solo se resuelven reiniciando: guardar claves desde el Panel no los borra.
        health.RecordStartupReasons(startupReasons);
        reasons.AddRange(startupReasons);

        // 4) Huérfanos: contenedores codenetix.run y volúmenes codenetix-ws-* (nunca codenetix-tools) y árboles de workspaces/.
        var containersRemoved = 0;
        var volumesRemoved = 0;
        try
        {
            var cleanup = await sandboxReconciler.CleanupOrphansAsync(cancellationToken);
            containersRemoved = cleanup.ContainersRemoved;
            volumesRemoved = cleanup.VolumesRemoved;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogCleanupFailed("Docker", ex.Message);
        }

        var workspacesRemoved = 0;
        try
        {
            workspacesRemoved = await workspace.CleanupOrphansAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogCleanupFailed("workspaces", ex.Message);
        }

        if (containersRemoved > 0 || volumesRemoved > 0 || workspacesRemoved > 0)
        {
            LogOrphansCleaned(containersRemoved, volumesRemoved, workspacesRemoved);
        }

        // 5) Publicación de salud: solo ahora Ready despierta al Dispatcher; Diagnostic lo mantiene detenido.
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

    public static string RunsStillActive(int count) =>
        $"La reconciliación de arranque no ha podido cerrar {count} Ejecución(es) no terminal(es) de antes del reinicio; revisa los logs y reinicia CodeNetix.";

    public const string ReconciliationFailed =
        "La reconciliación de arranque de las Ejecuciones ha fallado; revisa los logs y reinicia CodeNetix.";

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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reconciliación tras reinicio: {Count} Ejecución(es) no terminal(es) marcada(s) como Failed(HostRestarted).")]
    private partial void LogRunsReconciled(int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "No se ha podido completar la reconciliación de Ejecuciones tras el reinicio; el arranque continúa.")]
    private partial void LogRunsReconcileFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se ha podido completar la limpieza de huérfanos ({Target}) tras el reinicio: {Reason}.")]
    private partial void LogCleanupFailed(string target, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Limpieza tras reinicio completada: {Containers} contenedor(es), {Volumes} volumen(es) y {Workspaces} árbol(es) de trabajo huérfanos eliminados.")]
    private partial void LogOrphansCleaned(int containers, int volumes, int workspaces);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Reason}")]
    private partial void LogPanelSecretsUnreadable(string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Configuración de instancia completa: CodeNetix está listo.")]
    private partial void LogReady();

    [LoggerMessage(Level = LogLevel.Warning, Message = "CodeNetix arranca en modo diagnóstico ({Count} motivo(s)); no procesará trabajo hasta completar la configuración.")]
    private partial void LogDiagnostic(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Modo diagnóstico: {Reason}")]
    private partial void LogDiagnosticReason(string reason);
}
