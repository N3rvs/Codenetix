using CodeNetix.Contracts;
using CodeNetix.Sandbox.Docker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace CodeNetix.Sandbox;

public static class SandboxServiceCollectionExtensions
{
    /// <summary>
    /// Registra el módulo Sandbox: <see cref="ISandboxManager"/> (AD-6: ningún otro módulo crea contenedores) y
    /// <see cref="ISandboxHealthCheck"/>, sobre un cliente interno de la API de Docker. Necesita
    /// <see cref="IRepositoryWorkspace"/> (GitHub) para exportar el árbol. Las <see cref="SandboxOptions"/> las
    /// enlaza Host (incluido el endpoint de Docker).
    /// </summary>
    public static IServiceCollection AddCodeNetixSandbox(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<SandboxOptions>();

        // El endpoint se interpreta al primer uso: uno inválido hace fallar la comprobación de salud (Diagnostic),
        // nunca el arranque del proceso.
        services.TryAddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<IOptions<SandboxOptions>>().Value;
            return new DockerClient(DockerEndpoint.ParseOrInvalid(settings.EffectiveDockerEndpoint));
        });
        services.TryAddSingleton<ISandboxManager, SandboxManager>();
        services.TryAddSingleton<ISandboxHealthCheck, SandboxHealthCheck>();
        return services;
    }
}
