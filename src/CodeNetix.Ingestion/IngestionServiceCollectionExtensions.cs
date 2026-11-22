using CodeNetix.Contracts;
using CodeNetix.Ingestion.Data;
using CodeNetix.Ingestion.Features.DispatchEvents;
using CodeNetix.Ingestion.Features.SubmitGitHubActionEvent;
using CodeNetix.Ingestion.Features.SubmitManualEvent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CodeNetix.Ingestion;

public static class IngestionServiceCollectionExtensions
{
    /// <summary>
    /// Registra Ingestion: su <c>DbContext</c> (sobre el <see cref="IDatabaseConnection"/> que aporta
    /// Persistence), su <see cref="IModuleDatabase"/>, <see cref="IManualEventIngestion"/>, <see cref="IGitHubActionEventIngestion"/>, <see cref="IAcceptedEventSource"/>
    /// e <see cref="IEventReader"/>. Necesita un <see cref="IEventDispatchSignal"/> (lo aporta Pipeline).
    /// </summary>
    public static IServiceCollection AddCodeNetixIngestion(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddDbContextFactory<IngestionDbContext>((sp, options) =>
            IngestionDbContext.Configure(options, sp.GetRequiredService<IDatabaseConnection>().ConnectionString));

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IModuleDatabase, IngestionModuleDatabase>();
        services.AddSingleton<IManualEventIngestion, ManualEventIngestion>();
        services.AddSingleton<IGitHubActionEventIngestion, GitHubActionEventIngestion>();
        services.AddSingleton<AcceptedEventSource>();
        services.AddSingleton<IAcceptedEventSource>(sp => sp.GetRequiredService<AcceptedEventSource>());
        services.AddSingleton<IEventReader>(sp => sp.GetRequiredService<AcceptedEventSource>());
        return services;
    }
}
