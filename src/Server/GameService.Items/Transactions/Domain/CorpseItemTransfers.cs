using Mmorpg.WorldData.Items;

namespace Mmorpg.GameService.Items.Transactions.Domain;

// Rules for moving one item stack into, out of, or within a corpse.
internal static class CorpseItemTransfers
{
    public static CorpseStackDisposition GetCorpseStackDisposition(
        LockedItem source,
        IReadOnlyList<LockedItemPolicy> sourcePolicies,
        ItemPolicyCapabilities sourceCapabilities,
        LockedItem target,
        IReadOnlyList<LockedItemPolicy> targetPolicies,
        ResolvedItemDefinition definition,
        int movedQuantity)
    {
        var targetCapabilities = ItemPolicyRules.Evaluate(
            definition.RuntimeDefinition,
            ItemPolicyStates.ToPolicyStates(targetPolicies));
        var sourceState = new ItemStackState(
            source.DefinitionId,
            movedQuantity,
            ItemPolicyStates.CalculatePolicyFingerprint(
                sourcePolicies.Where(policy => string.Equals(
                    policy.Status,
                    ItemPolicyRules.ActiveStatus,
                    StringComparison.Ordinal))),
            ItemPolicyStates.EmptyStackStateFingerprint);
        var targetState = new ItemStackState(
            target.DefinitionId,
            target.Quantity,
            ItemPolicyStates.CalculatePolicyFingerprint(
                targetPolicies.Where(policy => string.Equals(
                    policy.Status,
                    ItemPolicyRules.ActiveStatus,
                    StringComparison.Ordinal))),
            ItemPolicyStates.EmptyStackStateFingerprint);
        if (sourceCapabilities.CanStack
            && targetCapabilities.CanStack
            && ItemStackRules.CanMerge(definition.RuntimeDefinition, sourceState, targetState))
        {
            return CorpseStackDisposition.Merge;
        }

        return ItemStackRules.AreCompatible(sourceState, targetState)
            ? CorpseStackDisposition.LimitExceeded
            : CorpseStackDisposition.Incompatible;
    }

    public static void RejectCorpseStackMerge(CorpseStackDisposition disposition)
    {
        ItemTransactionContext.Reject(
            disposition == CorpseStackDisposition.LimitExceeded
                ? ItemTransactionErrorCodes.ItemStackLimitExceeded
                : ItemTransactionErrorCodes.ItemStackIncompatible,
            disposition == CorpseStackDisposition.LimitExceeded
                ? "The transferred quantity would exceed the target stack limit."
                : "The transferred item and target stack have incompatible state.");
    }

    public static void EnsureCarriedLootDestination(LockedContainer destination)
    {
        if (!string.Equals(
                destination.ContainerType,
                "permanent_inventory",
                StringComparison.Ordinal)
            && !string.Equals(
                destination.ContainerType,
                "bag_contents",
                StringComparison.Ordinal))
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemSlotIncompatible,
                "Corpse loot must enter carried character storage.");
        }
    }

    public static void EnsureCarriedCorpseTransferSource(LockedContainer source)
    {
        if (!string.Equals(source.ContainerType, "permanent_inventory", StringComparison.Ordinal)
            && !string.Equals(source.ContainerType, "bag_contents", StringComparison.Ordinal))
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemSlotIncompatible,
                "Only carried character storage can deposit items into a corpse.");
        }
    }

    public static void ValidateCorpseItemTransferCommand(CorpseItemTransferCommand command)
    {
        var hasTarget = command.TargetItemInstanceId is not null;
        if (command.CharacterId == Guid.Empty
            || command.CorpseId == Guid.Empty
            || command.ItemInstanceId == Guid.Empty
            || command.ExpectedItemRevision < 0
            || command.DestinationContainerId == Guid.Empty
            || command.ExpectedDestinationContainerRevision < 0
            || command.DestinationSlotIndex < 0
            || hasTarget != (command.ExpectedTargetItemRevision is not null)
            || command.TargetItemInstanceId == Guid.Empty
            || command.TargetItemInstanceId == command.ItemInstanceId
            || command.ExpectedTargetItemRevision < 0)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.SimulationOperationInvalid,
                "The corpse loot command is incomplete or invalid.");
        }
    }
}

internal sealed record CorpseItemTransferCommand(
    Guid CharacterId,
    Guid CorpseId,
    Guid ItemInstanceId,
    long ExpectedItemRevision,
    int? Quantity,
    Guid DestinationContainerId,
    long ExpectedDestinationContainerRevision,
    int DestinationSlotIndex,
    Guid? TargetItemInstanceId,
    long? ExpectedTargetItemRevision);

internal enum CorpseStackDisposition
{
    Merge,
    LimitExceeded,
    Incompatible
}
