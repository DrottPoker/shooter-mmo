using Mmorpg.GameService.Platform.Database;

namespace Mmorpg.GameService.Characters.Contracts;

// The result of a currency debit. A failed debit leaves the balance unchanged.
public sealed record CurrencyDebit(bool Succeeded, long Before, long After);

// Character currency for other modules, always in the caller's transaction.
public interface ICharacterWallet
{
    // Locks the character FOR NO KEY UPDATE and subtracts the amount when the balance
    // covers it. The character must exist.
    Task<CurrencyDebit> DebitAsync(
        PostgresTransactionScope scope,
        Guid characterId,
        long amount,
        CancellationToken cancellationToken);
}
