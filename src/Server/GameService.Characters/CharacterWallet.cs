using Dapper;
using Mmorpg.GameService.Characters.Contracts;
using Mmorpg.GameService.Platform.Database;

namespace Mmorpg.GameService.Characters;

internal sealed class CharacterWallet : ICharacterWallet
{
    public async Task<CurrencyDebit> DebitAsync(
        PostgresTransactionScope scope,
        Guid characterId,
        long amount,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var currencyBefore = await scope.Connection.QuerySingleAsync<long>(
            new CommandDefinition(
                "select currency from characters where id = @CharacterId for no key update;",
                new { CharacterId = characterId },
                scope.Transaction,
                cancellationToken: cancellationToken));
        if (currencyBefore < amount)
        {
            return new CurrencyDebit(false, currencyBefore, currencyBefore);
        }

        var currencyAfter = currencyBefore - amount;
        await scope.Connection.ExecuteAsync(new CommandDefinition(
            "update characters set currency = @Currency where id = @CharacterId;",
            new { CharacterId = characterId, Currency = currencyAfter },
            scope.Transaction,
            cancellationToken: cancellationToken));
        return new CurrencyDebit(true, currencyBefore, currencyAfter);
    }
}
