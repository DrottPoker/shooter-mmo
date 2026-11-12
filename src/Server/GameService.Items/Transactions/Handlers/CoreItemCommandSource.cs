using Mmorpg.GameService.Items.Contracts;

namespace Mmorpg.GameService.Items.Transactions.Handlers;

// The core item commands: grants, moves and container swaps, equipment, stacks, quantity and
// ammunition consumption, destruction, and Bag aggregate swaps.
internal sealed class CoreItemCommandSource : IItemCommandSource
{
    public CoreItemCommandSource()
    {
        var relocation = new ItemRelocationCommandHandler();
        var equipment = new ItemEquipmentCommandHandler();
        var stack = new ItemStackCommandHandler();
        Registrations =
        [
            new ItemCommandRegistration<GrantItemCommand>(
                ItemOperationKinds.Grant,
                static command => command.CharacterId,
                new ItemGrantCommandHandler()),
            new ItemCommandRegistration<RelocateItemCommand>(
                ItemOperationKinds.Relocate,
                static command => command.CharacterId,
                relocation),
            new ItemCommandRegistration<SwapContainerItemsCommand>(
                ItemOperationKinds.SwapContainerItems,
                static command => command.CharacterId,
                relocation),
            new ItemCommandRegistration<EquipItemCommand>(
                ItemOperationKinds.Equip,
                static command => command.CharacterId,
                equipment),
            new ItemCommandRegistration<UnequipItemCommand>(
                ItemOperationKinds.Unequip,
                static command => command.CharacterId,
                equipment),
            new ItemCommandRegistration<SplitItemStackCommand>(
                ItemOperationKinds.SplitStack,
                static command => command.CharacterId,
                stack),
            new ItemCommandRegistration<MergeItemStacksCommand>(
                ItemOperationKinds.MergeStacks,
                static command => command.CharacterId,
                stack),
            new ItemCommandRegistration<ConsumeItemQuantityCommand>(
                ItemOperationKinds.ConsumeQuantity,
                static command => command.CharacterId,
                stack),
            new ItemCommandRegistration<ConsumeEquippedAmmunitionCommand>(
                ItemOperationKinds.ConsumeEquippedAmmunition,
                static command => command.CharacterId,
                new AmmunitionConsumptionCommandHandler()),
            new ItemCommandRegistration<DestroyItemCommand>(
                ItemOperationKinds.Destroy,
                static command => command.CharacterId,
                new ItemDestructionCommandHandler()),
            new ItemCommandRegistration<SwapBagAggregatesCommand>(
                ItemOperationKinds.SwapBagAggregates,
                static command => command.FirstCharacterId,
                new BagAggregateCommandHandler())
        ];
    }

    public IReadOnlyList<ItemCommandRegistration> Registrations { get; }
}
