using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeNetix.Contracts;
using CodeNetix.Sandbox.Docker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeNetix.Sandbox;

/// <summary>
/// <see cref="ISandboxManager"/> sobre la API de Docker (AD-6). Por Ejecución:
/// <list type="number">
/// <item>exporta el árbol del <c>BaseSha</c> (<see cref="IRepositoryWorkspace.ExportTreeAsync"/>) a un archivo temporal;</item>
/// <item>asegura el volumen <c>codenetix-tools</c> y crea <c>codenetix-ws-&lt;runId&gt;</c> (borrando antes uno de un
/// intento anterior de la misma Ejecución);</item>
/// <item>crea el contenedor <c>codenetix-sbx-&lt;shortId&gt;</c>: etiquetas, límites, <c>NetworkMode: none</c>, solo
/// montajes de tipo <c>volume</c> (nunca <i>binds</i>), raíz de solo lectura, sin capacidades y usuario 1000:1000;</item>
/// <item>sube el tar a <c>/workspace</c> antes del <c>start</c> y arranca el contenedor (<c>sleep infinity</c>).</item>
/// </list>
/// Si algo falla, borra lo creado a medias y lanza <see cref="SandboxCreationException"/>.
/// </summary>
internal sealed partial class SandboxManager(
    DockerClient docker,
    IRepositoryWorkspace workspace,
    IOptions<SandboxOptions> options,
    ILoggerFactory loggerFactory) : ISandboxManager
{
    public const string RunLabel = "codenetix.run";

    public const string ManagedLabel = "codenetix.managed";

    public const string ToolsVolume = "codenetix-tools";

    public const string WorkspacePath = "/workspace";

    public const string ToolsPath = "/tools";

    public const string SandboxUser = "1000:1000";

    public const string BuildHint = "docker build -f docker/sandbox.Dockerfile -t codenetix-sandbox .";

    /// <summary>Tamaño del <c>tmpfs</c> de <c>/tmp</c> (HOME y caché de NuGet).</summary>
    public const string TmpfsOptions = "rw,nosuid,nodev,exec,size=2g,mode=1777";

    private static readonly TimeSpan UploadTimeout = TimeSpan.FromMinutes(10);

    private readonly ILogger<SandboxManager> _logger = loggerFactory.CreateLogger<SandboxManager>();

    public static string VolumeNameOf(Guid runId) => "codenetix-ws-" + runId.ToString("D");

    public static string ContainerNameOf(Guid runId) => "codenetix-sbx-" + RunShortId.Of(runId);

    public async Task<ISandboxSession> CreateForRunAsync(Guid runId, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var image = string.IsNullOrWhiteSpace(settings.Image) ? SandboxOptions.DefaultImage : settings.Image.Trim();
        var volume = VolumeNameOf(runId);
        var name = ContainerNameOf(runId);
        var labels = LabelsOf(runId, settings);

        string? containerId = null;
        var volumeCreated = false;
        try
        {
            // 1) Árbol del BaseSha (sin .git) a un temporal: si no hay árbol, falla antes de tocar Docker.
            var treePath = Path.Combine(Path.GetTempPath(), $"codenetix-tree-{runId:N}.tar");
            var tree = new FileStream(treePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            await using (tree.ConfigureAwait(false))
            {
                await workspace.ExportTreeAsync(runId, tree, cancellationToken).ConfigureAwait(false);
                await tree.FlushAsync(cancellationToken).ConfigureAwait(false);

                // 2) Volúmenes: codenetix-tools (se conserva siempre) y el de trabajo, limpio.
                await docker.CreateVolumeAsync(ToolsVolume, new Dictionary<string, string> { [ManagedLabel] = "true" }, cancellationToken).ConfigureAwait(false);
                await RemoveLeftoversAsync(runId, name, volume, cancellationToken).ConfigureAwait(false);
                await docker.CreateVolumeAsync(volume, labels, cancellationToken).ConfigureAwait(false);
                volumeCreated = true;

                // 3) Contenedor (sin arrancar).
                containerId = await docker.CreateContainerAsync(name, BuildContainerBody(image, volume, labels, settings), cancellationToken).ConfigureAwait(false);

                // 4) Árbol a /workspace antes del start.
                await docker.PutArchiveAsync(containerId, WorkspacePath, new WorkspaceArchiveContent(tree), UploadTimeout, cancellationToken).ConfigureAwait(false);
            }

            await docker.StartContainerAsync(containerId, cancellationToken).ConfigureAwait(false);

            var session = new SandboxSession(docker, runId, containerId, image, volume, settings.MaxLifetime, loggerFactory.CreateLogger<SandboxSession>(), cancellationToken);
            LogCreated(RunShortId.Of(runId), name, image);
            return session;
        }
        catch (Exception ex) when (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            await CleanupAsync(runId, containerId, volumeCreated ? volume : null).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            // Cualquier otro fallo (también inesperado): se borra lo creado y se informa como fallo de creación.
            await CleanupAsync(runId, containerId, volumeCreated ? volume : null).ConfigureAwait(false);
            var reason = Describe(ex, image);
            LogCreationFailed(RunShortId.Of(runId), reason);
            throw new SandboxCreationException(reason, ex);
        }
    }

    /// <summary>Cuerpo de <c>POST /containers/create</c> (la forma la comprueban las pruebas contra un Docker falso).</summary>
    internal static JsonObject BuildContainerBody(string image, string volume, IReadOnlyDictionary<string, string> labels, SandboxOptions settings)
    {
        var labelsJson = new JsonObject();
        foreach (var (key, value) in labels)
        {
            labelsJson[key] = value;
        }

        var memoryBytes = (long)settings.MemoryMb * 1024 * 1024;
        return new JsonObject
        {
            ["Image"] = image,
            ["Cmd"] = new JsonArray("sleep", "infinity"),
            ["User"] = SandboxUser,
            ["WorkingDir"] = WorkspacePath,
            ["Env"] = new JsonArray(
                "HOME=/tmp/home",
                "DOTNET_CLI_HOME=/tmp/home",
                "NUGET_PACKAGES=/tmp/nuget",
                "DOTNET_CLI_TELEMETRY_OPTOUT=1",
                "DOTNET_NOLOGO=1",

                // Sin procesos de compilación persistentes que hereden la tubería del exec y la mantengan abierta.
                "MSBUILDDISABLENODEREUSE=1",
                "DOTNET_CLI_USE_MSBUILD_SERVER=0",
                "UseSharedCompilation=false"),
            ["Labels"] = labelsJson,
            ["AttachStdin"] = false,
            ["AttachStdout"] = false,
            ["AttachStderr"] = false,
            ["Tty"] = false,
            ["OpenStdin"] = false,
            ["NetworkDisabled"] = true,
            ["HostConfig"] = new JsonObject
            {
                ["NetworkMode"] = "none",
                ["Privileged"] = false,
                ["Binds"] = new JsonArray(),
                ["Mounts"] = new JsonArray(
                    new JsonObject { ["Type"] = "volume", ["Source"] = volume, ["Target"] = WorkspacePath, ["ReadOnly"] = false },
                    new JsonObject { ["Type"] = "volume", ["Source"] = ToolsVolume, ["Target"] = ToolsPath, ["ReadOnly"] = true }),
                ["NanoCpus"] = (long)Math.Round(settings.Cpus * 1_000_000_000d),
                ["Memory"] = memoryBytes,
                ["MemorySwap"] = memoryBytes,
                ["PidsLimit"] = settings.PidsLimit,
                ["CapDrop"] = new JsonArray("ALL"),
                ["SecurityOpt"] = new JsonArray("no-new-privileges"),
                ["ReadonlyRootfs"] = true,
                ["Tmpfs"] = new JsonObject { ["/tmp"] = TmpfsOptions },
                ["Init"] = true,
                ["AutoRemove"] = false,
                ["RestartPolicy"] = new JsonObject { ["Name"] = "no" },
            },
        };
    }

    internal static Dictionary<string, string> LabelsOf(Guid runId, SandboxOptions settings)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in settings.ExtraLabels)
        {
            labels[key] = value;
        }

        labels[RunLabel] = runId.ToString("D");
        labels[ManagedLabel] = "true";
        return labels;
    }

    /// <summary>
    /// Restos de un intento anterior de la misma Ejecución (proceso caído a medias): el contenedor con su nombre (solo
    /// si lleva su etiqueta) y el volumen de trabajo.
    /// </summary>
    private async Task RemoveLeftoversAsync(Guid runId, string name, string volume, CancellationToken cancellationToken)
    {
        using (var existing = await docker.InspectContainerAsync(name, cancellationToken).ConfigureAwait(false))
        {
            if (existing is not null)
            {
                var owner = existing.RootElement.TryGetProperty("Config", out var config)
                    && config.TryGetProperty("Labels", out var labels)
                    && labels.ValueKind == JsonValueKind.Object
                    && labels.TryGetProperty(RunLabel, out var run)
                        ? run.GetString()
                        : null;
                if (!string.Equals(owner, runId.ToString("D"), StringComparison.OrdinalIgnoreCase))
                {
                    throw new SandboxCreationException($"ya existe un contenedor '{name}' que no pertenece a esta Ejecución");
                }

                await docker.RemoveContainerAsync(name, cancellationToken).ConfigureAwait(false);
            }
        }

        await docker.RemoveVolumeAsync(volume, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Borra lo creado a medias (de mejor esfuerzo: nunca lanza).</summary>
    private async Task CleanupAsync(Guid runId, string? containerId, string? volume)
    {
        var shortId = RunShortId.Of(runId);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        if (containerId is not null)
        {
            try
            {
                await docker.RemoveContainerAsync(containerId, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (SandboxSession.IsNonFatal(ex))
            {
                LogCleanupFailed(shortId, containerId, ex.Message);
            }
        }

        if (volume is not null)
        {
            try
            {
                await docker.RemoveVolumeAsync(volume, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (SandboxSession.IsNonFatal(ex))
            {
                LogCleanupFailed(shortId, volume, ex.Message);
            }
        }
    }

    /// <summary>Motivo en español y en una línea (sin secretos: Docker no los conoce).</summary>
    internal static string Describe(Exception ex, string image)
    {
        var reason = ex switch
        {
            SandboxCreationException => ex.Message,
            DockerApiException { StatusCode: 404 } docker when docker.Message.Contains("image", StringComparison.OrdinalIgnoreCase) =>
                $"falta la imagen {image}: constrúyela con {BuildHint}",
            DockerApiException docker => $"Docker ha respondido {(docker.StatusCode == 0 ? "con un error" : docker.StatusCode.ToString(CultureInfo.InvariantCulture))}: {docker.Message}",
            InvalidDataException => $"el árbol de trabajo no es válido: {ex.Message}",

            // HttpClient envuelve lo que lanza el cuerpo al serializarse (el tar reescrito).
            HttpRequestException { InnerException: InvalidDataException invalid } => $"el árbol de trabajo no es válido: {invalid.Message}",
            HttpRequestException { InnerException: IOException inner } => $"Docker no responde: {inner.Message}",
            HttpRequestException or IOException => $"Docker no responde: {ex.Message}",
            TimeoutException or OperationCanceledException => $"Docker no responde a tiempo: {ex.Message}",
            RepositoryPreparationException => $"no se ha podido exportar el árbol de trabajo: {ex.Message}",
            _ => $"error inesperado ({ex.GetType().Name}): {ex.Message}",
        };
        var oneLine = string.Join(' ', reason.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return oneLine.Length <= 400 ? oneLine : string.Concat(oneLine.AsSpan(0, 399), "…");
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Sandbox de la Ejecución {ShortId} creado ({Name}, imagen {Image}).")]
    private partial void LogCreated(string shortId, string name, string image);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se ha podido crear el Sandbox de la Ejecución {ShortId}: {Reason}.")]
    private partial void LogCreationFailed(string shortId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se ha podido borrar '{Name}' tras el fallo al crear el Sandbox de la Ejecución {ShortId}: {Reason}.")]
    private partial void LogCleanupFailed(string shortId, string name, string reason);
}
