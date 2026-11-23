using CodeNetix.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeNetix.Ingestion.Features.PollGitHub;

/// <summary>
/// Sondeo periódico de GitHub (FR-6, AD-18 paso 5), solo en <c>serve</c>. Sin repositorios configurados no hace nada.
/// Con ellos, espera a que la instancia esté <c>Ready</c> (en <c>Diagnostic</c> nunca sondea), lanza un ciclo inmediato
/// y después uno cada <see cref="GitHubPollingOptions.Interval"/>, de uno en uno; tras un rate limit espera lo que pida
/// GitHub, entre el intervalo y <see cref="GitHubPollingOptions.MaxBackoff"/>. Una excepción de un ciclo se registra y el
/// servicio sigue.
/// </summary>
internal sealed partial class GitHubPollingService(
    IOptions<GitHubPollingOptions> options,
    IInstanceHealth health,
    IGitHubPollCycle poller,
    TimeProvider timeProvider,
    ILogger<GitHubPollingService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            LogDisabled();
            return;
        }

        try
        {
            await health.WhenReadyAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var interval = settings.Interval > TimeSpan.Zero ? settings.Interval : GitHubPollingOptions.DefaultInterval;
        LogStarted(settings.Repositories.Count, settings.Label, interval);
        while (!stoppingToken.IsCancellationRequested)
        {
            var wait = interval;
            try
            {
                var result = await poller.RunCycleAsync(stoppingToken).ConfigureAwait(false);
                if (result.RateLimited)
                {
                    wait = Backoff(result.RetryAfter, interval);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogCycleFailed(ex);
            }

            try
            {
                await Task.Delay(wait, timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Espera tras un rate limit: lo que pida GitHub, como poco el intervalo y como mucho <see cref="GitHubPollingOptions.MaxBackoff"/>.</summary>
    internal static TimeSpan Backoff(TimeSpan? retryAfter, TimeSpan interval)
    {
        var wanted = retryAfter ?? interval;
        var max = interval > GitHubPollingOptions.MaxBackoff ? interval : GitHubPollingOptions.MaxBackoff;
        return wanted < interval ? interval : wanted > max ? max : wanted;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Sondeo de GitHub desactivado: no hay repositorios en CODENETIX__Polling__Repositories.")]
    private partial void LogDisabled();

    [LoggerMessage(Level = LogLevel.Information, Message = "Sondeo de GitHub en marcha: {Count} repositorio(s), etiqueta '{Label}', cada {Interval}.")]
    private partial void LogStarted(int count, string label, TimeSpan interval);

    [LoggerMessage(Level = LogLevel.Error, Message = "Fallo en un ciclo del sondeo de GitHub; se reintenta en el siguiente.")]
    private partial void LogCycleFailed(Exception exception);
}
