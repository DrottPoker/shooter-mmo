using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Mmorpg.GameService.Characters.Contracts;
using Mmorpg.GameService.Platform.Database;
using Mmorpg.GameService.Testing;
using Mmorpg.Testing;

namespace Mmorpg.GameService.Characters.Tests;

// The gold checks of the economy audit: balances that the wallet changed are clean, and each
// ledger fault planted directly in the database is reported by exactly the check that owns it.
public sealed class CurrencyLedgerAuditIntegrationTests
{
    [PostgresIntegrationFact]
    public async Task AWalletThatChangedGoldLeavesACleanLedger()
    {
        var (context, character) = await CharacterWithGoldAsync();
        await using var _ = context;

        var report = await EconomyAudit.RunAsync(context);

        var balance = Assert.Single(report.Checks, check => check.Id == "currency_balance_matches_ledger");
        var running = Assert.Single(report.Checks, check => check.Id == "currency_ledger_running_balance");
        Assert.Equal(1, balance.Examined);
        Assert.True(running.Examined >= 2);
        Assert.Equal(0, report.Violations);
        Assert.NotEqual(Guid.Empty, character);
    }

    [PostgresIntegrationFact]
    public async Task ABalanceThatChangedWithoutItsLedgerRowDoesNotSum()
    {
        var (context, character) = await CharacterWithGoldAsync();
        await using var _ = context;
        await ExecuteAsync(context, "update characters set currency = currency + 7 where id = @Character;", character);

        var check = await EconomyAudit.AssertOnlyViolationAsync(context, "currency_balance_matches_ledger");

        var example = Assert.Single(check.Examples);
        Assert.Equal(character.ToString(), example["character_id"]);
        Assert.Equal(
            long.Parse(example["balance"]!, System.Globalization.CultureInfo.InvariantCulture),
            long.Parse(example["ledger_sum"]!, System.Globalization.CultureInfo.InvariantCulture) + 7);
    }

    [PostgresIntegrationFact]
    public async Task ALedgerRowWhoseBalanceAfterWasChangedBreaksTheRunningBalance()
    {
        var (context, character) = await CharacterWithGoldAsync();
        await using var _ = context;
        await ExecuteAsync(
            context,
            """
            update character_currency_ledger
            set balance_after = balance_after + 1
            where id = (
                select min(id)
                from character_currency_ledger
                where character_id = @Character);
            """,
            character);

        var check = await EconomyAudit.AssertOnlyViolationAsync(context, "currency_ledger_running_balance");

        Assert.Equal(1, check.Violations);
        Assert.Equal(character.ToString(), Assert.Single(check.Examples)["character_id"]);
    }

    [PostgresIntegrationFact]
    public async Task ATradeRowWithoutItsOperationHasNoReason()
    {
        var (context, character) = await CharacterWithGoldAsync();
        await using var _ = context;
        await ExecuteAsync(
            context,
            """
            update characters set currency = currency + 12 where id = @Character;
            insert into character_currency_ledger (
                character_id,
                source_kind,
                source_id,
                item_operation_id,
                delta,
                balance_after)
            select @Character, 'vendor_sale', 'fixture.vendor', null, 12, currency
            from characters
            where id = @Character;
            """,
            character);

        var check = await EconomyAudit.AssertOnlyViolationAsync(context, "currency_ledger_reason");

        var example = Assert.Single(check.Examples);
        Assert.Equal("vendor_sale", example["source_kind"]);
        Assert.Null(example["item_operation_id"]);
    }

    [PostgresIntegrationFact]
    public async Task ARowWithABlankSourceIdHasNoReason()
    {
        var (context, character) = await CharacterWithGoldAsync();
        await using var _ = context;
        await ExecuteAsync(
            context,
            """
            update character_currency_ledger
            set source_id = '   '
            where id = (
                select max(id)
                from character_currency_ledger
                where character_id = @Character);
            """,
            character);

        var check = await EconomyAudit.AssertOnlyViolationAsync(context, "currency_ledger_reason");

        Assert.Equal(CurrencySourceKinds.Development, Assert.Single(check.Examples)["source_kind"]);
    }

    // A fresh character whose gold the wallet credited and debited, audited clean.
    private static async Task<(PostgresIntegrationTestContext Context, Guid Character)> CharacterWithGoldAsync()
    {
        var context = await PostgresIntegrationTestContext.CreateAsync();
        try
        {
            var player = await context.RegisterPlayerAsync();
            var wallet = context.Services.GetRequiredService<ICharacterWallet>();
            await using (var connection = await context.DataSource.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                var scope = new PostgresTransactionScope(connection, transaction);
                var source = new CurrencyChangeSource(CurrencySourceKinds.Development, "audit_test", null);
                Assert.True((await wallet.CreditAsync(scope, player.Character.Id, 30, source, CancellationToken.None)).Succeeded);
                Assert.True((await wallet.DebitAsync(scope, player.Character.Id, 5, source, CancellationToken.None)).Succeeded);
                await transaction.CommitAsync();
            }

            await EconomyAudit.AssertCleanAsync(context);
            return (context, player.Character.Id);
        }
        catch
        {
            await context.DisposeAsync();
            throw;
        }
    }

    private static async Task ExecuteAsync(PostgresIntegrationTestContext context, string sql, Guid character)
    {
        await using var connection = await context.DataSource.OpenConnectionAsync();
        await connection.ExecuteAsync(sql, new { Character = character });
    }
}
