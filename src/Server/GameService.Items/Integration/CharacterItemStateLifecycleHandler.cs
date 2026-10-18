using Dapper;
using Mmorpg.GameService.Characters.Contracts;
using Mmorpg.GameService.Platform.Database;

namespace Mmorpg.GameService.Items.Integration;

// Every new character gets its complete empty item state and the catalog's starting
// currency balance in the creation transaction.
internal sealed class CharacterItemStateLifecycleHandler(
    CharacterItemStateBootstrapper bootstrapper,
    ICharacterWallet characterWallet) : ICharacterLifecycleHandler
{
    public async Task OnCharacterCreatedAsync(
        PostgresTransactionScope scope,
        CharacterCreated characterCreated,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(characterCreated);

        await bootstrapper.BootstrapAsync(
            scope,
            characterCreated.CharacterId,
            characterCreated.AccountId,
            cancellationToken);

        var settings = await scope.Connection.QuerySingleAsync<StartingBalanceRow>(
            new CommandDefinition(
                """
                select
                    catalog_id as "CatalogId",
                    starting_currency_balance as "StartingBalance"
                from item_system_settings
                where id = 'character_default';
                """,
                transaction: scope.Transaction,
                cancellationToken: cancellationToken));
        if (settings.StartingBalance <= 0)
        {
            return;
        }

        var credit = await characterWallet.CreditAsync(
            scope,
            characterCreated.CharacterId,
            settings.StartingBalance,
            new CurrencyChangeSource(CurrencySourceKinds.StartingBalance, settings.CatalogId, null),
            cancellationToken);
        if (!credit.Succeeded)
        {
            throw new InvalidOperationException(
                "The starting currency balance exceeds the maximum balance.");
        }
    }

    private sealed class StartingBalanceRow
    {
        public string CatalogId { get; set; } = string.Empty;

        public long StartingBalance { get; set; }
    }
}
