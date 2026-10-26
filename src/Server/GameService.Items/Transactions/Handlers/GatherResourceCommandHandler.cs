using Dapper;
using Mmorpg.GameService.Items.Transactions.Domain;
using Mmorpg.Server.Common.Identity;
using Mmorpg.WorldData.Items;

namespace Mmorpg.GameService.Items.Transactions.Handlers;

// Grants one gathering yield into carried storage. SimulationWorker owns the resource node,
// its charges, and the live interaction; GameService checks the equipped tool and places the
// yield in the same transaction, so a removed tool can never gather.
internal sealed class GatherResourceCommandHandler
    : IItemCommandHandler<GatherResourceCommand>
{
    private const int MaximumResourceNodeIdLength = 256;

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
            || command.YieldQuantity <= 0)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.GatheringYieldInvalid,
                "The gathering yield is incomplete or invalid.");
        }

        var character = await context.Locks.LockCharacterStateForIdempotentEventAsync(
            command.CharacterId,
            cancellationToken);
        var hasTool = await context.Connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            """
            select exists (
                select 1
                from item_instances tool
                join item_definition_tool_kinds tool_kind
                  on tool_kind.definition_id = tool.definition_id
                join item_tool_kinds kind
                  on kind.id = tool_kind.tool_kind_id
                 and kind.is_active
                where tool.equipped_character_id = @CharacterId
                  and tool.equipment_slot_id = @ToolSlotId
                  and tool_kind.tool_kind_id = @ToolKind);
            """,
            new
            {
                command.CharacterId,
                ToolSlotId = ItemEquipmentSlotIds.Tool,
                ToolKind = command.RequiredToolKind
            },
            context.Transaction,
            cancellationToken: cancellationToken));
        if (!hasTool)
        {
            // Names the missing kind, because another tool in the slot looks like the right one.
            var toolKindName = await context.Connection.ExecuteScalarAsync<string?>(new CommandDefinition(
                "select display_name from item_tool_kinds where id = @ToolKind;",
                new { ToolKind = command.RequiredToolKind },
                context.Transaction,
                cancellationToken: cancellationToken));
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.GatheringToolRequired,
                (toolKindName ?? command.RequiredToolKind) + " required in the Tool slot.");
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
    }
}
