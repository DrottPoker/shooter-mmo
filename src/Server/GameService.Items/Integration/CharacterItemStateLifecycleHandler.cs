using Mmorpg.GameService.Characters.Contracts;
using Mmorpg.GameService.Platform.Database;

namespace Mmorpg.GameService.Items.Integration;

// Every new character gets its complete empty item state in the creation transaction.
internal sealed class CharacterItemStateLifecycleHandler(
    CharacterItemStateBootstrapper bootstrapper) : ICharacterLifecycleHandler
{
    public Task OnCharacterCreatedAsync(
        PostgresTransactionScope scope,
        CharacterCreated characterCreated,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(characterCreated);

        return bootstrapper.BootstrapAsync(
            scope,
            characterCreated.CharacterId,
            characterCreated.AccountId,
            cancellationToken);
    }
}
