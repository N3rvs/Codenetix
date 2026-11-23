using System.Collections.Concurrent;

namespace CodeNetix.Pipeline.Features.CancelRun;

/// <summary>
/// Registro en memoria de las Ejecuciones activas en <c>RunOrchestrator</c> (FR-23, AD-2).
/// Permite cancelar de forma cooperativa una Ejecución en curso, coordina una barrera atómica al entrar
/// en <c>Deciding</c> y serializa por <c>runId</c> el inicio de <c>RunOrchestrator</c> frente a la
/// cancelación directa en BD de <c>RunControl</c> y frente a peticiones concurrentes de cancelación.
/// </summary>
internal sealed class RunCancellationRegistry
{
    private readonly ConcurrentDictionary<Guid, ActiveRunSlot> _slots = new();
    private readonly object _gatesLock = new();
    private readonly Dictionary<Guid, RunGate> _gates = [];

    /// <summary>
    /// Adquiere un cerrojo exclusivo por <paramref name="runId"/> para serializar el arranque del orquestador
    /// y la cancelación directa en BD cuando la Ejecución no está activa en memoria.
    /// </summary>
    public async Task<IDisposable> AcquireRunGateAsync(Guid runId, CancellationToken cancellationToken)
    {
        RunGate gate;
        lock (_gatesLock)
        {
            if (!_gates.TryGetValue(runId, out gate!))
            {
                gate = new RunGate();
                _gates[runId] = gate;
            }

            gate.RefCount++;
        }

        try
        {
            await gate.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new RunGateLease(this, runId, gate);
        }
        catch
        {
            ReleaseGate(runId, gate, releaseSemaphore: false);
            throw;
        }
    }

    /// <summary>Registra una Ejecución activa y enlaza su token de cancelación con el apagado del Host.</summary>
    public ActiveRunHandle BeginRun(Guid runId, CancellationToken hostStoppingToken)
    {
        var slot = new ActiveRunSlot();
        _slots[runId] = slot;
        var linked = CancellationTokenSource.CreateLinkedTokenSource(hostStoppingToken, slot.UserCts.Token);
        return new ActiveRunHandle(this, runId, slot, linked);
    }

    /// <summary>
    /// Solicita la cancelación de una Ejecución activa en memoria.
    /// Devuelve <c>true</c> si se aceptó y disparó la cancelación; <c>false</c> con <paramref name="inDeciding"/>
    /// a <c>true</c> si ya entró en <c>Deciding</c>; o <c>false</c> con <paramref name="inDeciding"/> a <c>false</c>
    /// si la Ejecución no está activa en memoria o ya finalizó su fase cancelable.
    /// </summary>
    public bool TryRequestCancel(Guid runId, out bool inDeciding)
    {
        if (!_slots.TryGetValue(runId, out var slot))
        {
            inDeciding = false;
            return false;
        }

        CancellationTokenSource ctsToCancel;
        lock (slot.Sync)
        {
            if (slot.Disposed || slot.BudgetExhausted)
            {
                inDeciding = false;
                return false;
            }

            if (slot.InDeciding)
            {
                inDeciding = true;
                return false;
            }

            slot.CancelRequested = true;
            ctsToCancel = slot.UserCts;
            inDeciding = false;
        }

        try
        {
            ctsToCancel.Cancel(throwOnFirstException: false);
        }
        catch (ObjectDisposedException)
        {
            // La Ejecución terminó justo al disparar la cancelación: devolver false para recargar BD y responder 409.
            return false;
        }
        catch (AggregateException)
        {
            // Algún callback registrado en el token lanzó una excepción; la cancelación ya fue señalizada.
        }

        return true;
    }

    /// <summary>
    /// Solicita el corte cooperativo de una Ejecución activa en memoria al agotarse su presupuesto (FR-13, AD-15).
    /// Devuelve <c>true</c> si se marcó el corte y se señalizó la cancelación; <c>false</c> si la Ejecución no está
    /// activa o ya entró en <c>Deciding</c>.
    /// </summary>
    public bool TryRequestBudgetExhausted(Guid runId, string detail)
    {
        if (!_slots.TryGetValue(runId, out var slot))
        {
            return false;
        }

        CancellationTokenSource ctsToCancel;
        lock (slot.Sync)
        {
            if (slot.Disposed || slot.InDeciding)
            {
                return false;
            }

            slot.BudgetExhausted = true;
            if (string.IsNullOrWhiteSpace(slot.BudgetExhaustedDetail))
            {
                slot.BudgetExhaustedDetail = detail;
            }

            ctsToCancel = slot.UserCts;
        }

        try
        {
            ctsToCancel.Cancel(throwOnFirstException: false);
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (AggregateException)
        {
            // Algún callback registrado en el token lanzó una excepción; la cancelación ya fue señalizada.
        }

        return true;
    }

    /// <summary>
    /// Barrera atómica antes de <c>Reviewing → Deciding</c>. Devuelve <c>false</c> si ya existe una petición
    /// de cancelación o corte por presupuesto pendiente (el orquestador debe abortar sin entrar en <c>Deciding</c>);
    /// si devuelve <c>true</c>, la Ejecución queda marcada en <c>Deciding</c> y cualquier cancelación posterior
    /// recibirá conflicto.
    /// </summary>
    public bool TryEnterDeciding(Guid runId)
    {
        if (!_slots.TryGetValue(runId, out var slot))
        {
            return true;
        }

        lock (slot.Sync)
        {
            if (slot.CancelRequested || slot.BudgetExhausted)
            {
                return false;
            }

            slot.InDeciding = true;
            return true;
        }
    }

    /// <summary>Indica si la Ejecución activa recibió una petición explícita de cancelación del usuario.</summary>
    public bool IsUserCancelRequested(Guid runId)
    {
        if (!_slots.TryGetValue(runId, out var slot))
        {
            return false;
        }

        lock (slot.Sync)
        {
            return slot.CancelRequested;
        }
    }

    /// <summary>Indica si la Ejecución activa recibió una solicitud de corte por presupuesto agotado.</summary>
    public bool IsBudgetExhausted(Guid runId, out string? detail)
    {
        if (!_slots.TryGetValue(runId, out var slot))
        {
            detail = null;
            return false;
        }

        lock (slot.Sync)
        {
            detail = slot.BudgetExhaustedDetail;
            return slot.BudgetExhausted;
        }
    }

    private void EndRun(Guid runId, ActiveRunSlot slot)
    {
        _slots.TryRemove(new KeyValuePair<Guid, ActiveRunSlot>(runId, slot));
        lock (slot.Sync)
        {
            slot.Disposed = true;
        }
    }

    private void ReleaseGate(Guid runId, RunGate gate, bool releaseSemaphore)
    {
        if (releaseSemaphore)
        {
            gate.Semaphore.Release();
        }

        var shouldDispose = false;
        lock (_gatesLock)
        {
            gate.RefCount--;
            if (gate.RefCount == 0 && _gates.TryGetValue(runId, out var current) && ReferenceEquals(current, gate))
            {
                _gates.Remove(runId);
                shouldDispose = true;
            }
        }

        if (shouldDispose)
        {
            gate.Semaphore.Dispose();
        }
    }

    private sealed class RunGate
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int RefCount;
    }

    private sealed class RunGateLease(RunCancellationRegistry registry, Guid runId, RunGate gate) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                registry.ReleaseGate(runId, gate, releaseSemaphore: true);
            }
        }
    }

    internal sealed class ActiveRunSlot
    {
        public object Sync { get; } = new();

        public CancellationTokenSource UserCts { get; } = new();

        public bool CancelRequested { get; set; }

        public bool BudgetExhausted { get; set; }

        public string? BudgetExhaustedDetail { get; set; }

        public bool InDeciding { get; set; }

        public bool Disposed { get; set; }
    }

    internal sealed class ActiveRunHandle(
        RunCancellationRegistry registry,
        Guid runId,
        ActiveRunSlot slot,
        CancellationTokenSource linkedCts) : IDisposable
    {
        private int _phaseCompleted;
        private int _disposed;

        public CancellationToken Token => linkedCts.Token;

        /// <summary>
        /// Indica si esta Ejecución recibió una petición de cancelación del usuario, incluso tras llamar a
        /// <see cref="CompleteCancellablePhase"/>.
        /// </summary>
        public bool IsUserCancelRequested
        {
            get
            {
                lock (slot.Sync)
                {
                    return slot.CancelRequested;
                }
            }
        }

        /// <summary>
        /// Indica si esta Ejecución agotó su presupuesto, incluso tras llamar a <see cref="CompleteCancellablePhase"/>.
        /// </summary>
        public bool IsBudgetExhausted
        {
            get
            {
                lock (slot.Sync)
                {
                    return slot.BudgetExhausted;
                }
            }
        }

        /// <summary>Detalle del agotamiento de presupuesto si <see cref="IsBudgetExhausted"/> es <c>true</c>.</summary>
        public string? BudgetExhaustedDetail
        {
            get
            {
                lock (slot.Sync)
                {
                    return slot.BudgetExhaustedDetail;
                }
            }
        }

        /// <summary>
        /// Retira el slot del registro en cuanto termina la fase cancelable (antes de limpieza y notificaciones),
        /// evitando responder falsos <c>202 Accepted</c> sobre una Ejecución ya terminada.
        /// </summary>
        public void CompleteCancellablePhase()
        {
            if (Interlocked.Exchange(ref _phaseCompleted, 1) == 0)
            {
                registry.EndRun(runId, slot);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            CompleteCancellablePhase();
            linkedCts.Dispose();
            slot.UserCts.Dispose();
        }
    }
}
