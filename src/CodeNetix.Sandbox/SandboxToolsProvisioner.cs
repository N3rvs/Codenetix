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
/// <see cref="ISandboxToolsProvisioner"/> (AD-6): instala <c>@anthropic-ai/claude-code</c> en el volumen
/// <c>codenetix-tools</c> la primera vez que hace falta.
/// <list type="number">
/// <item>Si <c>claude</c> ya está en el <c>PATH</c> del Sandbox (<c>/tools/node_modules/.bin</c>, <c>/tools/bin</c> o la
/// imagen), no hace nada.</item>
/// <item>Si no, abre <see cref="NpmRegistryHost"/> en el proxy <c>CONNECT</c> solo durante la instalación y ejecuta un
/// contenedor efímero en la red del Sandbox con <c>codenetix-tools</c> montado en lectura/escritura en <c>/tools</c>
/// (<c>npm install --prefix /tools …</c>).</item>
/// <item>En el <c>finally</c> revoca el permiso de npm y borra el contenedor.</item>
/// </list>
/// Las instalaciones se serializan: dos Ejecuciones simultáneas no escriben a la vez en el volumen.
/// </summary>
internal sealed partial class SandboxToolsProvisioner(
    DockerClient docker,
    NuGetConnectProxy proxy,
    IOptions<SandboxOptions> options,
    ILogger<SandboxToolsProvisioner> logger) : ISandboxToolsProvisioner, IDisposable
{
    public const string NpmRegistryHost = "registry.npmjs.org:443";

    public const string ClaudePackage = "@anthropic-ai/claude-code@" + ISandboxToolsProvisioner.ClaudeCliVersion;

    /// <summary>Tiempo máximo de la instalación.</summary>
    internal static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _gate = new(1, 1);

    public static string ContainerNameOf(Guid runId) => "codenetix-tools-" + RunShortId.Of(runId);

    public async Task EnsureClaudeCliAsync(ISandboxSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (await IsInstalledAsync(session, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Otra Ejecución puede haberlo instalado mientras se esperaba.
            if (await IsInstalledAsync(session, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            await InstallAsync(session, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        if (!await IsInstalledAsync(session, cancellationToken).ConfigureAwait(false))
        {
            throw new SandboxUnavailableException($"{ClaudePackage} no está disponible en {SandboxManager.ToolsVolume} tras instalarlo");
        }
    }

    public void Dispose() => _gate.Dispose();

    /// <summary><c>command -v claude</c> en el Sandbox (su <c>PATH</c> empieza por <c>/tools/node_modules/.bin:/tools/bin</c>).</summary>
    private static async Task<bool> IsInstalledAsync(ISandboxSession session, CancellationToken cancellationToken)
    {
        var probe = new SandboxCommand("sh", ["-c", "command -v claude"]) { Timeout = ProbeTimeout };
        var result = await session.ExecAsync(probe, cancellationToken).ConfigureAwait(false);
        return !result.TimedOut && result.ExitCode == 0;
    }

    private async Task InstallAsync(ISandboxSession session, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var shortId = RunShortId.Of(session.RunId);
        var name = ContainerNameOf(session.RunId);
        LogInstalling(shortId, ClaudePackage);

        string? extraHost;
        using (var inspect = await docker.InspectContainerAsync(session.ContainerId, cancellationToken).ConfigureAwait(false))
        {
            extraHost = inspect is null ? null : ExtractExtraHost(inspect.RootElement);
        }

        var body = BuildInstallerBody(session.Image, SandboxManager.LabelsOf(session.RunId, settings), settings, extraHost);
        string? containerId = null;
        try
        {
            // Solo durante la instalación: registry.npmjs.org:443 queda abierto en el proxy CONNECT.
            using (proxy.AllowTemporaryHost(NpmRegistryHost))
            {
                await docker.RemoveContainerAsync(name, cancellationToken).ConfigureAwait(false);
                containerId = await docker.CreateContainerAsync(name, body, cancellationToken).ConfigureAwait(false);
                await docker.StartContainerAsync(containerId, cancellationToken).ConfigureAwait(false);
                var exitCode = await docker.WaitContainerAsync(containerId, InstallTimeout, cancellationToken).ConfigureAwait(false);
                if (exitCode != 0)
                {
                    throw new SandboxUnavailableException(
                        $"no se ha podido instalar {ClaudePackage} en {SandboxManager.ToolsVolume} (npm ha salido con {exitCode.ToString(CultureInfo.InvariantCulture)})");
                }
            }
        }
        catch (Exception ex) when (ex is DockerApiException or HttpRequestException or IOException or TimeoutException)
        {
            throw new SandboxUnavailableException($"no se ha podido instalar {ClaudePackage} en {SandboxManager.ToolsVolume}: {ex.Message}", ex);
        }
        finally
        {
            if (containerId is not null)
            {
                await RemoveQuietlyAsync(shortId, containerId).ConfigureAwait(false);
            }
        }

        LogInstalled(shortId, ClaudePackage);
    }

    /// <summary>
    /// Cuerpo del contenedor de instalación: el del Sandbox (red aislada, proxy, límites, raíz en solo lectura) pero
    /// con únicamente <c>codenetix-tools</c> montado en lectura/escritura y <c>npm install</c> como comando.
    /// </summary>
    internal static JsonObject BuildInstallerBody(string image, IReadOnlyDictionary<string, string> labels, SandboxOptions settings, string? extraHost)
    {
        var body = SandboxManager.BuildContainerBody(image, "unused", labels, settings, apiKey: null, extraHost);
        body["Cmd"] = new JsonArray("npm", "install", "--prefix", SandboxManager.ToolsPath, "--no-audit", "--no-fund", ClaudePackage);
        body["WorkingDir"] = SandboxManager.ToolsPath;
        var hostConfig = body["HostConfig"]!.AsObject();
        hostConfig["Mounts"] = new JsonArray(
            new JsonObject { ["Type"] = "volume", ["Source"] = SandboxManager.ToolsVolume, ["Target"] = SandboxManager.ToolsPath, ["ReadOnly"] = false });
        return body;
    }

    private static string? ExtractExtraHost(JsonElement root) =>
        root.TryGetProperty("HostConfig", out var hostConfig)
        && hostConfig.ValueKind == JsonValueKind.Object
        && hostConfig.TryGetProperty("ExtraHosts", out var hosts)
        && hosts.ValueKind == JsonValueKind.Array
            ? hosts.EnumerateArray().Where(h => h.ValueKind == JsonValueKind.String).Select(h => h.GetString()).FirstOrDefault(h => !string.IsNullOrWhiteSpace(h))
            : null;

    private async Task RemoveQuietlyAsync(string shortId, string containerId)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await docker.RemoveContainerAsync(containerId, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (SandboxSession.IsNonFatal(ex))
        {
            LogCleanupFailed(shortId, containerId, ex.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Ejecución {ShortId}: instalando {Package} en codenetix-tools.")]
    private partial void LogInstalling(string shortId, string package);

    [LoggerMessage(Level = LogLevel.Information, Message = "Ejecución {ShortId}: {Package} instalado en codenetix-tools.")]
    private partial void LogInstalled(string shortId, string package);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ejecución {ShortId}: no se ha podido borrar el contenedor de instalación '{Name}': {Reason}.")]
    private partial void LogCleanupFailed(string shortId, string name, string reason);
}

