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
    CarryStateStore carryStateStore,
    ActiveSimulationSessionStore sessionStore)
{
    private readonly CommittedCarryApplier carryApplier = new(carryStateStore, sessionStore);

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

        var operationKind = ToOperationKind(intent.LifecycleAction);
        if (operationKind is null)
        {
            return Rejected(
                "npc_lifecycle_action_invalid",
                "Select an insurance or quest lifecycle action before submitting this capability.");
        }

        var insurance = string.Equals(
            context.Capability.Kind,
            WorldActorCapabilityKindIds.Insurance,
            StringComparison.Ordinal);
        var quest = string.Equals(
            context.Capability.Kind,
            WorldActorCapabilityKindIds.QuestOffer,
            StringComparison.Ordinal);
        if ((!insurance || intent.LifecycleAction is not (
                RealtimeNpcLifecycleActionKind.ApplyInsurance
                or RealtimeNpcLifecycleActionKind.RemoveInsurance))
            && (!quest || intent.LifecycleAction is not (
                RealtimeNpcLifecycleActionKind.AcceptQuest
                or RealtimeNpcLifecycleActionKind.AbandonQuest)))
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
                quest),
            operationKind,
            intent.ExpectedCharacterRevision,
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
            InteractionSessionId: intent.InteractionSessionId,
            CapabilityId: intent.CapabilityId);
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

        var itemStateRevision = ApplyCommittedCarry(session, result.Value!);
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
            SuccessMessage(intent.LifecycleAction),
            itemStateRevision);
    }

    private long ApplyCommittedCarry(
        ActiveSimulationSession session,
        SimulationItemTransactionResponse transaction)
    {
        if (transaction.CharacterRevisions.Count == 0)
        {
            return carryStateStore.TryGet(
                session.CharacterId,
                session.SimulationSessionId,
                out var current)
                    ? current!.ItemStateRevision
                    : -1;
        }

        if (!CommittedCarryApplier.TryGetCommittedCarry(transaction, out var committed)
            || CommittedCarryApplier.IsDiverged(
                carryApplier.Apply(session, committed, out var currentCarry)))
        {
            return -1;
        }

        return currentCarry.ItemStateRevision;
    }

    private static string? ToOperationKind(RealtimeNpcLifecycleActionKind action)
    {
        return action switch
        {
            RealtimeNpcLifecycleActionKind.ApplyInsurance => "apply_item_policy",
            RealtimeNpcLifecycleActionKind.RemoveInsurance => "remove_insurance_policy",
            RealtimeNpcLifecycleActionKind.AcceptQuest => "grant",
            RealtimeNpcLifecycleActionKind.AbandonQuest => "abandon_quest_items",
            _ => null
        };
    }

    private static string SuccessMessage(RealtimeNpcLifecycleActionKind action)
    {
        return action switch
        {
            RealtimeNpcLifecycleActionKind.ApplyInsurance =>
                "Insurance applied. The policy covers one protecting death.",
            RealtimeNpcLifecycleActionKind.RemoveInsurance =>
                "Insurance removed. Normal transfer rules are restored.",
            RealtimeNpcLifecycleActionKind.AcceptQuest =>
                "Quest accepted. The required protected item grant is active.",
            RealtimeNpcLifecycleActionKind.AbandonQuest =>
                "Quest abandoned. Only items from this quest grant were removed.",
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
