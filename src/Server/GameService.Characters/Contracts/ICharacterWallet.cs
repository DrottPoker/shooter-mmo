using Mmorpg.GameService.Platform.Database;

namespace Mmorpg.GameService.Characters.Contracts;

public static class CurrencyRules
{
    // The database check on characters.currency enforces the same range.
    public const long MaximumBalance = 1_000_000_000_000L;
}

// Why a balance changed. Every change writes one ledger row with this source.
public static class CurrencySourceKinds
{
    public const string StartingBalance = "starting_balance";
    public const string VendorPurchase = "vendor_purchase";
    public const string VendorSale = "vendor_sale";
    public const string Insurance = "insurance";
    public const string Development = "development";
}

// SourceId names what caused the change, such as a vendor or an item policy, and
// ItemOperationId is the item operation that committed it, when there is one.
public sealed record CurrencyChangeSource(string Kind, string SourceId, Guid? ItemOperationId);

// The result of a currency change. A failed change leaves the balance unchanged.
public sealed record CurrencyChange(bool Succeeded, long Before, long After);

// Character currency for other modules, always in the caller's transaction.
public interface ICharacterWallet
{
    // Locks the character FOR NO KEY UPDATE and subtracts the positive amount when the
    // balance covers it. The character must exist.
    Task<CurrencyChange> DebitAsync(
        PostgresTransactionScope scope,
        Guid characterId,
        long amount,
        CurrencyChangeSource source,
        CancellationToken cancellationToken);

    // Locks the character FOR NO KEY UPDATE and adds the positive amount when the result
    // stays within CurrencyRules.MaximumBalance. The character must exist.
    Task<CurrencyChange> CreditAsync(
        PostgresTransactionScope scope,
        Guid characterId,
        long amount,
        CurrencyChangeSource source,
        CancellationToken cancellationToken);
}
