using Mmorpg.GameService.Characters.Contracts;
using Mmorpg.GameService.Items.Contracts;
using Mmorpg.GameService.Quests.Contracts;

namespace Mmorpg.GameService.Economy.Transactions.Handlers;

// The economy commands: vendor purchases and sales, and gathering yields, which count for quests
// through Quests' IQuestGatherCredit.
internal sealed class EconomyCommandSource : IItemCommandSource
{
    public EconomyCommandSource(
        ICharacterWallet characterWallet,
        IQuestGatherCredit questGatherCredit)
    {
        var vendor = new VendorCommandHandler(characterWallet);
        Registrations =
        [
            new ItemCommandRegistration<PurchaseFromVendorCommand>(
                EconomyOperationKinds.VendorPurchase,
                static command => command.CharacterId,
                vendor),
            new ItemCommandRegistration<SellToVendorCommand>(
                EconomyOperationKinds.VendorSale,
                static command => command.CharacterId,
                vendor),
            new ItemCommandRegistration<GatherResourceCommand>(
                EconomyOperationKinds.GatherResource,
                static command => command.CharacterId,
                new GatherResourceCommandHandler(questGatherCredit))
        ];
    }

    public IReadOnlyList<ItemCommandRegistration> Registrations { get; }
}
