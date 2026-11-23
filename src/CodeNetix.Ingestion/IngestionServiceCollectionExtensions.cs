using CodeNetix.Contracts;
using CodeNetix.Ingestion.Data;
using CodeNetix.Ingestion.Features.DispatchEvents;
using CodeNetix.Ingestion.Features.PollGitHub;
using CodeNetix.Ingestion.Features.ReceiveGitHubWebhook;
using CodeNetix.Ingestion.Features.ReceiveSentryWebhook;
using CodeNetix.Ingestion.Features.RecordLabelEvent;
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
        services.TryAddSingleton<LabelEventRecorder>();
        services.AddSingleton<AcceptedEventSource>();
        services.AddSingleton<IAcceptedEventSource>(sp => sp.GetRequiredService<AcceptedEventSource>());
        services.AddSingleton<IEventReader>(sp => sp.GetRequiredService<AcceptedEventSource>());
        services.AddSingleton<IIgnoredEventReader>(sp => sp.GetRequiredService<AcceptedEventSource>());
        return services;
    }

    /// <summary>
    /// Registra el webhook de Sentry (FR-7, FR-8): <see cref="SentryWebhookOptions"/> (las rellena Host),
    /// <see cref="ISentryWebhookIngestion"/> (firma, filtro, correspondencia y cola) y el <c>BackgroundService</c> que procesa
    /// la cola. Solo para <c>serve</c>; necesita <see cref="ISentryTrackingIssues"/> (módulo GitHub) e
    /// <see cref="IInstanceHealth"/> (Host).
    /// </summary>
    public static IServiceCollection AddCodeNetixSentryWebhooks(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<SentryWebhookOptions>();
        services.TryAddSingleton<SentryEventRecorder>();
        services.TryAddSingleton<SentryWebhookIngestion>();
        services.TryAddSingleton<ISentryWebhookIngestion>(sp => sp.GetRequiredService<SentryWebhookIngestion>());
        services.TryAddSingleton<SentryWebhookProcessor>();
        services.AddHostedService(sp => sp.GetRequiredService<SentryWebhookProcessor>());
        return services;
    }

    /// <summary>
    /// Registra el sondeo de issues etiquetadas (FR-6): <see cref="GitHubPollingOptions"/> (las rellena Host) y su
    /// <c>BackgroundService</c>. Solo para <c>serve</c>; necesita <see cref="ILabeledIssueSource"/> (módulo GitHub) e
    /// <see cref="IInstanceHealth"/> (Host), además de lo que registra <see cref="AddCodeNetixIngestion"/>.
    /// </summary>
    public static IServiceCollection AddCodeNetixGitHubPolling(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<GitHubPollingOptions>();
        services.TryAddSingleton<GitHubPoller>();
        services.TryAddSingleton<IGitHubPollCycle>(sp => sp.GetRequiredService<GitHubPoller>());
        services.AddHostedService<GitHubPollingService>();
        return services;
    }

    /// <summary>
    /// Registra el webhook de GitHub (FR-7): <see cref="GitHubWebhookOptions"/> (las rellena Host),
    /// <see cref="IGitHubWebhookIngestion"/> (firma, filtro y cola) y el <c>BackgroundService</c> que procesa la cola. Solo
    /// para <c>serve</c>; necesita <see cref="ILabeledIssueSource"/> (módulo GitHub) e <see cref="IInstanceHealth"/> (Host).
    /// </summary>
    public static IServiceCollection AddCodeNetixGitHubWebhooks(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<GitHubWebhookOptions>();
        services.TryAddSingleton<GitHubWebhookIngestion>();
        services.TryAddSingleton<IGitHubWebhookIngestion>(sp => sp.GetRequiredService<GitHubWebhookIngestion>());
        services.TryAddSingleton<GitHubWebhookProcessor>();
        services.AddHostedService(sp => sp.GetRequiredService<GitHubWebhookProcessor>());
        return services;
    }
}
