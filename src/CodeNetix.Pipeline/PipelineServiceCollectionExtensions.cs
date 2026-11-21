using CodeNetix.Contracts;
using CodeNetix.Pipeline.Data;
using CodeNetix.Pipeline.Events;
using CodeNetix.Pipeline.Features.DispatchEvents;
using CodeNetix.Pipeline.Features.GetRun;
using CodeNetix.Pipeline.Features.RunPipeline;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CodeNetix.Pipeline;

public static class PipelineServiceCollectionExtensions
{
    /// <summary>
    /// Registra Pipeline: su <c>DbContext</c> y su <see cref="IModuleDatabase"/> (<c>run</c>), el sumidero
    /// <see cref="IRunEventSink"/>, <see cref="IRunReader"/>, <see cref="IEventDispatchSignal"/>, el orquestador
    /// y el Dispatcher (<c>BackgroundService</c>). Necesita <see cref="IAcceptedEventSource"/> (Ingestion),
    /// <see cref="IRepositoryWorkspace"/>, <see cref="IIssueReader"/> e <see cref="IIssueNotifier"/> (GitHub), <see cref="ISandboxManager"/> (Sandbox), <see cref="IAnalyst"/> (Agents), <see cref="IInstanceHealth"/> e <see cref="ISecretRedactor"/> (Host).
    /// Los límites de la instancia (<see cref="InstanceLimits"/>) los enlaza Host.
    /// </summary>
    public static IServiceCollection AddCodeNetixPipeline(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddDbContextFactory<PipelineDbContext>((sp, options) =>
            PipelineDbContext.Configure(options, sp.GetRequiredService<IDatabaseConnection>().ConnectionString));

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IRunEventPublisher, NullRunEventPublisher>();
        services.AddSingleton<IModuleDatabase, PipelineModuleDatabase>();
        services.AddSingleton<RunEventLog>();
        services.AddSingleton<IRunEventSink, RunEventSink>();
        services.AddSingleton<IRunReader, RunReader>();
        services.AddSingleton<EventDispatchSignal>();
        services.AddSingleton<IEventDispatchSignal>(sp => sp.GetRequiredService<EventDispatchSignal>());
        services.AddSingleton<IRunDispatchHandler, RunDispatchHandler>();
        services.AddSingleton<RunDispatchHandler>();
        services.AddOptions<InstanceLimits>();
        services.AddSingleton<IRunOrchestrator, RunOrchestrator>();
        services.AddSingleton<StuckRunRecovery>();
        services.TryAddSingleton(new DispatcherOptions());
        services.AddHostedService<Dispatcher>();
        return services;
    }
}
