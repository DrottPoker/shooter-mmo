using Dapper;

namespace Mmorpg.GameService.Items.Transactions.Domain;

// Corpse custody checks shared by every corpse interaction: the open-corpse row
// lock, corpse section membership, and the guarded corpse revision advance.
internal static class CorpseCustody
{
    public static async Task<LockedCorpseRow> LockOpenCorpseAsync(
        ItemTransactionContext context,
        Guid corpseId,
        CancellationToken cancellationToken)
    {
        var corpse = await context.Connection.QuerySingleOrDefaultAsync<LockedCorpseRow>(
            new CommandDefinition(
                """
                select
                    id as "CorpseId",
                    shard_id as "ShardId",
                    revision as "Revision",
                    expires_at as "ExpiresAt",
                    closed_at as "ClosedAt",
                    close_reason as "CloseReason",
                    expires_at <= now() as "IsExpired"
                from corpses
                where id = @CorpseId
                for update;
                """,
                new { CorpseId = corpseId },
                context.Transaction,
                cancellationToken: cancellationToken));
        if (corpse is null)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.CorpseNotFound,
                "The corpse was not found.");
        }

        if (corpse.IsExpired
            || string.Equals(corpse.CloseReason, "expired", StringComparison.Ordinal))
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.CorpseExpired,
                "The corpse has reached its absolute expiry time.");
        }

        if (corpse.ClosedAt is not null)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.CorpseInvalidated,
                "The corpse is no longer available for interaction.");
        }

        if (context.Actor.Authority == ItemTransactionAuthority.SimulationWorker
            && !string.Equals(
                context.Actor.Simulation!.ShardId,
                corpse.ShardId,
                StringComparison.Ordinal))
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.WrongSimulationWorker,
                "The corpse belongs to another Shard.");
        }

        return corpse;
    }

    public static Task<bool> OwnsCorpseBagSectionAsync(
        ItemTransactionContext context,
        Guid corpseId,
        Guid bagItemInstanceId,
        CancellationToken cancellationToken)
    {
        return context.Connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            """
            select exists (
                select 1
                from item_containers contents
                join corpse_sections section on section.container_id = contents.id
                where contents.bound_bag_item_instance_id = @BagItemInstanceId
                  and section.corpse_id = @CorpseId);
            """,
            new { CorpseId = corpseId, BagItemInstanceId = bagItemInstanceId },
            context.Transaction,
            cancellationToken: cancellationToken));
    }

    public static Task<bool> IsCorpseSectionContainerAsync(
        ItemTransactionContext context,
        Guid corpseId,
        Guid containerId,
        CancellationToken cancellationToken)
    {
        return context.Connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            """
            select exists (
                select 1
                from corpse_sections
                where corpse_id = @CorpseId
                  and container_id = @ContainerId);
            """,
            new { CorpseId = corpseId, ContainerId = containerId },
            context.Transaction,
            cancellationToken: cancellationToken));
    }

    public static async Task AdvanceCorpseRevisionAsync(
        ItemTransactionContext context,
        LockedCorpseRow corpse,
        CancellationToken cancellationToken)
    {
        await AdvanceCorpseRevisionAsync(
            context,
            corpse,
            "corpse_loot_revision_advanced",
            cancellationToken);
    }

    public static async Task AdvanceCorpseRevisionAsync(
        ItemTransactionContext context,
        LockedCorpseRow corpse,
        string auditKind,
        CancellationToken cancellationToken)
    {
        var nextRevision = await context.Connection.QuerySingleAsync<long>(
            new CommandDefinition(
                """
                update corpses
                set revision = revision + 1
                where id = @CorpseId
                  and revision = @ExpectedRevision
                returning revision;
                """,
                new
                {
                    corpse.CorpseId,
                    ExpectedRevision = corpse.Revision
                },
                context.Transaction,
                cancellationToken: cancellationToken));
        context.AddMetadataAudit(
            auditKind,
            new { corpse.CorpseId, corpse.Revision },
            new { corpse.CorpseId, Revision = nextRevision });
    }
}

internal sealed class LockedCorpseRow
{
    public Guid CorpseId { get; set; }

    public string ShardId { get; set; } = string.Empty;

    public long Revision { get; set; }

    public DateTime ExpiresAt { get; set; }

    public DateTime? ClosedAt { get; set; }

    public string? CloseReason { get; set; }

    public bool IsExpired { get; set; }
}
