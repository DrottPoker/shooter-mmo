using Mmorpg.GameService.Platform.Database;

namespace Mmorpg.GameService.Items.Contracts;

// Shares what a character still lacks of lineage items between the offers of one transaction,
// such as the quest drops of one corpse, so two offers of one item never take more than is
// wanted: an offer takes what is still lacking after the offers that the plan counted before it.
// The wanted total is read again for every offer, before what the character owns.
public sealed class LineageNeedPlan
{
    private readonly Dictionary<(string SourceKind, string SourceId, string DefinitionId), int> planned = [];

    // The part of an offer still lacking after the offers counted before it, which the plan
    // counts.
    public async Task<int> TakeAsync(
        PostgresTransactionScope scope,
        Guid characterId,
        ItemLineage lineage,
        string definitionId,
        int quantity,
        Func<CancellationToken, Task<int>> readWanted,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(readWanted);
        var key = (lineage.SourceKind, lineage.SourceId, definitionId);
        var before = planned.GetValueOrDefault(key);
        var needed = Math.Max(
            0,
            await LineageItems.LackingAsync(
                scope,
                characterId,
                lineage,
                definitionId,
                await readWanted(cancellationToken),
                quantity + before,
                cancellationToken) - before);
        planned[key] = before + needed;
        return needed;
    }

    // Whether all of an offer is still lacking after the offers counted before it; only then does
    // the plan count it.
    internal async Task<bool> TakeAllAsync(
        PostgresTransactionScope scope,
        Guid characterId,
        ItemLineage lineage,
        string definitionId,
        int quantity,
        Func<CancellationToken, Task<int>> readWanted,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(readWanted);
        var key = (lineage.SourceKind, lineage.SourceId, definitionId);
        var before = planned.GetValueOrDefault(key);
        var needed = await LineageItems.LackingAsync(
            scope,
            characterId,
            lineage,
            definitionId,
            await readWanted(cancellationToken),
            quantity + before,
            cancellationToken) - before;
        if (needed < quantity)
        {
            return false;
        }

        planned[key] = before + quantity;
        return true;
    }
}
