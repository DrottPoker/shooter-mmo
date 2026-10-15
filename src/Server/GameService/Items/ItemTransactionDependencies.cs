using Mmorpg.GameService.Characters.Contracts;

namespace Mmorpg.GameService.Items;

// Other modules' contracts that an item transaction uses inside its own transaction.
internal sealed record ItemTransactionDependencies(
    ICharacterLocks CharacterLocks,
    ICharacterWallet CharacterWallet);
