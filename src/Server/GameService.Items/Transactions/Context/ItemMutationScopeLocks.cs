using System.Diagnostics;
using Dapper;
using Npgsql;
using static Mmorpg.GameService.Items.ItemTransactionRejection;

namespace Mmorpg.GameService.Items.Transactions;

// Discovers and locks the mutation scope of a command: Bag aggregate roots, then
// containers, then ordinary items and Bag children, then item policies, each in
// ascending id order. Lock waits are recorded as mutation_scope.
internal sealed class ItemMutationScopeLocks(
    ItemTransactionSession session,
    ItemOperationsMetrics? operationsMetrics)
{
    private NpgsqlConnection Connection => session.Connection;

    private NpgsqlTransaction Transaction => session.Transaction;

    public async Task LockMutationScopeAsync(
        IEnumerable<Guid> itemIds,
        IEnumerable<Guid> containerIds,
        CancellationToken cancellationToken)
    {
        var lockStarted = Stopwatch.GetTimestamp();
        try
        {
            await LockMutationScopeCoreAsync(itemIds, containerIds, cancellationToken);
        }
        finally
        {
            operationsMetrics?.RecordLockWait(
                "mutation_scope",
                Stopwatch.GetElapsedTime(lockStarted));
        }
    }

    private async Task LockMutationScopeCoreAsync(
        IEnumerable<Guid> itemIds,
        IEnumerable<Guid> containerIds,
        CancellationToken cancellationToken)
    {
        var requestedItemIds = itemIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .Order()
            .ToArray();
        var requestedContainerIds = containerIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToHashSet();

        var discoveryRows = requestedItemIds.Length == 0
            ? []
            : (await Connection.QueryAsync<LockDiscoveryRow>(new CommandDefinition(
                """
                select
                    item.id as "ItemInstanceId",
                    item.container_id as "ContainerId",
                    case when bag.definition_id is null then null else item.id end as "ItemBagRootId",
                    source_container.bound_bag_item_instance_id as "SourceBagRootId",
                    bag_contents.id as "BagContentsContainerId"
                from item_instances item
                left join bag_definitions bag on bag.definition_id = item.definition_id
                left join item_containers source_container on source_container.id = item.container_id
                left join item_containers bag_contents
                  on bag_contents.bound_bag_item_instance_id = item.id
                 and bag_contents.container_type = 'bag_contents'
                where item.id = any(@ItemIds)
                order by item.id;
                """,
                new { ItemIds = requestedItemIds },
                Transaction,
                cancellationToken: cancellationToken))).ToArray();

        foreach (var sourceContainerId in discoveryRows
                     .Where(row => row.ContainerId is not null)
                     .Select(row => row.ContainerId!.Value))
        {
            requestedContainerIds.Add(sourceContainerId);
        }

        foreach (var bagContentsContainerId in discoveryRows
                     .Where(row => row.BagContentsContainerId is not null)
                     .Select(row => row.BagContentsContainerId!.Value))
        {
            requestedContainerIds.Add(bagContentsContainerId);
        }

        var explicitContainerIds = requestedContainerIds.Order().ToArray();
        var containerBagRoots = explicitContainerIds.Length == 0
            ? []
            : (await Connection.QueryAsync<Guid>(new CommandDefinition(
                """
                select bound_bag_item_instance_id
                from item_containers
                where id = any(@ContainerIds)
                  and bound_bag_item_instance_id is not null
                order by bound_bag_item_instance_id;
                """,
                new { ContainerIds = explicitContainerIds },
                Transaction,
                cancellationToken: cancellationToken))).ToArray();

        var bagRootIds = discoveryRows
            .SelectMany(row => new[] { row.ItemBagRootId, row.SourceBagRootId })
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .Concat(containerBagRoots)
            .Distinct()
            .Order()
            .ToArray();

        if (bagRootIds.Length > 0)
        {
            var lockedBagCount = (await Connection.QueryAsync<Guid>(new CommandDefinition(
                """
                select id
                from item_instances
                where id = any(@BagItemIds)
                order by id
                for update;
                """,
                new { BagItemIds = bagRootIds },
                Transaction,
                cancellationToken: cancellationToken))).Count();
            if (lockedBagCount != bagRootIds.Length)
            {
                Reject(
                    ItemTransactionErrorCodes.BagStateChanged,
                    "A required Bag aggregate changed before it could be locked.");
            }
        }

        if (explicitContainerIds.Length > 0)
        {
            await Connection.QueryAsync<Guid>(new CommandDefinition(
                """
                select id
                from item_containers
                where id = any(@ContainerIds)
                order by id
                for update;
                """,
                new { ContainerIds = explicitContainerIds },
                Transaction,
                cancellationToken: cancellationToken));
        }

        var aggregateContainerIds = discoveryRows
            .Where(row => row.ItemBagRootId is not null
                && row.BagContentsContainerId is not null)
            .Select(row => row.BagContentsContainerId!.Value)
            .Distinct()
            .Order()
            .ToArray();
        var aggregateChildItemIds = aggregateContainerIds.Length == 0
            ? []
            : (await Connection.QueryAsync<Guid>(new CommandDefinition(
                """
                select id
                from item_instances
                where container_id = any(@ContainerIds)
                order by id;
                """,
                new { ContainerIds = aggregateContainerIds },
                Transaction,
                cancellationToken: cancellationToken))).ToArray();

        var ordinaryItemIds = requestedItemIds
            .Except(bagRootIds)
            .Concat(aggregateChildItemIds)
            .Distinct()
            .Order()
            .ToArray();
        if (ordinaryItemIds.Length > 0)
        {
            await Connection.QueryAsync<Guid>(new CommandDefinition(
                """
                select id
                from item_instances
                where id = any(@ItemIds)
                order by id
                for update;
                """,
                new { ItemIds = ordinaryItemIds },
                Transaction,
                cancellationToken: cancellationToken));
        }

        var policyItemIds = requestedItemIds
            .Concat(bagRootIds)
            .Concat(aggregateChildItemIds)
            .Distinct()
            .Order()
            .ToArray();
        if (policyItemIds.Length > 0)
        {
            await Connection.QueryAsync<Guid>(new CommandDefinition(
                """
                select id
                from item_instance_policies
                where item_instance_id = any(@ItemIds)
                order by item_instance_id, id
                for update;
                """,
                new { ItemIds = policyItemIds },
                Transaction,
                cancellationToken: cancellationToken));
        }
    }

    private sealed class LockDiscoveryRow
    {
        public Guid ItemInstanceId { get; set; }

        public Guid? ContainerId { get; set; }

        public Guid? ItemBagRootId { get; set; }

        public Guid? SourceBagRootId { get; set; }

        public Guid? BagContentsContainerId { get; set; }
    }
}
