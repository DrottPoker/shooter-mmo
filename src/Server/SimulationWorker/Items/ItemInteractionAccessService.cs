using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.Sessions;
using Mmorpg.SimulationWorker.WorldActors;
using Mmorpg.WorldData.Actors;

namespace Mmorpg.SimulationWorker.Items;

// Decides which item services an operation may use. Bank and Recovery Storage require an
// open interaction with a world actor that offers the service as an available capability,
// so walking away or losing line of sight closes the interaction and ends the access. The
// Development option grants both everywhere and never grants insurance.
public sealed class ItemInteractionAccessService
{
    private readonly DevelopmentItemInteractionOptions developmentOptions;
    private readonly WorldInteractionLeaseRegistry? leases;
    private readonly WorldActorStore? actors;
    private readonly IWorldActorCapabilityAvailabilityPolicy availabilityPolicy;

    public ItemInteractionAccessService(
        DevelopmentItemInteractionOptions developmentOptions,
        WorldInteractionLeaseRegistry? leases,
        WorldActorStore? actors,
        IWorldActorCapabilityAvailabilityPolicy availabilityPolicy)
    {
        this.developmentOptions = developmentOptions
            ?? throw new ArgumentNullException(nameof(developmentOptions));
        this.leases = leases;
        this.actors = actors;
        this.availabilityPolicy = availabilityPolicy
            ?? throw new ArgumentNullException(nameof(availabilityPolicy));
    }

    public ItemInteractionAccess Evaluate(int peerId, ActiveSimulationSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var bank = developmentOptions.GlobalBankAndRecoveryAccess;
        var recoveryStorage = developmentOptions.GlobalBankAndRecoveryAccess;
        if (leases is not null
            && actors is not null
            && leases.TryGet(peerId, out var lease)
            && lease!.TargetKind == RealtimeWorldInteractionTargetKind.WorldActor
            && actors.TryGetByRuntimeId(lease.TargetRuntimeId, out var actor))
        {
            foreach (var capability in actor!.Definition.Capabilities)
            {
                if (!availabilityPolicy.IsAvailable(session, actor, capability))
                {
                    continue;
                }

                bank |= string.Equals(
                    capability.Kind,
                    WorldActorCapabilityKindIds.Bank,
                    StringComparison.Ordinal);
                recoveryStorage |= string.Equals(
                    capability.Kind,
                    WorldActorCapabilityKindIds.RecoveryStorage,
                    StringComparison.Ordinal);
            }
        }

        return new ItemInteractionAccess(bank, recoveryStorage);
    }
}

public sealed record ItemInteractionAccess(bool Bank, bool RecoveryStorage);
