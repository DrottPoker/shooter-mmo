using Dapper;
using Mmorpg.GameService.Characters.Contracts;
using Mmorpg.GameService.Platform.Database;

namespace Mmorpg.GameService.Characters;

// Changes a balance and appends its ledger row in the caller's transaction, so the ledger
// deltas of a character always sum to its balance.
internal sealed class CharacterWallet : ICharacterWallet
{
    private const int MaximumSourceIdLength = 256;

    public Task<CurrencyChange> DebitAsync(
        PostgresTransactionScope scope,
        Guid characterId,
        long amount,
        CurrencyChangeSource source,
        CancellationToken cancellationToken)
    {
        return ChangeAsync(scope, characterId, -RequirePositive(amount), source, cancellationToken);
    }

    public Task<CurrencyChange> CreditAsync(
        PostgresTransactionScope scope,
        Guid characterId,
        long amount,
        CurrencyChangeSource source,
        CancellationToken cancellationToken)
    {
        return ChangeAsync(scope, characterId, RequirePositive(amount), source, cancellationToken);
    }

    private static async Task<CurrencyChange> ChangeAsync(
        PostgresTransactionScope scope,
        Guid characterId,
        long delta,
        CurrencyChangeSource source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ValidateSource(source);

        var before = await scope.Connection.QuerySingleAsync<long>(
            new CommandDefinition(
                "select currency from characters where id = @CharacterId for no key update;",
                new { CharacterId = characterId },
                scope.Transaction,
                cancellationToken: cancellationToken));
        var after = before + delta;
        if (after < 0 || after > CurrencyRules.MaximumBalance)
        {
            return new CurrencyChange(false, before, before);
        }

        await scope.Connection.ExecuteAsync(new CommandDefinition(
            """
            update characters set currency = @Currency where id = @CharacterId;

            insert into character_currency_ledger (
                character_id,
                source_kind,
                source_id,
                item_operation_id,
                delta,
                balance_after)
            values (
                @CharacterId,
                @SourceKind,
                @SourceId,
                @ItemOperationId,
                @Delta,
                @Currency);
            """,
            new
            {
                CharacterId = characterId,
                Currency = after,
                SourceKind = source.Kind,
                source.SourceId,
                source.ItemOperationId,
                Delta = delta
            },
            scope.Transaction,
            cancellationToken: cancellationToken));
        return new CurrencyChange(true, before, after);
    }

    private static long RequirePositive(long amount)
    {
        return amount is > 0 and <= CurrencyRules.MaximumBalance
            ? amount
            : throw new ArgumentOutOfRangeException(
                nameof(amount),
                amount,
                "A currency change must be positive and within the maximum balance.");
    }

    private static void ValidateSource(CurrencyChangeSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Kind is not (
                CurrencySourceKinds.StartingBalance
                or CurrencySourceKinds.VendorPurchase
                or CurrencySourceKinds.VendorSale
                or CurrencySourceKinds.Insurance
                or CurrencySourceKinds.Development))
        {
            throw new ArgumentException("The currency source kind is unknown.", nameof(source));
        }

        if (string.IsNullOrWhiteSpace(source.SourceId) || source.SourceId.Length > MaximumSourceIdLength)
        {
            throw new ArgumentException(
                "A currency source id is required and must not exceed 256 characters.",
                nameof(source));
        }
    }
}
