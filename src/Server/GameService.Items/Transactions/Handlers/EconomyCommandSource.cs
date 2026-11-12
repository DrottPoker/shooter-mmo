using Mmorpg.GameService.Characters.Contracts;
using Mmorpg.GameService.Quests.Contracts;

namespace Mmorpg.GameService.Items.Transactions.Handlers;

// The economy commands: vendor purchases and sales, and gathering yields.
internal sealed class EconomyCommandSource : IItemCommandSource
{
    public EconomyCommandSource(
        ICharacterWallet characterWallet,
        IQuestDefinitions questDefinitions,
        ICharacterQuestLog questLog)
    {
        var vendor = new VendorCommandHandler(characterWallet);
        Registrations =
        [
            new ItemCommandRegistration<PurchaseFromVendorCommand>(
                ItemOperationKinds.VendorPurchase,
                static command => command.CharacterId,
                vendor),
            new ItemCommandRegistration<SellToVendorCommand>(
                ItemOperationKinds.VendorSale,
                static command => command.CharacterId,
                vendor),
            new ItemCommandRegistration<GatherResourceCommand>(
                ItemOperationKinds.GatherResource,
                static command => command.CharacterId,
                new GatherResourceCommandHandler(questDefinitions, questLog))
        ];
    }

    public IReadOnlyList<ItemCommandRegistration> Registrations { get; }
}
