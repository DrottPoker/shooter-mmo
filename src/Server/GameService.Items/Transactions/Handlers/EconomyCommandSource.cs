using Mmorpg.GameService.Characters.Contracts;
using Mmorpg.GameService.Items.Contracts;

namespace Mmorpg.GameService.Items.Transactions.Handlers;

// The economy commands: vendor purchases and sales, and gathering yields, which reach the quest
// code only through IQuestGatherCredit.
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
                new GatherResourceCommandHandler(questGatherCredit))
        ];
    }

    public IReadOnlyList<ItemCommandRegistration> Registrations { get; }
}
