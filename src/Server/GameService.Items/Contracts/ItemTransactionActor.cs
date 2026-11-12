using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Mmorpg.GameService.Items.Contracts;

public enum ItemTransactionAuthority
{
    Account = 1,
    System = 2,
    SimulationWorker = 3
}

public sealed record ItemTransactionActor(
    ItemTransactionAuthority Authority,
    Guid? AccountId,
    bool RequiresOfflineCharacter = false,
    SimulationItemTransactionAuthority? Simulation = null)
{
    public static ItemTransactionActor ForAccount(Guid accountId)
    {
        return new ItemTransactionActor(ItemTransactionAuthority.Account, accountId);
    }

    public static ItemTransactionActor ForOfflineAccount(Guid accountId)
    {
        return new ItemTransactionActor(
            ItemTransactionAuthority.Account,
            accountId,
            RequiresOfflineCharacter: true);
    }

    public static ItemTransactionActor ForSystem()
    {
        return new ItemTransactionActor(ItemTransactionAuthority.System, null);
    }

    public static ItemTransactionActor ForSimulationWorker(
        Guid accountId,
        Guid characterId,
        Guid simulationSessionId,
        string workerId,
        string workerRuntimeId,
        string shardId,
        string sessionTokenHash,
        ItemTransactionLiveAccess liveAccess)
    {
        return new ItemTransactionActor(
            ItemTransactionAuthority.SimulationWorker,
            accountId,
            Simulation: new SimulationItemTransactionAuthority(
                simulationSessionId,
                characterId,
                workerId,
                workerRuntimeId,
                shardId,
                sessionTokenHash,
                liveAccess));
    }
}

public sealed record SimulationItemTransactionAuthority(
    Guid SimulationSessionId,
    Guid CharacterId,
    string WorkerId,
    string WorkerRuntimeId,
    string ShardId,
    [property: JsonIgnore] string SessionTokenHash,
    [property: JsonIgnore] ItemTransactionLiveAccess LiveAccess);

// What the worker validated for one live operation: the city services as flags, and the
// capability claims of the commands above the kernel, each a capability kind with the id the
// worker validated for it, such as the vendor profile of an NPC's vendor capability. The worker
// sets them from what it validated, never from a client value.
public sealed record ItemTransactionLiveAccess(
    bool Bank,
    bool RecoveryStorage,
    bool InsuranceNpc)
{
    public static ItemTransactionLiveAccess None { get; } = new(false, false, false);

    public ImmutableDictionary<string, string> Capabilities { get; init; } =
        ImmutableDictionary.Create<string, string>(StringComparer.Ordinal);

    // The same access with a claim of the kind, which replaces an earlier claim of that kind.
    public ItemTransactionLiveAccess WithCapability(string kind, string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(id);
        return this with { Capabilities = Capabilities.SetItem(kind, id) };
    }
}
