namespace CodeNetix.Sandbox;

/// <summary>
/// Opciones del módulo Sandbox (<c>CODENETIX__Sandbox__*</c>). Las enlaza Host (el módulo no lee el entorno);
/// Host resuelve también <see cref="DockerEndpoint"/> a partir de <c>DOCKER_HOST</c>.
/// </summary>
public sealed class SandboxOptions
{
    public const string DefaultImage = "codenetix-sandbox:latest";

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

    /// <summary><c>CODENETIX__Sandbox__Cpus</c>: CPUs del contenedor (por defecto 2).</summary>
    public double Cpus { get; set; } = DefaultCpus;

    /// <summary><c>CODENETIX__Sandbox__MemoryMb</c>: memoria máxima en MiB, sin swap (por defecto 4096).</summary>
    public int MemoryMb { get; set; } = DefaultMemoryMb;

    /// <summary><c>CODENETIX__Sandbox__PidsLimit</c>: procesos máximos (por defecto 512).</summary>
    public int PidsLimit { get; set; } = DefaultPidsLimit;

    /// <summary><c>CODENETIX__Sandbox__MaxLifetimeMinutes</c>: al vencer, la sesión detiene el contenedor (1–1440, por defecto 60).</summary>
    public int MaxLifetimeMinutes { get; set; } = DefaultMaxLifetimeMinutes;

    /// <summary>Endpoint efectivo: <see cref="DockerEndpoint"/> o <see cref="DefaultDockerEndpoint"/>.</summary>
    public string EffectiveDockerEndpoint =>
        string.IsNullOrWhiteSpace(DockerEndpoint) ? DefaultDockerEndpoint : DockerEndpoint.Trim();

    /// <summary>Etiquetas extra de cada volumen y contenedor (solo pruebas: marcan lo que deben limpiar).</summary>
    internal Dictionary<string, string> ExtraLabels { get; } = new(StringComparer.Ordinal);

    /// <summary>Vida máxima del contenedor (solo pruebas: permite valores por debajo del minuto).</summary>
    internal TimeSpan? MaxLifetimeOverride { get; set; }

    internal TimeSpan MaxLifetime => MaxLifetimeOverride ?? TimeSpan.FromMinutes(MaxLifetimeMinutes);
}
