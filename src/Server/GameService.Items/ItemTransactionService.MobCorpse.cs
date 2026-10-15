using Dapper;
using Mmorpg.GameService.Items.Transactions;
using Mmorpg.GameService.Items.Transactions.Domain;
using Mmorpg.WorldData.Actors;

namespace Mmorpg.GameService.Items;

internal sealed partial class ItemTransactionService
{
    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<CreatePersistentMobCorpseCommand> request,
        CancellationToken cancellationToken)
    {
        return kernel.ExecuteAsync(
            request,
            ItemOperationKinds.CreatePersistentMobCorpse,
            null,
            mobCorpse,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<GrantMobLootCommand> request,
        CancellationToken cancellationToken)
    {
        return kernel.ExecuteAsync(
            request,
            ItemOperationKinds.GrantMobLoot,
            request.Command.CharacterId,
            mobLootGrant,
            cancellationToken);
    }
}
