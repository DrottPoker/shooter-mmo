namespace SimulationWorker.Corpses;

public sealed class CorpseMutationCoordinator
{
    private readonly object sync = new();
    private readonly Dictionary<Guid, MutationGate> gates = [];
    private int waitingMutationCount;

    public int ActiveCorpseCount
    {
        get
        {
            lock (sync)
            {
                return gates.Count;
            }
        }
    }

    public int WaitingMutationCount => Volatile.Read(ref waitingMutationCount);

    public async ValueTask<IDisposable> AcquireAsync(
        Guid corpseId,
        CancellationToken cancellationToken)
    {
        if (corpseId == Guid.Empty)
        {
            throw new ArgumentException("A corpse id is required.", nameof(corpseId));
        }

        MutationGate gate;
        bool queued;
        lock (sync)
        {
            if (!gates.TryGetValue(corpseId, out gate!))
            {
                gate = new MutationGate();
                gates.Add(corpseId, gate);
            }

            queued = gate.ReferenceCount > 0;
            gate.ReferenceCount++;
            if (queued)
            {
                Interlocked.Increment(ref waitingMutationCount);
            }
        }

        try
        {
            await gate.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (queued)
            {
                Interlocked.Decrement(ref waitingMutationCount);
            }

            ReleaseReference(corpseId, gate);
            throw;
        }

        if (queued)
        {
            Interlocked.Decrement(ref waitingMutationCount);
        }

        return new MutationLease(this, corpseId, gate);
    }

    private void Release(Guid corpseId, MutationGate gate)
    {
        gate.Semaphore.Release();
        ReleaseReference(corpseId, gate);
    }

    private void ReleaseReference(Guid corpseId, MutationGate gate)
    {
        lock (sync)
        {
            gate.ReferenceCount--;
            if (gate.ReferenceCount != 0)
            {
                return;
            }

            gates.Remove(corpseId);
            gate.Semaphore.Dispose();
        }
    }

    private sealed class MutationGate
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int ReferenceCount { get; set; }
    }

    private sealed class MutationLease(
        CorpseMutationCoordinator owner,
        Guid corpseId,
        MutationGate gate) : IDisposable
    {
        private CorpseMutationCoordinator? owner = owner;

        public void Dispose()
        {
            Interlocked.Exchange(ref owner, null)?.Release(corpseId, gate);
        }
    }
}
