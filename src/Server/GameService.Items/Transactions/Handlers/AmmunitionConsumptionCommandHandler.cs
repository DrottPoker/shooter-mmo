using Mmorpg.GameService.Items.Transactions.Domain;

namespace Mmorpg.GameService.Items.Transactions.Handlers;

// Commits one batch of shots that the SimulationWorker counted against the equipped
// ammunition stack. The batch id is the operation id, so a retry replays the result.
internal sealed class AmmunitionConsumptionCommandHandler
    : IItemCommandHandler<ConsumeEquippedAmmunitionCommand>
{
    public async Task ExecuteAsync(
        ItemTransactionContext context,
        ConsumeEquippedAmmunitionCommand command,
        CancellationToken cancellationToken)
    {
        await context.Locks.LockCharacterStatesAsync(
            [new CharacterLockRequest(command.CharacterId, command.ExpectedCharacterRevision)],
            cancellationToken);
        await context.Locks.LockMutationScopeAsync(
            [command.ItemInstanceId],
            [],
            cancellationToken);
        var item = await context.State.LoadOwnedItemAsync(
            command.ItemInstanceId,
            command.CharacterId,
            command.ExpectedItemRevision,
            cancellationToken);
        await ItemQuantityConsumption.ConsumeEquippedAmmunitionAsync(
            context,
            command.CharacterId,
            item,
            command.Quantity,
            cancellationToken);
        context.Journal.TouchCharacter(command.CharacterId);
    }
}
