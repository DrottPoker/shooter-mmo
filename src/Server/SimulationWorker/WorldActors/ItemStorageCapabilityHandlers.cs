using Mmorpg.GameProtocol;

namespace Mmorpg.SimulationWorker.WorldActors;

// Opening the Character Bank or Recovery Storage of a world actor changes no durable state.
// Item operations gain the service while the interaction stays open, see
// ItemInteractionAccessService. The result confirms the capability so the client presents
// the storage.
public sealed class ItemStorageWorldActorCapabilityHandler : IWorldActorCapabilityHandler
{
    public ItemStorageWorldActorCapabilityHandler(RealtimeWorldActorCapabilityKind kind)
    {
        if (kind is not (RealtimeWorldActorCapabilityKind.Bank
            or RealtimeWorldActorCapabilityKind.RecoveryStorage))
        {
            throw new ArgumentOutOfRangeException(
                nameof(kind),
                kind,
                "Only Bank and Recovery Storage are item storage capabilities.");
        }

        Kind = kind;
    }

    public RealtimeWorldActorCapabilityKind Kind { get; }

    public Task<WorldActorCapabilityDispatchResult> ExecuteAsync(
        WorldActorCapabilityOperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Intent.LifecycleAction != RealtimeNpcLifecycleActionKind.None)
        {
            return Task.FromResult(new WorldActorCapabilityDispatchResult(
                false,
                "npc_lifecycle_action_invalid",
                "Item storage capabilities take no lifecycle action."));
        }

        var name = Kind == RealtimeWorldActorCapabilityKind.Bank
            ? "Character Bank"
            : "Recovery Storage";
        return Task.FromResult(new WorldActorCapabilityDispatchResult(
            true,
            string.Empty,
            name + " is open while this interaction stays open."));
    }
}
