using CodeNetix.Contracts;

namespace CodeNetix.Pipeline.Features.DispatchEvents;

/// <summary>Despertador del <see cref="Dispatcher"/>: varios avisos seguidos equivalen a uno.</summary>
internal sealed class EventDispatchSignal : IEventDispatchSignal, IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(0, 1);

    public void Notify()
    {
        try
        {
            _semaphore.Release();
        }
        catch (SemaphoreFullException)
        {
            // Ya había un aviso pendiente.
        }
        catch (ObjectDisposedException)
        {
            // El host se está deteniendo.
        }
    }

    /// <summary>Espera un aviso o hasta <paramref name="timeout"/>.</summary>
    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        _semaphore.WaitAsync(timeout, cancellationToken);

    public void Dispose() => _semaphore.Dispose();
}
