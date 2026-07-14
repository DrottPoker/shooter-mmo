namespace SimulationWorker.Entities;

public sealed class ConnectionEntityBindingRegistry
{
    private readonly object syncRoot = new();
    private readonly Dictionary<int, ulong> entityIdsByConnectionId = [];
    private readonly Dictionary<ulong, int> connectionIdsByEntityId = [];

    public ConnectionEntityBinding Bind(int connectionId, ulong entityId)
    {
        if (connectionId < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(connectionId));
        }

        if (entityId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(entityId));
        }

        lock (syncRoot)
        {
            if (entityIdsByConnectionId.TryGetValue(connectionId, out var previousEntityId)
                && previousEntityId != entityId)
            {
                connectionIdsByEntityId.Remove(previousEntityId);
            }

            int? replacedConnectionId = null;
            if (connectionIdsByEntityId.TryGetValue(entityId, out var previousConnectionId)
                && previousConnectionId != connectionId)
            {
                entityIdsByConnectionId.Remove(previousConnectionId);
                replacedConnectionId = previousConnectionId;
            }

            entityIdsByConnectionId[connectionId] = entityId;
            connectionIdsByEntityId[entityId] = connectionId;
            return new ConnectionEntityBinding(connectionId, entityId, replacedConnectionId);
        }
    }

    public bool TryGetEntityId(int connectionId, out ulong entityId)
    {
        lock (syncRoot)
        {
            return entityIdsByConnectionId.TryGetValue(connectionId, out entityId);
        }
    }

    public bool TryGetConnectionId(ulong entityId, out int connectionId)
    {
        lock (syncRoot)
        {
            return connectionIdsByEntityId.TryGetValue(entityId, out connectionId);
        }
    }

    public bool UnbindConnection(int connectionId, out ulong entityId)
    {
        lock (syncRoot)
        {
            if (!entityIdsByConnectionId.Remove(connectionId, out entityId))
            {
                return false;
            }

            if (connectionIdsByEntityId.TryGetValue(entityId, out var currentConnectionId)
                && currentConnectionId == connectionId)
            {
                connectionIdsByEntityId.Remove(entityId);
            }

            return true;
        }
    }

    public bool UnbindEntity(ulong entityId, out int connectionId)
    {
        lock (syncRoot)
        {
            if (!connectionIdsByEntityId.Remove(entityId, out connectionId))
            {
                return false;
            }

            if (entityIdsByConnectionId.TryGetValue(connectionId, out var currentEntityId)
                && currentEntityId == entityId)
            {
                entityIdsByConnectionId.Remove(connectionId);
            }

            return true;
        }
    }

    public void Clear()
    {
        lock (syncRoot)
        {
            entityIdsByConnectionId.Clear();
            connectionIdsByEntityId.Clear();
        }
    }
}

public sealed record ConnectionEntityBinding(
    int ConnectionId,
    ulong EntityId,
    int? ReplacedConnectionId);
