using Mmorpg.GameService.Items.Contracts;
using Mmorpg.Server.Common.Identity;
using Mmorpg.WorldData.Items;

namespace Mmorpg.GameService.Items.Transactions.Handlers;

// Grants one gathering yield into carried storage. SimulationWorker owns the resource node,
// its charges, and the live interaction; GameService checks the equipped tool and places the
// yield in the same transaction, so a removed tool can never gather. The quest entry counts the
// yield for the gather objectives of the node's actor definition in the same transaction, so a
// retried yield counts once.
internal sealed class GatherResourceCommandHandler(IQuestGatherCredit questCredit)
    : IItemCommandHandler<GatherResourceCommand>
{
    private const int MaximumResourceNodeIdLength = 256;

    private readonly IQuestGatherCredit questCredit = questCredit;

    public async Task ExecuteAsync(
        ItemTransactionContext context,
        GatherResourceCommand command,
        CancellationToken cancellationToken)
    {
        context.Authority.EnsureSimulationWorkerAuthority();
        if (!Identifiers.IsContentIdentifier(command.ResourceProfileId)
            || string.IsNullOrWhiteSpace(command.ResourceNodeId)
            || command.ResourceNodeId.Length > MaximumResourceNodeIdLength
            || !Identifiers.IsContentIdentifier(command.RequiredToolKind)
            || !Identifiers.IsContentIdentifier(command.YieldDefinitionId)
            || command.YieldQuantity <= 0
            || (command.ResourceActorId is not null && !Identifiers.IsContentIdentifier(command.ResourceActorId)))
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.GatheringYieldInvalid,
                "The gathering yield is incomplete or invalid.");
        }

        var character = await context.Locks.LockCharacterStateForIdempotentEventAsync(
            command.CharacterId,
            cancellationToken);
        if (!await context.State.HasEquippedToolKindAsync(
                command.CharacterId,
                command.RequiredToolKind,
                cancellationToken))
        {
            // Names the missing kind, because another tool in the slot looks like the right one.
            // The worker adds the kind to the code, so the client shows its name.
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.GatheringToolRequired,
                "A tool of the kind '" + command.RequiredToolKind + "' is required in the Tool slot.");
        }

        var definition = await context.State.LoadDefinitionAsync(
            command.YieldDefinitionId,
            cancellationToken);
        if (definition.IsBag
            || string.Equals(
                definition.RuntimeDefinition.Category,
                ItemCategoryIds.QuestItem,
                StringComparison.Ordinal))
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.GatheringYieldInvalid,
                "A gathering yield cannot be a Bag or a quest item.");
        }

        await CarriedAcquisition.AcquireAsync(
            context,
            character,
            definition,
            command.YieldQuantity,
            "resource_gathered",
            cancellationToken);
        var simulation = context.Actor.Simulation!;
        context.Journal.AddMetadataAudit(
            "resource_gathered",
            null,
            new
            {
                command.CharacterId,
                command.ResourceProfileId,
                command.ResourceNodeId,
                simulation.ShardId,
                simulation.WorkerRuntimeId,
                DefinitionId = definition.RuntimeDefinition.Id,
                Quantity = command.YieldQuantity
            });
        if (command.ResourceActorId is null)
        {
            return;
        }

        await questCredit.CreditAsync(
            context,
            command.CharacterId,
            command.ResourceActorId,
            command.YieldQuantity,
            cancellationToken);
    }
}
