using CodeNetix.Contracts;
using CodeNetix.Sandbox.Docker;
using CodeNetix.Sandbox.Gateway;
using CodeNetix.Sandbox.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace CodeNetix.Sandbox;

public static class SandboxServiceCollectionExtensions
{
    /// <summary>
    /// Registra el módulo Sandbox: <see cref="ISandboxManager"/> (AD-6: ningún otro módulo crea contenedores),
    /// <see cref="ISandboxHealthCheck"/>, la limpieza de huérfanos del arranque (<see cref="ISandboxReconciler"/>, AD-18)
    /// y el Egress Gateway (<see cref="ISandboxGateway"/>,
    /// <see cref="LlmReverseProxy"/> con YARP y <see cref="NuGetConnectProxy"/>, AD-7). Necesita
    /// <see cref="IRepositoryWorkspace"/> (GitHub) para exportar el árbol e <see cref="IRunEventSink"/> (Pipeline) para
    /// registrar eventos del Gateway. Las <see cref="SandboxOptions"/> las enlaza Host.
    /// </summary>
    public static IServiceCollection AddCodeNetixSandbox(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<SandboxOptions>();
        services.AddHttpForwarder();

        // El endpoint se interpreta al primer uso: uno inválido hace fallar la comprobación de salud (Diagnostic),
        // nunca el arranque del proceso.
        services.TryAddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<IOptions<SandboxOptions>>().Value;
            return new DockerClient(DockerEndpoint.ParseOrInvalid(settings.EffectiveDockerEndpoint));
        });
        services.TryAddSingleton<SandboxGatewayRegistry>();
        services.TryAddSingleton<ISandboxGateway>(sp => sp.GetRequiredService<SandboxGatewayRegistry>());
        services.TryAddSingleton<LlmReverseProxy>();
        services.TryAddSingleton<NuGetConnectProxy>();
        services.AddHostedService(sp => sp.GetRequiredService<NuGetConnectProxy>());
        services.TryAddSingleton<ISandboxManager, SandboxManager>();
        services.TryAddSingleton<ISandboxReconciler, SandboxReconciler>();
        services.TryAddSingleton<ISandboxToolsProvisioner, SandboxToolsProvisioner>();
        services.TryAddSingleton<ITestRunner, DotnetTestRunner>();
        services.TryAddSingleton<ISandboxHealthCheck, SandboxHealthCheck>();
        return services;
    }
}
