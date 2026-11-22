using System.Collections.Immutable;

namespace CodeNetix.Contracts;

/// <summary>
/// Crea el Sandbox de una Ejecución (AD-6: solo lo implementa el módulo Sandbox y solo lo invoca el orquestador,
/// tras el clonado). Ningún otro módulo crea ni destruye contenedores.
/// </summary>
public interface ISandboxManager
{
    /// <summary>
    /// Crea el volumen <c>codenetix-ws-&lt;runId&gt;</c> y el contenedor <c>codenetix-sbx-&lt;shortId&gt;</c>, sube el
    /// árbol del <c>BaseSha</c> (sin <c>.git</c>) a <c>/workspace</c> y arranca el contenedor. Al cancelarse
    /// <paramref name="cancellationToken"/> (el token de la Ejecución) la sesión detiene el contenedor en 10 s como
    /// máximo. Si algo falla, borra lo creado a medias y lanza <see cref="SandboxCreationException"/>.
    /// </summary>
    Task<ISandboxSession> CreateForRunAsync(Guid runId, CancellationToken cancellationToken);
}

/// <summary>
/// Sandbox vivo de una Ejecución. <see cref="IAsyncDisposable.DisposeAsync"/> lo detiene y borra el contenedor y el
/// volumen de trabajo; es idempotente y nunca lanza (los fallos se registran en el log).
/// </summary>
public interface ISandboxSession : IAsyncDisposable
{
    Guid RunId { get; }

    string ContainerId { get; }

    /// <summary>Imagen con la que se ha creado el contenedor.</summary>
    string Image { get; }

    /// <summary>
    /// Ejecuta <paramref name="command"/> dentro del contenedor (como el usuario <c>sandbox</c>). Un timeout no
    /// invalida la sesión (<see cref="SandboxExecResult.TimedOut"/>). Cancelar <paramref name="cancellationToken"/>
    /// con el comando en curso detiene el contenedor (no hay otra forma de parar el proceso) y lanza
    /// <see cref="OperationCanceledException"/>. Lanza <see cref="SandboxUnavailableException"/> si el contenedor ya
    /// está detenido o Docker no responde.
    /// </summary>
    Task<SandboxExecResult> ExecAsync(SandboxCommand command, CancellationToken cancellationToken);

    /// <summary>
    /// <c>true</c> si algún archivo o enlace de <c>/workspace</c> se ha modificado, añadido o eliminado respecto al
    /// árbol del <c>BaseSha</c> subido al crear el Sandbox (no usa <c>.git</c>, que no existe en el Sandbox). Lanza
    /// <see cref="SandboxUnavailableException"/> si no se puede inspeccionar el árbol.
    /// </summary>
    Task<bool> HasChangesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Exporta el árbol actual de <c>/workspace</c> como tar en <paramref name="destination"/>
    /// (<c>GET /containers/{id}/archive?path=/workspace</c>, AD-8). Lanza <see cref="SandboxUnavailableException"/>
    /// si el Sandbox está detenido o Docker falla.
    /// </summary>
    Task ExportWorkspaceAsync(Stream destination, CancellationToken cancellationToken);

    /// <summary>
    /// Detiene el contenedor: <c>stop</c> con 10 s de gracia y, si sigue vivo, <c>kill</c>. Idempotente; nunca lanza.
    /// </summary>
    Task StopAsync();
}

/// <summary>
/// Provisión de herramientas en el volumen <c>codenetix-tools</c> (AD-6). Solo la implementa el módulo Sandbox.
/// </summary>
public interface ISandboxToolsProvisioner
{
    /// <summary>Versión fijada de <c>@anthropic-ai/claude-code</c>.</summary>
    public const string ClaudeCliVersion = "2.1.311";

    /// <summary>
    /// Garantiza que <c>claude</c> está disponible en <paramref name="session"/>. Si no está instalado en
    /// <c>codenetix-tools</c>, lo instala con un contenedor efímero abriendo <c>registry.npmjs.org:443</c> en el
    /// Gateway solo durante la instalación. Lanza <see cref="SandboxUnavailableException"/> si no se puede instalar.
    /// </summary>
    Task EnsureClaudeCliAsync(ISandboxSession session, CancellationToken cancellationToken);
}

/// <summary>Resumen de recursos huérfanos eliminados en Docker al arrancar (AD-18, FR-24).</summary>
public sealed record SandboxCleanupSummary(int ContainersRemoved, int VolumesRemoved);

/// <summary>
/// Limpieza de recursos huérfanos del Sandbox al arrancar el Host (AD-18, FR-24): detiene y borra contenedores con
/// etiqueta <c>codenetix.run</c> y volúmenes <c>codenetix-ws-*</c> (preservando siempre <c>codenetix-tools</c> y
/// cualquier volumen ajeno). Es best-effort y nunca lanza si Docker no responde.
/// </summary>
public interface ISandboxReconciler
{
    /// <summary>Elimina contenedores y volúmenes huérfanos del Sandbox y devuelve el resumen.</summary>
    Task<SandboxCleanupSummary> CleanupOrphansAsync(CancellationToken cancellationToken);
}

/// <summary>Sesión activa registrada en el Egress Gateway para una Ejecución (AD-7).</summary>
/// <param name="RunId">Identificador de la Ejecución.</param>
/// <param name="Token">Credencial efímera <c>cnx-run-&lt;runId&gt;-&lt;nonce&gt;</c>.</param>
/// <param name="ContainerIp">IP del contenedor en la red <c>codenetix-sandbox</c>, si se conoce.</param>
public sealed record SandboxGatewaySession(Guid RunId, string Token, string? ContainerIp = null);

/// <summary>
/// Registro de sesiones activas del Sandbox en el Egress Gateway (AD-7). Mantiene por <c>runId</c> su credencial
/// efímera <c>cnx-run-&lt;runId&gt;-&lt;nonce&gt;</c> y la IP del contenedor en <c>codenetix-sandbox</c>, y las da de
/// baja en <c>StopAsync</c>/<c>DisposeAsync</c>.
/// </summary>
public interface ISandboxGateway
{
    /// <summary>Genera un token efímero <c>cnx-run-&lt;runId&gt;-&lt;nonce&gt;</c> y registra la sesión activa.</summary>
    SandboxGatewaySession Register(Guid runId);

    /// <summary>Registra o actualiza una sesión activa con el token efímero indicado.</summary>
    SandboxGatewaySession Register(Guid runId, string token, string? containerIp = null);

    /// <summary>Actualiza la IP del contenedor en <c>codenetix-sandbox</c> para una sesión activa.</summary>
    void UpdateContainerIp(Guid runId, string? containerIp);

    /// <summary>Da de baja la sesión de <paramref name="runId"/> y revoca su credencial efímera.</summary>
    bool Revoke(Guid runId);

    /// <summary>Busca la sesión activa de <paramref name="runId"/>.</summary>
    bool TryGet(Guid runId, out SandboxGatewaySession? session);

    /// <summary>Valida en tiempo constante una credencial efímera y devuelve su sesión si sigue activa.</summary>
    bool TryAuthenticate(string? token, out SandboxGatewaySession? session);

    /// <summary>
    /// Identifica el <c>runId</c> de una petición al proxy <c>CONNECT</c> por <c>Proxy-Authorization</c>, IP de origen
    /// del contenedor en <c>codenetix-sandbox</c> o única sesión activa.
    /// </summary>
    Guid? ResolveRunId(string? proxyAuthorization, string? remoteIp);
}

/// <summary>Comando para <see cref="ISandboxSession.ExecAsync"/>.</summary>
/// <param name="Executable">Ejecutable (se busca en el <c>PATH</c> del contenedor).</param>
/// <param name="Arguments">Argumentos, sin interpretar por ningún shell.</param>
public sealed record SandboxCommand(string Executable, ImmutableArray<string> Arguments = default)
{
    public const string DefaultWorkingDirectory = "/workspace";

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

    /// <summary>Directorio de trabajo dentro del contenedor (por defecto <c>/workspace</c>).</summary>
    public string WorkingDirectory { get; init; } = DefaultWorkingDirectory;

    /// <summary>Variables de entorno adicionales (nunca secretos: el Sandbox no los ve, AD-7).</summary>
    public ImmutableDictionary<string, string> Environment { get; init; } = ImmutableDictionary<string, string>.Empty;

    /// <summary>Tiempo máximo del comando (por defecto 10 minutos).</summary>
    public TimeSpan Timeout { get; init; } = DefaultTimeout;

    public ImmutableArray<string> Arguments { get; init; } = Arguments.IsDefault ? [] : Arguments;
}

/// <summary>Resultado de <see cref="ISandboxSession.ExecAsync"/>.</summary>
/// <param name="ExitCode">Código de salida (no significativo si <see cref="TimedOut"/>).</param>
/// <param name="StdOut">Salida estándar, como mucho <see cref="MaxOutputBytes"/> bytes (UTF-8).</param>
/// <param name="StdErr">Salida de error, como mucho <see cref="MaxOutputBytes"/> bytes (UTF-8).</param>
/// <param name="TimedOut">El comando no terminó en su timeout y se ha matado.</param>
/// <param name="Truncated">Algún flujo superó <see cref="MaxOutputBytes"/> y se ha recortado.</param>
public sealed record SandboxExecResult(int ExitCode, string StdOut, string StdErr, bool TimedOut, bool Truncated)
{
    /// <summary>Límite de cada flujo: 1 MiB.</summary>
    public const int MaxOutputBytes = 1024 * 1024;
}

/// <summary>No se ha podido crear el Sandbox; <see cref="Exception.Message"/> es el motivo (en español, sin secretos).</summary>
public sealed class SandboxCreationException : Exception
{
    public SandboxCreationException()
        : base("no se ha podido crear el Sandbox")
    {
    }

    public SandboxCreationException(string reason)
        : base(reason)
    {
    }

    public SandboxCreationException(string reason, Exception innerException)
        : base(reason, innerException)
    {
    }
}

/// <summary>El Sandbox no está disponible (detenido, liberado o Docker no responde).</summary>
public sealed class SandboxUnavailableException : Exception
{
    public SandboxUnavailableException()
        : base("el Sandbox no está disponible")
    {
    }

    public SandboxUnavailableException(string reason)
        : base(reason)
    {
    }

    public SandboxUnavailableException(string reason, Exception innerException)
        : base(reason, innerException)
    {
    }
}
