using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeNetix.Contracts;
using CodeNetix.Sandbox.Docker;
using CodeNetix.Sandbox.Gateway;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeNetix.Sandbox;

/// <summary>
/// <see cref="ISandboxManager"/> sobre la API de Docker (AD-6, AD-7). Por Ejecución:
/// <list type="number">
/// <item>exporta el árbol del <c>BaseSha</c> (<see cref="IRepositoryWorkspace.ExportTreeAsync"/>) a un archivo temporal;</item>
/// <item>asegura la red Docker <c>internal</c> <c>codenetix-sandbox</c> y conecta en ella al contenedor host como
/// <c>gateway</c> (o añade <c>ExtraHosts: ["gateway:&lt;ip-bridge&gt;"]</c> cuando el host corre fuera de contenedor);</item>
/// <item>asegura el volumen <c>codenetix-tools</c> y crea <c>codenetix-ws-&lt;runId&gt;</c> (borrando antes uno de un
/// intento anterior de la misma Ejecución);</item>
/// <item>genera y registra en <see cref="ISandboxGateway"/> la credencial efímera <c>cnx-run-&lt;runId&gt;-&lt;nonce&gt;</c> y
/// crea el contenedor <c>codenetix-sbx-&lt;shortId&gt;</c> unido únicamente a <c>codenetix-sandbox</c>, con
/// <c>ANTHROPIC_BASE_URL</c>, <c>ANTHROPIC_API_KEY</c>, <c>HTTPS_PROXY</c>/<c>HTTP_PROXY</c> y <c>NO_PROXY</c>;</item>
/// <item>sube el tar a <c>/workspace</c> antes del <c>start</c>, arranca el contenedor y anota su IP en el Gateway.</item>
/// </list>
/// Si algo falla, revoca la credencial, borra lo creado a medias y lanza <see cref="SandboxCreationException"/>.
/// </summary>
internal sealed partial class SandboxManager(
    DockerClient docker,
    IRepositoryWorkspace workspace,
    IOptions<SandboxOptions> options,
    ILoggerFactory loggerFactory,
    ISandboxGateway? gateway = null) : ISandboxManager
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

    private const string DefaultBridgeGatewayFallback = "172.28.0.1";

    private static readonly TimeSpan UploadTimeout = TimeSpan.FromMinutes(10);

    private readonly ISandboxGateway _gateway = gateway ?? new SandboxGatewayRegistry();
    private readonly ILogger<SandboxManager> _logger = loggerFactory.CreateLogger<SandboxManager>();

    internal ISandboxGateway Gateway => _gateway;

    public static string VolumeNameOf(Guid runId) => "codenetix-ws-" + runId.ToString("D");

    public static string ContainerNameOf(Guid runId) => "codenetix-sbx-" + RunShortId.Of(runId);

    public async Task<ISandboxSession> CreateForRunAsync(Guid runId, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var image = string.IsNullOrWhiteSpace(settings.Image) ? SandboxOptions.DefaultImage : settings.Image.Trim();
        var networkName = settings.EffectiveNetworkName;
        var volume = VolumeNameOf(runId);
        var name = ContainerNameOf(runId);
        var labels = LabelsOf(runId, settings);

        string? containerId = null;
        var volumeCreated = false;
        var gatewayRegistered = false;
        try
        {
            // 1) Árbol del BaseSha (sin .git) a un temporal: si no hay árbol, falla antes de tocar Docker.
            var treePath = Path.Combine(Path.GetTempPath(), $"codenetix-tree-{runId:N}.tar");
            var tree = new FileStream(treePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            var apiKey = SandboxGatewayRegistry.GenerateToken(runId);
            await using (tree.ConfigureAwait(false))
            {
                await workspace.ExportTreeAsync(runId, tree, cancellationToken).ConfigureAwait(false);
                await tree.FlushAsync(cancellationToken).ConfigureAwait(false);

                // 2) Volúmenes y red aislada codenetix-sandbox (Internal: true) con enlace al Gateway.
                await docker.CreateVolumeAsync(ToolsVolume, new Dictionary<string, string> { [ManagedLabel] = "true" }, cancellationToken).ConfigureAwait(false);
                var extraHost = await EnsureNetworkAsync(networkName, settings, cancellationToken).ConfigureAwait(false);
                await RemoveLeftoversAsync(runId, name, volume, cancellationToken).ConfigureAwait(false);
                await docker.CreateVolumeAsync(volume, labels, cancellationToken).ConfigureAwait(false);
                volumeCreated = true;

                // 4) Sesión en el Gateway y contenedor (sin arrancar).
                _gateway.Register(runId, apiKey);
                gatewayRegistered = true;
                containerId = await docker.CreateContainerAsync(
                    name,
                    BuildContainerBody(image, volume, labels, settings, apiKey, extraHost),
                    cancellationToken).ConfigureAwait(false);

                // 5) Árbol a /workspace antes del start.
                await docker.PutArchiveAsync(containerId, WorkspacePath, new WorkspaceArchiveContent(tree), UploadTimeout, cancellationToken).ConfigureAwait(false);
            }

            await docker.StartContainerAsync(containerId, cancellationToken).ConfigureAwait(false);

            using (var inspect = await docker.InspectContainerAsync(containerId, cancellationToken).ConfigureAwait(false))
            {
                if (inspect is not null && ExtractContainerIp(inspect.RootElement, networkName) is { } containerIp)
                {
                    _gateway.UpdateContainerIp(runId, containerIp);
                }
            }

            var session = new SandboxSession(
                docker,
                runId,
                containerId,
                image,
                volume,
                settings.MaxLifetime,
                loggerFactory.CreateLogger<SandboxSession>(),
                cancellationToken,
                _gateway,
                apiKey);
            LogCreated(RunShortId.Of(runId), name, image);
            return session;
        }
        catch (Exception ex) when (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            if (gatewayRegistered)
            {
                _gateway.Revoke(runId);
            }

            await CleanupAsync(runId, containerId, volumeCreated ? volume : null).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            // Cualquier otro fallo (también inesperado): se revoca la credencial, se borra lo creado y se informa como fallo de creación.
            if (gatewayRegistered)
            {
                _gateway.Revoke(runId);
            }

            await CleanupAsync(runId, containerId, volumeCreated ? volume : null).ConfigureAwait(false);
            var reason = Describe(ex, image);
            LogCreationFailed(RunShortId.Of(runId), reason);
            throw new SandboxCreationException(reason, ex);
        }
    }

    /// <summary>Cuerpo de <c>POST /containers/create</c> (la forma la comprueban las pruebas contra un Docker falso).</summary>
    internal static JsonObject BuildContainerBody(
        string image,
        string volume,
        IReadOnlyDictionary<string, string> labels,
        SandboxOptions settings,
        string? apiKey = null,
        string? extraHost = null)
    {
        var labelsJson = new JsonObject();
        foreach (var (key, value) in labels)
        {
            labelsJson[key] = value;
        }

        var effectiveKey = string.IsNullOrWhiteSpace(apiKey) ? SandboxGatewayRegistry.GenerateToken(Guid.Empty) : apiKey;
        var llmPort = settings.LlmGatewayPort > 0 ? settings.LlmGatewayPort : SandboxOptions.DefaultLlmGatewayPort;
        var proxyPort = settings.ConnectProxyPort > 0 ? settings.ConnectProxyPort : SandboxOptions.DefaultConnectProxyPort;
        var proxyUrl = $"http://{SandboxOptions.GatewayAlias}:{proxyPort.ToString(CultureInfo.InvariantCulture)}";
        var noProxy = $"{SandboxOptions.GatewayAlias},localhost,127.0.0.1";
        var networkName = settings.EffectiveNetworkName;

        var memoryBytes = (long)settings.MemoryMb * 1024 * 1024;
        var hostConfig = new JsonObject
        {
            ["NetworkMode"] = networkName,
            ["ExtraHosts"] = string.IsNullOrWhiteSpace(extraHost) ? new JsonArray() : new JsonArray(extraHost),
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
        };

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
                "UseSharedCompilation=false",

                // Egress Gateway (AD-7): LLM por :8080/llm con credencial efímera y proxy CONNECT en :8081.
                $"ANTHROPIC_BASE_URL=http://{SandboxOptions.GatewayAlias}:{llmPort.ToString(CultureInfo.InvariantCulture)}/llm",
                $"ANTHROPIC_API_KEY={effectiveKey}",
                $"HTTP_PROXY={proxyUrl}",
                $"HTTPS_PROXY={proxyUrl}",
                $"http_proxy={proxyUrl}",
                $"https_proxy={proxyUrl}",
                $"NO_PROXY={noProxy}",
                $"no_proxy={noProxy}",
                "CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC=1",
                "DISABLE_TELEMETRY=1",
                "DISABLE_ERROR_REPORTING=1",
                "DISABLE_AUTOUPDATER=1",
                "DO_NOT_TRACK=1"),
            ["Labels"] = labelsJson,
            ["AttachStdin"] = false,
            ["AttachStdout"] = false,
            ["AttachStderr"] = false,
            ["Tty"] = false,
            ["OpenStdin"] = false,
            ["NetworkDisabled"] = false,
            ["HostConfig"] = hostConfig,
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
    /// Asegura que la red <paramref name="networkName"/> existe y tiene <c>Internal: true</c>. Si el host corre en un
    /// contenedor, lo une con el alias <c>gateway</c> y devuelve <c>null</c>; si corre fuera de contenedor, devuelve
    /// <c>gateway:&lt;ip-bridge&gt;</c> para <c>ExtraHosts</c>.
    /// </summary>
    private async Task<string?> EnsureNetworkAsync(string networkName, SandboxOptions settings, CancellationToken cancellationToken)
    {
        var network = await docker.InspectNetworkAsync(networkName, cancellationToken).ConfigureAwait(false);
        if (network is null)
        {
            await docker.CreateNetworkAsync(
                networkName,
                new Dictionary<string, string> { [ManagedLabel] = "true" },
                cancellationToken).ConfigureAwait(false);
            network = await docker.InspectNetworkAsync(networkName, cancellationToken).ConfigureAwait(false);
        }

        using (network)
        {
            if (network is null || !IsInternalNetwork(network.RootElement))
            {
                throw new SandboxCreationException(
                    $"la red '{networkName}' ya existe en Docker pero no es interna (Internal=false): bórrala con 'docker network rm {networkName}' para que CodeNetix la cree aislada");
            }

            var hostContainer = await ResolveHostContainerAsync(settings, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(hostContainer))
            {
                await docker.ConnectNetworkAsync(networkName, hostContainer, SandboxOptions.GatewayAlias, cancellationToken).ConfigureAwait(false);
                return null;
            }

            var gatewayIp = ExtractGatewayIp(network.RootElement);
            return $"{SandboxOptions.GatewayAlias}:{gatewayIp}";
        }
    }

    private async Task<string?> ResolveHostContainerAsync(SandboxOptions settings, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(settings.HostContainer))
        {
            return settings.HostContainer.Trim();
        }

        // Autodetección cuando CodeNetix corre dentro de un contenedor sobre el socket local de Docker.
        if (docker.Endpoint.Transport != DockerTransport.Tcp
            && File.Exists("/.dockerenv")
            && !string.IsNullOrWhiteSpace(Environment.MachineName))
        {
            var candidate = Environment.MachineName.Trim();
            using var inspect = await docker.InspectContainerAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (inspect is not null)
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool IsInternalNetwork(JsonElement root) =>
        root.TryGetProperty("Internal", out var prop) && prop.ValueKind == JsonValueKind.True;

    private static string ExtractGatewayIp(JsonElement root)
    {
        if (root.TryGetProperty("IPAM", out var ipam)
            && ipam.ValueKind == JsonValueKind.Object
            && ipam.TryGetProperty("Config", out var configs)
            && configs.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in configs.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (entry.TryGetProperty("Gateway", out var gw)
                    && gw.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(gw.GetString()))
                {
                    return gw.GetString()!.Trim();
                }

                if (entry.TryGetProperty("Subnet", out var subnet)
                    && subnet.ValueKind == JsonValueKind.String
                    && TryFirstIpFromSubnet(subnet.GetString(), out var derived))
                {
                    return derived;
                }
            }
        }

        return DefaultBridgeGatewayFallback;
    }

    private static bool TryFirstIpFromSubnet(string? cidr, out string ip)
    {
        ip = string.Empty;
        if (string.IsNullOrWhiteSpace(cidr))
        {
            return false;
        }

        var slash = cidr.IndexOf('/', StringComparison.Ordinal);
        var addressPart = slash > 0 ? cidr[..slash] : cidr;
        var lastDot = addressPart.LastIndexOf('.');
        if (lastDot <= 0 || !addressPart[(lastDot + 1)..].Equals("0", StringComparison.Ordinal))
        {
            return false;
        }

        ip = addressPart[..(lastDot + 1)] + "1";
        return true;
    }

    private static string? ExtractContainerIp(JsonElement root, string networkName)
    {
        if (!root.TryGetProperty("NetworkSettings", out var netSettings) || netSettings.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (netSettings.TryGetProperty("Networks", out var networks) && networks.ValueKind == JsonValueKind.Object)
        {
            if (networks.TryGetProperty(networkName, out var specific)
                && specific.ValueKind == JsonValueKind.Object
                && specific.TryGetProperty("IPAddress", out var ip)
                && ip.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(ip.GetString()))
            {
                return ip.GetString()!.Trim();
            }

            foreach (var prop in networks.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Object
                    && prop.Value.TryGetProperty("IPAddress", out var fallbackIp)
                    && fallbackIp.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(fallbackIp.GetString()))
                {
                    return fallbackIp.GetString()!.Trim();
                }
            }
        }

        if (netSettings.TryGetProperty("IPAddress", out var directIp)
            && directIp.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(directIp.GetString()))
        {
            return directIp.GetString()!.Trim();
        }

        return null;
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
