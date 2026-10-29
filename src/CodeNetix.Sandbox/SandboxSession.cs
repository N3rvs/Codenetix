using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using CodeNetix.Contracts;
using CodeNetix.Sandbox.Docker;
using Microsoft.Extensions.Logging;

namespace CodeNetix.Sandbox;

/// <summary>
/// Sandbox vivo de una Ejecución (AD-6, AD-7). Los comandos se ejecutan por exec como <c>1000:1000</c>, envueltos en
/// <c>timeout</c> para que el proceso muera al vencer el plazo y la sesión siga usable. Se detiene (<c>stop</c> con
/// 10 s de gracia y, si sigue vivo, <c>kill</c>) al cancelarse el token de la Ejecución, al vencer la vida máxima o con
/// <see cref="StopAsync"/>, revocando su credencial efímera en <see cref="ISandboxGateway"/>. <see cref="DisposeAsync"/>
/// lo detiene y borra el contenedor y el volumen de trabajo.
/// </summary>
internal sealed partial class SandboxSession : ISandboxSession
{
    /// <summary>Segundos de gracia de <c>POST /containers/{id}/stop</c>.</summary>
    public const int StopGraceSeconds = 10;

    /// <summary>Margen tras el timeout de un comando antes de abandonar su flujo (si <c>timeout</c> no lo ha matado).</summary>
    internal static readonly TimeSpan ExecTimeoutGrace = TimeSpan.FromSeconds(10);

    /// <summary>Segundos que <c>timeout</c> espera tras SIGTERM antes de enviar SIGKILL.</summary>
    private const int KillAfterSeconds = 2;

    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Timeout máximo de un comando.</summary>
    internal static readonly TimeSpan MaxCommandTimeout = TimeSpan.FromHours(24);

    private readonly DockerClient _docker;
    private readonly ISandboxGateway? _gateway;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _lifetime;
    private readonly CancellationTokenRegistration _runRegistration;
    private readonly CancellationTokenRegistration _lifetimeRegistration;
    private readonly Lock _gate = new();
    private Task? _stopTask;
    private int _disposed;

    public SandboxSession(
        DockerClient docker,
        Guid runId,
        string containerId,
        string image,
        string volumeName,
        TimeSpan maxLifetime,
        ILogger logger,
        CancellationToken runToken,
        ISandboxGateway? gateway = null,
        string apiKey = "",
        string baseDigest = "")
    {
        _docker = docker;
        _gateway = gateway;
        _logger = logger;
        RunId = runId;
        ContainerId = containerId;
        Image = image;
        VolumeName = volumeName;
        ApiKey = apiKey;
        BaseDigest = baseDigest;

        // Vida máxima: al vencer, la sesión detiene el contenedor (los comandos en curso terminan).
        _lifetime = new CancellationTokenSource(maxLifetime);
        _lifetimeRegistration = _lifetime.Token.Register(() =>
        {
            LogLifetimeExpired(RunShortId.Of(RunId), maxLifetime.TotalMinutes);
            _ = StopAsync();
        });

        // Cancelación de la Ejecución: detenido en 10 s como máximo.
        _runRegistration = runToken.Register(() => _ = StopAsync());
    }

    public Guid RunId { get; }

    public string ContainerId { get; }

    public string Image { get; }

    public string VolumeName { get; }

    internal string ApiKey { get; }

    /// <summary>Huella (<see cref="WorkspaceManifest"/>) del árbol del <c>BaseSha</c> subido a <c>/workspace</c>.</summary>
    internal string BaseDigest { get; }

    /// <summary>Tiempo máximo del cálculo de la huella de <c>/workspace</c>.</summary>
    internal static readonly TimeSpan ManifestTimeout = TimeSpan.FromMinutes(5);

    public async Task<bool> HasChangesAsync(CancellationToken cancellationToken)
    {
        var command = new SandboxCommand("sh", ["-c", WorkspaceManifest.Script])
        {
            WorkingDirectory = SandboxManager.WorkspacePath,
            Timeout = ManifestTimeout,
        };
        var result = await ExecAsync(command, cancellationToken).ConfigureAwait(false);
        if (result.TimedOut || result.ExitCode != 0 || WorkspaceManifest.ParseDigest(result.StdOut) is not { } digest)
        {
            var detail = result.TimedOut ? "timeout" : $"código {result.ExitCode.ToString(CultureInfo.InvariantCulture)}";
            throw new SandboxUnavailableException($"no se ha podido inspeccionar /workspace ({detail})");
        }

        return !string.Equals(digest, BaseDigest, StringComparison.Ordinal);
    }

    /// <summary>Se ha pedido la parada (por cancelación, vida máxima, <see cref="StopAsync"/> o liberación).</summary>
    public bool IsStopping
    {
        get
        {
            lock (_gate)
            {
                return _stopTask is not null;
            }
        }
    }

    public async Task<SandboxExecResult> ExecAsync(SandboxCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateCommand(command);
        cancellationToken.ThrowIfCancellationRequested();
        if (IsStopping || Volatile.Read(ref _disposed) == 1)
        {
            throw new SandboxUnavailableException("el Sandbox está detenido");
        }

        // El proceso del exec no se puede matar por la API: cancelar el comando detiene el contenedor.
        using var cancelRegistration = cancellationToken.Register(() => _ = StopAsync());

        var seconds = Math.Max(1, (int)Math.Ceiling(command.Timeout.TotalSeconds));
        var cmd = new JsonArray
        {
            "timeout",
            $"--kill-after={KillAfterSeconds.ToString(CultureInfo.InvariantCulture)}s",

            // Fin de las opciones de timeout (GNU solo admite "--" antes de la duración): nada posterior es una opción.
            "--",
            $"{seconds.ToString(CultureInfo.InvariantCulture)}s",
            command.Executable,
        };
        foreach (var argument in command.Arguments)
        {
            cmd.Add(argument);
        }

        var env = new JsonArray();
        foreach (var (key, value) in command.Environment.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            env.Add($"{key}={value}");
        }

        var body = new JsonObject
        {
            ["AttachStdin"] = false,
            ["AttachStdout"] = true,
            ["AttachStderr"] = true,
            ["Tty"] = false,
            ["Privileged"] = false,
            ["User"] = SandboxManager.SandboxUser,
            ["WorkingDir"] = command.WorkingDirectory,
            ["Env"] = env,
            ["Cmd"] = cmd,
        };

        try
        {
            var execId = await _docker.CreateExecAsync(ContainerId, body, cancellationToken).ConfigureAwait(false);
            var stopwatch = Stopwatch.StartNew();

            // Plazo del flujo: el timeout del comando más un margen (por si timeout no llegara a matarlo).
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(command.Timeout + TimeSpan.FromSeconds(KillAfterSeconds) + ExecTimeoutGrace);

            // Fuera del lector: lo leído hasta un abandono o una cancelación se conserva.
            var stdout = new BoundedOutput(SandboxExecResult.MaxOutputBytes);
            var stderr = new BoundedOutput(SandboxExecResult.MaxOutputBytes);
            var abandoned = false;
            using (var response = await _docker.StartExecAsync(execId, deadline.Token).ConfigureAwait(false))
            {
                var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
                await using (stream.ConfigureAwait(false))
                {
                    try
                    {
                        await MultiplexedStreamReader.ReadAsync(stream, stdout, stderr, deadline.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        // El flujo sigue abierto tras el plazo y el margen: se abandona (con lo leído hasta ahora).
                        abandoned = true;
                    }
                }
            }

            stopwatch.Stop();
            cancellationToken.ThrowIfCancellationRequested();

            // El flujo ha terminado porque se ha detenido el contenedor (vida máxima, StopAsync): no hay resultado.
            if (IsStopping)
            {
                throw new SandboxUnavailableException("el Sandbox se ha detenido durante el comando");
            }

            var (running, exitCode) = await _docker.InspectExecAsync(execId, CancellationToken.None).ConfigureAwait(false);
            if (abandoned)
            {
                // Un nieto (p. ej. un servidor de compilación) puede mantener abierta la tubería aunque el proceso haya
                // terminado: si ya no corre, su código es el real; si sigue vivo, es un timeout.
                return running || exitCode is null
                    ? new SandboxExecResult(-1, stdout.ToString(), stderr.ToString(), TimedOut: true, stdout.Truncated || stderr.Truncated)
                    : new SandboxExecResult(exitCode.Value, stdout.ToString(), stderr.ToString(), TimedOut: false, stdout.Truncated || stderr.Truncated);
            }

            var code = exitCode ?? -1;

            // timeout devuelve 124 (SIGTERM) o 137 (SIGKILL tras --kill-after); solo cuenta si ha vencido el plazo.
            var timedOut = code is 124 or 137 && stopwatch.Elapsed >= command.Timeout;
            return new SandboxExecResult(code, stdout.ToString(), stderr.ToString(), timedOut, stdout.Truncated || stderr.Truncated);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is DockerApiException or HttpRequestException or IOException or TimeoutException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException("Comando cancelado.", ex, cancellationToken);
            }

            throw new SandboxUnavailableException(IsStopping ? "el Sandbox se ha detenido durante el comando" : $"Docker ha fallado al ejecutar el comando: {ex.Message}", ex);
        }
    }

    public Task StopAsync()
    {
        _gateway?.Revoke(RunId);
        lock (_gate)
        {
            return _stopTask ??= Task.Run(StopCoreAsync);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _gateway?.Revoke(RunId);
        await _runRegistration.DisposeAsync().ConfigureAwait(false);
        await _lifetimeRegistration.DisposeAsync().ConfigureAwait(false);

        await StopAsync().ConfigureAwait(false);
        _lifetime.Dispose();

        var shortId = RunShortId.Of(RunId);
        try
        {
            using var timeout = new CancellationTokenSource(CleanupTimeout);
            await _docker.RemoveContainerAsync(ContainerId, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsNonFatal(ex))
        {
            LogCleanupFailed("contenedor", ContainerId, shortId, ex.Message);
        }

        try
        {
            using var timeout = new CancellationTokenSource(CleanupTimeout);
            await _docker.RemoveVolumeAsync(VolumeName, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsNonFatal(ex))
        {
            LogCleanupFailed("volumen", VolumeName, shortId, ex.Message);
        }

        LogReleased(shortId);
    }

    /// <summary><c>stop?t=10</c>; si falla o el contenedor sigue vivo, <c>kill</c>. Nunca lanza.</summary>
    private async Task StopCoreAsync()
    {
        var shortId = RunShortId.Of(RunId);
        var stopped = false;
        try
        {
            // El plazo de la llamada cubre la gracia: Docker responde cuando el contenedor ya ha parado.
            await _docker.StopContainerAsync(ContainerId, StopGraceSeconds, TimeSpan.FromSeconds(StopGraceSeconds + 2), CancellationToken.None).ConfigureAwait(false);
            stopped = true;
        }
        catch (Exception ex) when (IsNonFatal(ex))
        {
            LogStopFailed(shortId, ex.Message);
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var running = !stopped;
            if (stopped)
            {
                using var inspect = await _docker.InspectContainerAsync(ContainerId, timeout.Token).ConfigureAwait(false);
                running = inspect is not null
                    && inspect.RootElement.TryGetProperty("State", out var state)
                    && state.TryGetProperty("Running", out var r)
                    && r.ValueKind == System.Text.Json.JsonValueKind.True;
            }

            // Si stop ha fallado o el contenedor sigue vivo: kill.
            if (running)
            {
                await _docker.KillContainerAsync(ContainerId, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (IsNonFatal(ex))
        {
            LogStopFailed(shortId, ex.Message);
        }

        LogStopped(shortId);
    }

    private static void ValidateCommand(SandboxCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.Executable))
        {
            throw new ArgumentException("El comando no tiene ejecutable.", nameof(command));
        }

        if (command.Timeout <= TimeSpan.Zero || command.Timeout > MaxCommandTimeout)
        {
            throw new ArgumentException("El timeout del comando debe ser positivo y de 24 h como mucho.", nameof(command));
        }

        if (string.IsNullOrWhiteSpace(command.WorkingDirectory) || !command.WorkingDirectory.StartsWith('/'))
        {
            throw new ArgumentException("El directorio de trabajo debe ser una ruta absoluta del contenedor.", nameof(command));
        }

        foreach (var key in command.Environment.Keys)
        {
            if (key.Length == 0 || key.Contains('=', StringComparison.Ordinal) || key.Contains('\0', StringComparison.Ordinal))
            {
                throw new ArgumentException($"Nombre de variable de entorno no válido: '{key}'.", nameof(command));
            }
        }
    }

    internal static bool IsNonFatal(Exception ex) =>
        ex is DockerApiException or HttpRequestException or IOException or TimeoutException or OperationCanceledException or ObjectDisposedException;

    [LoggerMessage(Level = LogLevel.Information, Message = "Sandbox de la Ejecución {ShortId} detenido.")]
    private partial void LogStopped(string shortId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se ha podido detener el Sandbox de la Ejecución {ShortId}: {Reason}.")]
    private partial void LogStopFailed(string shortId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "El Sandbox de la Ejecución {ShortId} ha alcanzado su vida máxima ({Minutes} min): se detiene.")]
    private partial void LogLifetimeExpired(string shortId, double minutes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se ha podido borrar el {Kind} '{Name}' del Sandbox de la Ejecución {ShortId}: {Reason}.")]
    private partial void LogCleanupFailed(string kind, string name, string shortId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Sandbox de la Ejecución {ShortId} liberado.")]
    private partial void LogReleased(string shortId);
}
