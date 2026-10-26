using Dapper;

namespace Mmorpg.GameService.Items.Transactions.Domain;

// Mob loot rules. Ids derived from Mob corpse and grant ids use ScopedGuid.
internal static class MobLootRules
{
    public static void ValidateMobLootDefinition(
        ResolvedItemDefinition definition,
        int quantity)
    {
        if (quantity <= 0
            || quantity > definition.RuntimeDefinition.MaximumStackSize
            || definition.IsBag
            || definition.RuntimeDefinition.DefaultPolicies.Length > 0)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.MobLootGrantInvalid,
                "Mob loot must be transferable, policy-free, non-Bag item content within its stack limit.");
        }
    }

    // Records a live Mob loot grant, so no other operation commits it again. A grant that is
    // already recorded left the corpse before.
    public static async Task RecordGrantAsync(
        ItemTransactionContext context,
        Guid grantId,
        Guid characterId,
        Guid sourceCorpseId,
        CancellationToken cancellationToken)
    {
        if (!await TryRecordGrantAsync(context, grantId, characterId, sourceCorpseId, cancellationToken))
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemAlreadyLooted,
                "The live Mob loot was already granted.");
        }
    }

    public static async Task<bool> TryRecordGrantAsync(
        ItemTransactionContext context,
        Guid grantId,
        Guid characterId,
        Guid sourceCorpseId,
        CancellationToken cancellationToken)
    {
        var recorded = await context.Connection.QuerySingleOrDefaultAsync<int?>(new CommandDefinition(
            """
            insert into live_mob_loot_grants (grant_id, operation_id, character_id, source_corpse_id)
            values (@GrantId, @OperationId, @CharacterId, @SourceCorpseId)
            on conflict (grant_id) do nothing
            returning 1;
            """,
            new
            {
                GrantId = grantId,
                context.OperationId,
                CharacterId = characterId,
                SourceCorpseId = sourceCorpseId
            },
            context.Transaction,
            cancellationToken: cancellationToken));
        return recorded is not null;
    }

    // A grant into an equipment slot names no container: no id, revision 0, and slot -1.
    public static bool NamesEquipmentDestinationOnly(
        Guid destinationContainerId,
        long expectedDestinationContainerRevision,
        int destinationSlotIndex)
    {
        return destinationContainerId == Guid.Empty
            && expectedDestinationContainerRevision == 0
            && destinationSlotIndex == -1;
    }
}
