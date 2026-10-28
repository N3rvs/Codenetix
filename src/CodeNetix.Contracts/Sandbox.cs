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
    /// Detiene el contenedor: <c>stop</c> con 10 s de gracia y, si sigue vivo, <c>kill</c>. Idempotente; nunca lanza.
    /// </summary>
    Task StopAsync();
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
