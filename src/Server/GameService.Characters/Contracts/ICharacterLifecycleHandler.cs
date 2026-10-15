using Mmorpg.GameService.Platform.Database;

namespace Mmorpg.GameService.Characters.Contracts;

// A character was inserted in the caller's transaction.
public sealed record CharacterCreated(Guid CharacterId, Guid AccountId);

// Lets a higher module create its own per-character state in the character creation
// transaction, so a character never exists without it. Exactly one implementation is
// registered.
public interface ICharacterLifecycleHandler
{
    Task OnCharacterCreatedAsync(
        PostgresTransactionScope scope,
        CharacterCreated characterCreated,
        CancellationToken cancellationToken);
}
