using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Mmorpg.GameService.Characters.Contracts;
using Mmorpg.GameService.Items.Transactions;
using Mmorpg.GameService.Items.Transactions.Domain;
using Mmorpg.GameService.Placement.Contracts;
using Mmorpg.WorldData.Items;

namespace Mmorpg.GameService.Items;

internal sealed partial class ItemTransactionService
{
    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<ProcessPlayerDeathCommand> request,
        CancellationToken cancellationToken)
    {
        return kernel.ExecuteAsync(
            request,
            ItemOperationKinds.ProcessPlayerDeath,
            request.Command.CharacterId,
            playerDeath,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<ExpireCorpseCommand> request,
        CancellationToken cancellationToken)
    {
        return kernel.ExecuteAsync(
            request,
            ItemOperationKinds.ExpireCorpse,
            null,
            corpseExpiry,
            cancellationToken);
    }

    internal static string DefaultPlayerCorpsePresentationKey => CorpseTransformRules.PlayerCorpsePresentationKey;
}
