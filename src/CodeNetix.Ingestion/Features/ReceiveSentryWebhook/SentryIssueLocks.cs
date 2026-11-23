namespace CodeNetix.Ingestion.Features.ReceiveSentryWebhook;

/// <summary>
/// Cerrojo por incidencia de Sentry, compartido por el procesador del webhook (7.3) y <c>fix &lt;url-de-sentry&gt;</c> (7.4):
/// "buscar la issue de seguimiento o asegurarla y registrar el Evento" ocurre de uno en uno para cada <c>sentryIssueId</c>,
/// así ninguna combinación de entregas y <c>fix</c> crea dos issues de seguimiento. Incidencias distintas no se esperan
/// entre sí. Las entradas se liberan cuando nadie las usa.
/// </summary>
internal sealed class SentryIssueLocks
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public async Task<IDisposable> AcquireAsync(string sentryIssueId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sentryIssueId);

        Entry entry;
        lock (_entries)
        {
            if (!_entries.TryGetValue(sentryIssueId, out entry!))
            {
                entry = new Entry();
                _entries[sentryIssueId] = entry;
            }

            entry.References++;
        }

        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Release(sentryIssueId, entry, held: false);
            throw;
        }

        return new Lease(this, sentryIssueId, entry);
    }

    /// <summary>Incidencias con alguien dentro o esperando (para pruebas).</summary>
    internal int ActiveKeys
    {
        get
        {
            lock (_entries)
            {
                return _entries.Count;
            }
        }
    }

    private void Release(string sentryIssueId, Entry entry, bool held)
    {
        if (held)
        {
            entry.Semaphore.Release();
        }

        lock (_entries)
        {
            if (--entry.References == 0)
            {
                _entries.Remove(sentryIssueId);
                entry.Semaphore.Dispose();
            }
        }
    }

    private sealed class Entry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int References { get; set; }
    }

    private sealed class Lease(SentryIssueLocks owner, string sentryIssueId, Entry entry) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Release(sentryIssueId, entry, held: true);
            }
        }
    }
}
