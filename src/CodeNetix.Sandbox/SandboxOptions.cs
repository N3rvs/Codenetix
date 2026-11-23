namespace CodeNetix.Sandbox;

/// <summary>
/// Opciones del módulo Sandbox (<c>CODENETIX__Sandbox__*</c>). Las enlaza Host (el módulo no lee el entorno);
/// Host resuelve también <see cref="DockerEndpoint"/> a partir de <c>DOCKER_HOST</c> y <see cref="Coder"/> a partir de
/// <c>CODENETIX__Providers__*</c> y <c>CODENETIX__Agents__Coder__*</c>.
/// </summary>
public sealed class SandboxOptions
{
    public const string DefaultImage = "codenetix-sandbox:latest";

    public const string DefaultNetworkName = "codenetix-sandbox";

    public const string GatewayAlias = "gateway";

    public const int DefaultLlmGatewayPort = 8080;

    public const int DefaultConnectProxyPort = 8081;

    public const string DefaultNuGetHost = "api.nuget.org:443";

    public const double DefaultCpus = 2;

    public const int DefaultMemoryMb = 4096;

    public const int DefaultPidsLimit = 512;

    public const int DefaultMaxLifetimeMinutes = 60;

    /// <summary>Vida máxima admitida (24 h); por encima, la instancia queda en <c>Diagnostic</c>.</summary>
    public const int MaxLifetimeMinutesLimit = 1440;

    /// <summary><c>unix:///var/run/docker.sock</c> (Linux y macOS) o <c>npipe://./pipe/docker_engine</c> (Windows).</summary>
    public static string DefaultDockerEndpoint =>
        OperatingSystem.IsWindows() ? "npipe://./pipe/docker_engine" : "unix:///var/run/docker.sock";

    /// <summary>
    /// <c>CODENETIX__Sandbox__DockerEndpoint</c>: <c>unix://</c>, <c>npipe://</c> o <c>tcp://</c> (sin TLS). Sin
    /// valor, <c>DOCKER_HOST</c> o <see cref="DefaultDockerEndpoint"/>.
    /// </summary>
    public string? DockerEndpoint { get; set; }

    /// <summary><c>CODENETIX__Sandbox__Image</c> (por defecto <see cref="DefaultImage"/>).</summary>
    public string Image { get; set; } = DefaultImage;

    /// <summary><c>CODENETIX__Sandbox__NetworkName</c>: red Docker <c>internal</c> del Sandbox (por defecto <see cref="DefaultNetworkName"/>).</summary>
    public string NetworkName { get; set; } = DefaultNetworkName;

    /// <summary>
    /// <c>CODENETIX__Sandbox__HostContainer</c>: nombre o id del contenedor en el que corre CodeNetix para unirlo a
    /// <see cref="NetworkName"/> con el alias <see cref="GatewayAlias"/>. Vacío cuando el host corre fuera de contenedor.
    /// </summary>
    public string? HostContainer { get; set; }

    /// <summary><c>CODENETIX__Sandbox__LlmGatewayPort</c>: puerto del proxy inverso LLM <c>/llm/**</c> (por defecto 8080).</summary>
    public int LlmGatewayPort { get; set; } = DefaultLlmGatewayPort;

    /// <summary><c>CODENETIX__Sandbox__ConnectProxyPort</c>: puerto del proxy <c>CONNECT</c> de NuGet (por defecto 8081; 0 para puerto efímero en pruebas).</summary>
    public int ConnectProxyPort { get; set; } = DefaultConnectProxyPort;

    /// <summary><c>CODENETIX__Sandbox__AllowedNuGetHosts</c>: destinos <c>&lt;host&gt;:&lt;port&gt;</c> permitidos en el proxy <c>CONNECT</c> (por defecto <c>api.nuget.org:443</c>).</summary>
    public IList<string> AllowedNuGetHosts { get; set; } = [DefaultNuGetHost];

    /// <summary><c>CODENETIX__Sandbox__Cpus</c>: CPUs del contenedor (por defecto 2).</summary>
    public double Cpus { get; set; } = DefaultCpus;

    /// <summary><c>CODENETIX__Sandbox__MemoryMb</c>: memoria máxima en MiB, sin swap (por defecto 4096).</summary>
    public int MemoryMb { get; set; } = DefaultMemoryMb;

    /// <summary><c>CODENETIX__Sandbox__PidsLimit</c>: procesos máximos (por defecto 512).</summary>
    public int PidsLimit { get; set; } = DefaultPidsLimit;

    /// <summary><c>CODENETIX__Sandbox__MaxLifetimeMinutes</c>: al vencer, la sesión detiene el contenedor (1–1440, por defecto 60).</summary>
    public int MaxLifetimeMinutes { get; set; } = DefaultMaxLifetimeMinutes;

    /// <summary>Proveedor del Programador resuelto por Host para el proxy inverso LLM.</summary>
    public SandboxCoderEndpoint? Coder { get; set; }

    /// <summary>Endpoint efectivo: <see cref="DockerEndpoint"/> o <see cref="DefaultDockerEndpoint"/>.</summary>
    public string EffectiveDockerEndpoint =>
        string.IsNullOrWhiteSpace(DockerEndpoint) ? DefaultDockerEndpoint : DockerEndpoint.Trim();

    /// <summary>Nombre efectivo de la red Docker: <see cref="NetworkName"/> o <see cref="DefaultNetworkName"/>.</summary>
    public string EffectiveNetworkName =>
        string.IsNullOrWhiteSpace(NetworkName) ? DefaultNetworkName : NetworkName.Trim();

    /// <summary>Etiquetas extra de cada volumen y contenedor (solo pruebas: marcan lo que deben limpiar).</summary>
    internal Dictionary<string, string> ExtraLabels { get; } = new(StringComparer.Ordinal);

    /// <summary>Vida máxima del contenedor (solo pruebas: permite valores por debajo del minuto).</summary>
    internal TimeSpan? MaxLifetimeOverride { get; set; }

    internal TimeSpan MaxLifetime => MaxLifetimeOverride ?? TimeSpan.FromMinutes(MaxLifetimeMinutes);
}

/// <summary>Proveedor ya resuelto por Host para el rol <c>Coder</c> en el Egress Gateway (AD-7).</summary>
/// <param name="Provider">Nombre del Proveedor (<c>Anthropic</c>, <c>OpenAI</c>, …).</param>
/// <param name="BaseUrl">URL base del Proveedor (p. ej. <c>https://api.anthropic.com/v1/</c>).</param>
/// <param name="ApiKey">Key real del Proveedor; nunca se registra ni viaja al Sandbox.</param>
/// <param name="Model">Modelo por defecto del rol <c>Coder</c>.</param>
public sealed record SandboxCoderEndpoint(string Provider, Uri BaseUrl, string ApiKey, string Model = "claude-sonnet-5-5")
{
    public SandboxCoderEndpoint(string provider, string baseUrl, string apiKey, string model = "claude-sonnet-5-5")
        : this(provider, new Uri(baseUrl), apiKey, model)
    {
    }

    /// <summary>Sin la key: los records generan un <c>ToString</c> con todas las propiedades.</summary>
    public override string ToString() => $"{Provider} ({BaseUrl}, {Model})";
}
