using Dapper;

namespace Mmorpg.GameService.Items.Transactions.Domain;

// The corpse's equipped Bag owns the corpse Bag section: the section is the Bag's own
// contents container. A Bag that leaves the corpse takes that container along, so the
// corpse gets a new empty Bag section in its place. Every other corpse Bag is empty.
internal static class CorpseBagCustody
{
    public static Task<bool> OwnsSectionAsync(
        ItemTransactionContext context,
        Guid corpseId,
        LockedItem bag,
        CancellationToken cancellationToken)
    {
        return bag.IsBag && bag.BagContentsContainerId is not null
            ? CorpseCustody.OwnsCorpseBagSectionAsync(context, corpseId, bag.ItemInstanceId, cancellationToken)
            : Task.FromResult(false);
    }

    // Takes the Bag's contents container out of the corpse after the Bag left it.
    public static async Task DetachSectionAsync(
        ItemTransactionContext context,
        Guid corpseId,
        LockedItem bag,
        CancellationToken cancellationToken)
    {
        var sectionContainerId = Guid.NewGuid();
        var replaced = await context.Connection.ExecuteAsync(new CommandDefinition(
            """
            insert into item_containers (id, container_type, slot_capacity, lifecycle)
            values (@SectionContainerId, 'corpse_bag_contents', 0, 'active');

            update corpse_sections
            set container_id = @SectionContainerId
            where corpse_id = @CorpseId
              and section_kind = 'bag'
              and container_id = @BagContentsContainerId;
            """,
            new
            {
                SectionContainerId = sectionContainerId,
                CorpseId = corpseId,
                BagContentsContainerId = bag.BagContentsContainerId!.Value
            },
            context.Transaction,
            cancellationToken: cancellationToken));
        if (replaced != 2)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.BagStateChanged,
                "The corpse Bag section changed before the Bag left the corpse.");
        }

        context.Journal.TouchContainer(bag.BagContentsContainerId);
    }
}
