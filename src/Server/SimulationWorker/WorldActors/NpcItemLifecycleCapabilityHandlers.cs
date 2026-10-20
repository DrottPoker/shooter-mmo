using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Items;
using Mmorpg.SimulationWorker.Sessions;
using Mmorpg.WorldData.Actors;

namespace Mmorpg.SimulationWorker.WorldActors;

public sealed class InsuranceWorldActorCapabilityHandler(
    NpcItemLifecycleCapabilityExecutor executor) : IWorldActorCapabilityHandler
{
    public RealtimeWorldActorCapabilityKind Kind =>
        RealtimeWorldActorCapabilityKind.Insurance;

    public Task<WorldActorCapabilityDispatchResult> ExecuteAsync(
        WorldActorCapabilityOperationContext context,
        CancellationToken cancellationToken)
    {
        return executor.ExecuteAsync(context, cancellationToken);
    }
}

public sealed class QuestOfferWorldActorCapabilityHandler(
    NpcItemLifecycleCapabilityExecutor executor) : IWorldActorCapabilityHandler
{
    public RealtimeWorldActorCapabilityKind Kind =>
        RealtimeWorldActorCapabilityKind.QuestOffer;

    public Task<WorldActorCapabilityDispatchResult> ExecuteAsync(
        WorldActorCapabilityOperationContext context,
        CancellationToken cancellationToken)
    {
        return executor.ExecuteAsync(context, cancellationToken);
    }
}

// Opening the vendor confirms it without durable state, so the client can show its offers.
// Purchases and sales commit through GameService with the capability's vendor profile.
public sealed class VendorWorldActorCapabilityHandler(
    NpcItemLifecycleCapabilityExecutor executor) : IWorldActorCapabilityHandler
{
    public RealtimeWorldActorCapabilityKind Kind =>
        RealtimeWorldActorCapabilityKind.Vendor;

    public Task<WorldActorCapabilityDispatchResult> ExecuteAsync(
        WorldActorCapabilityOperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Intent.CapabilityAction == RealtimeCapabilityActionKind.None)
        {
            return Task.FromResult(new WorldActorCapabilityDispatchResult(
                true,
                string.Empty,
                context.Capability.DisplayName + " is open while this interaction stays open."));
        }

        return executor.ExecuteAsync(context, cancellationToken);
    }
}

public sealed class QuestTurnInWorldActorCapabilityHandler : IWorldActorCapabilityHandler
{
    public RealtimeWorldActorCapabilityKind Kind =>
        RealtimeWorldActorCapabilityKind.QuestTurnIn;

    public Task<WorldActorCapabilityDispatchResult> ExecuteAsync(
        WorldActorCapabilityOperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Task.FromResult(new WorldActorCapabilityDispatchResult(
            false,
            "quest_turn_in_unavailable",
            "Quest completion arrives together with quest definitions."));
    }
}

public sealed class NpcItemLifecycleCapabilityExecutor(
    GameServiceClient gameServiceClient,
    CharacterItemStateStore itemStateStore,
    ActiveSimulationSessionStore sessionStore)
{
    private readonly CommittedCarryApplier carryApplier = new(itemStateStore, sessionStore);

    public async Task<WorldActorCapabilityDispatchResult> ExecuteAsync(
        WorldActorCapabilityOperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var session = context.Session;
        var intent = context.Intent;
        if (session.IsSyntheticBot)
        {
            return Rejected(
                "npc_lifecycle_unavailable",
                "Synthetic development sessions cannot mutate durable item lifecycle state.");
        }

        var operationKind = ToOperationKind(intent.CapabilityAction);
        if (operationKind is null)
        {
            return Rejected(
                "npc_lifecycle_action_invalid",
                "Select an insurance, quest, or vendor action before submitting this capability.");
        }

        var insurance = string.Equals(
            context.Capability.Kind,
            WorldActorCapabilityKindIds.Insurance,
            StringComparison.Ordinal);
        var quest = string.Equals(
            context.Capability.Kind,
            WorldActorCapabilityKindIds.QuestOffer,
            StringComparison.Ordinal);
        var vendor = string.Equals(
            context.Capability.Kind,
            WorldActorCapabilityKindIds.Vendor,
            StringComparison.Ordinal);
        if ((!insurance || intent.CapabilityAction is not (
                RealtimeCapabilityActionKind.ApplyInsurance
                or RealtimeCapabilityActionKind.RemoveInsurance))
            && (!quest || intent.CapabilityAction is not (
                RealtimeCapabilityActionKind.AcceptQuest
                or RealtimeCapabilityActionKind.AbandonQuest))
            && (!vendor || intent.CapabilityAction is not (
                RealtimeCapabilityActionKind.VendorPurchase
                or RealtimeCapabilityActionKind.VendorSale)))
        {
            return Rejected(
                "npc_lifecycle_action_invalid",
                "The requested lifecycle action does not match this NPC capability.");
        }

        var request = new SimulationItemOperationRequest(
            intent.OperationId,
            session.AccountId,
            session.CharacterId,
            session.WorkerId,
            session.WorkerRuntimeId,
            session.ShardId,
            session.SimulationSessionToken,
            new SimulationItemAccessRequest(
                false,
                false,
                insurance,
                quest,
                vendor ? context.Capability.ProfileId : null),
            operationKind,
            context.ExpectedCharacterRevision,
            ItemInstanceId: intent.ItemInstanceId == Guid.Empty
                ? null
                : intent.ItemInstanceId,
            ExpectedItemRevision: intent.ItemInstanceId == Guid.Empty
                ? null
                : intent.ExpectedItemRevision,
            DestinationContainerId: intent.DestinationContainerId == Guid.Empty
                ? null
                : intent.DestinationContainerId,
            DestinationSlotIndex: intent.DestinationSlotIndex < 0
                ? null
                : intent.DestinationSlotIndex,
            Quantity: intent.Quantity > 0 ? intent.Quantity : null,
            InteractionSessionId: intent.InteractionSessionId,
            CapabilityId: intent.CapabilityId,
            DefinitionId: string.IsNullOrEmpty(intent.OfferDefinitionId)
                ? null
                : intent.OfferDefinitionId,
            ExpectedPrice: vendor ? intent.ExpectedPrice : null);
        var result = await gameServiceClient.MutateSimulationItemsAsync(
            session.SimulationSessionId,
            request,
            cancellationToken);
        if (!result.Succeeded)
        {
            return new WorldActorCapabilityDispatchResult(
                false,
                result.Error!.Code,
                result.Error.Message,
                0,
                GameServiceOutcome.EndsItemSession(result.Error.Code));
        }

        var itemStateRevision = ApplyCommittedCarry(session, result.Value!, out var applied);
        if (itemStateRevision < 0)
        {
            return new WorldActorCapabilityDispatchResult(
                false,
                "item_state_diverged",
                "Committed NPC lifecycle state diverged from the active simulation state.",
                0,
                true);
        }

        return new WorldActorCapabilityDispatchResult(
            true,
            string.Empty,
            SuccessMessage(intent.CapabilityAction),
            itemStateRevision,
            ItemStateChange: applied
                ? OwnerItemStateChangeMapper.TryCreate(intent.OperationId, session.CharacterId, result.Value!)
                : null);
    }

    private long ApplyCommittedCarry(
        ActiveSimulationSession session,
        SimulationItemTransactionResponse transaction,
        out bool applied)
    {
        applied = false;
        if (!CommittedCarryApplier.TryGetCommittedState(transaction, out var committed))
        {
            return -1;
        }

        var result = carryApplier.Apply(session, committed, out var currentCarry);
        if (CommittedCarryApplier.IsDiverged(result))
        {
            return -1;
        }

        applied = result == ItemStateApplyResult.Applied;
        return currentCarry.ItemStateRevision;
    }

    private static string? ToOperationKind(RealtimeCapabilityActionKind action)
    {
        return action switch
        {
            RealtimeCapabilityActionKind.ApplyInsurance => "apply_item_policy",
            RealtimeCapabilityActionKind.RemoveInsurance => "remove_insurance_policy",
            RealtimeCapabilityActionKind.AcceptQuest => "grant",
            RealtimeCapabilityActionKind.AbandonQuest => "abandon_quest_items",
            RealtimeCapabilityActionKind.VendorPurchase => "vendor_purchase",
            RealtimeCapabilityActionKind.VendorSale => "vendor_sale",
            _ => null
        };
    }

    private static string SuccessMessage(RealtimeCapabilityActionKind action)
    {
        return action switch
        {
            RealtimeCapabilityActionKind.ApplyInsurance =>
                "Insurance applied. The policy covers one protecting death.",
            RealtimeCapabilityActionKind.RemoveInsurance =>
                "Insurance removed. Normal transfer rules are restored.",
            RealtimeCapabilityActionKind.AcceptQuest =>
                "Quest accepted. The required protected item grant is active.",
            RealtimeCapabilityActionKind.AbandonQuest =>
                "Quest abandoned. Only items from this quest grant were removed.",
            RealtimeCapabilityActionKind.VendorPurchase => "Purchased.",
            RealtimeCapabilityActionKind.VendorSale => "Sold.",
            _ => string.Empty
        };
    }

    private static WorldActorCapabilityDispatchResult Rejected(
        string code,
        string message)
    {
        return new WorldActorCapabilityDispatchResult(false, code, message);
    }

}
