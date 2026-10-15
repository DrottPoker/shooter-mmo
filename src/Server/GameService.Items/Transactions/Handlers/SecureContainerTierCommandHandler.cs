using Dapper;
using Mmorpg.GameService.Characters.Contracts;
using Mmorpg.GameService.Items.Transactions.Domain;

namespace Mmorpg.GameService.Items.Transactions.Handlers;

// Changes an account's Secure Container tier for every character of the account and
// moves items above a reduced capacity into Recovery Storage.
internal sealed class SecureContainerTierCommandHandler
    : IItemCommandHandler<ChangeSecureContainerTierCommand>
{
    public async Task ExecuteAsync(
        ItemTransactionContext context,
        ChangeSecureContainerTierCommand command,
        CancellationToken cancellationToken)
    {
        if (command.AccountId == Guid.Empty || string.IsNullOrWhiteSpace(command.TierId))
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemNotFound,
                "An account id and Secure Container tier id are required.");
        }

        context.Authority.EnsureAccountAuthority(command.AccountId);
        await context.Connection.ExecuteAsync(new CommandDefinition(
            """
            select pg_advisory_xact_lock(
                hashtextextended('item-secure-entitlement:' || cast(@AccountId as text), 0));
            """,
            new { command.AccountId },
            context.Transaction,
            cancellationToken: cancellationToken));

        var candidates = (await context.Connection.QueryAsync<CharacterLockCandidate>(
            new CommandDefinition(
                $"""
                select
                    state.character_id as "CharacterId",
                    state.revision as "Revision"
                from character_item_states state
                join {CharactersSql.ActiveCharacters} character on character.id = state.character_id
                where character.account_id = @AccountId
                order by state.character_id;
                """,
                new { command.AccountId },
                context.Transaction,
                cancellationToken: cancellationToken))).ToArray();
        var suppliedCharacterRevisions = command.CharacterRevisions
            .GroupBy(expectation => expectation.CharacterId)
            .ToDictionary(group => group.Key, group => group.Select(value => value.Revision).Distinct().ToArray());
        if (suppliedCharacterRevisions.Values.Any(revisions => revisions.Length != 1))
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemStateConflict,
                "Secure Container tier change character revisions contain duplicates.");
        }

        if (context.Actor.Authority == ItemTransactionAuthority.Account
            && !candidates.Select(candidate => candidate.CharacterId).Order().SequenceEqual(
                suppliedCharacterRevisions.Keys.Order()))
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemStateConflict,
                "An account tier change requires every affected character revision.");
        }

        if (suppliedCharacterRevisions.Keys.Any(characterId =>
                candidates.All(candidate => candidate.CharacterId != characterId)))
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemStateConflict,
                "A supplied character revision does not belong to the account.");
        }

        var lockRequests = candidates.Select(candidate => new CharacterLockRequest(
            candidate.CharacterId,
            suppliedCharacterRevisions.TryGetValue(candidate.CharacterId, out var revisions)
                ? revisions[0]
                : null));
        var states = await context.LockCharacterStatesAsync(lockRequests, cancellationToken);
        var entitlement = await context.Connection.QuerySingleOrDefaultAsync<SecureEntitlementRow>(
            new CommandDefinition(
                """
                select
                    account_id as "AccountId",
                    tier_id as "TierId",
                    revision as "Revision"
                from account_secure_container_entitlements
                where account_id = @AccountId
                for update;
                """,
                new { command.AccountId },
                context.Transaction,
                cancellationToken: cancellationToken));
        if (entitlement is null)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemNotFound,
                "The account Secure Container entitlement was not found.");
        }

        if (entitlement.Revision != command.ExpectedEntitlementRevision)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemStateConflict,
                "The Secure Container entitlement changed before the operation committed.");
        }

        var tier = await context.Connection.QuerySingleOrDefaultAsync<SecureTierRow>(
            new CommandDefinition(
                """
                select
                    id as "TierId",
                    slot_capacity as "SlotCapacity"
                from secure_container_tiers
                where id = @TierId
                  and is_active;
                """,
                new { command.TierId },
                context.Transaction,
                cancellationToken: cancellationToken));
        if (tier is null)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemNotFound,
                "The Secure Container tier was not found.");
        }

        // An account may always return to the free base tier. A larger tier is granted
        // only by a trusted entitlement or purchase source running with system authority.
        if (context.Actor.Authority != ItemTransactionAuthority.System)
        {
            var baseTierId = await context.Connection.ExecuteScalarAsync<string>(
                new CommandDefinition(
                    """
                    select base_secure_container_tier_id
                    from item_catalog_revisions
                    where is_current;
                    """,
                    transaction: context.Transaction,
                    cancellationToken: cancellationToken));
            if (!string.Equals(tier.TierId, baseTierId, StringComparison.Ordinal))
            {
                ItemTransactionContext.Reject(
                    ItemTransactionErrorCodes.AuthorityRequired,
                    "Only an entitlement source can grant a Secure Container tier above the base tier.");
            }
        }

        if (string.Equals(entitlement.TierId, tier.TierId, StringComparison.Ordinal))
        {
            var unchanged = new SecureEntitlementAuditState(
                command.AccountId,
                entitlement.TierId,
                entitlement.Revision);
            context.Journal.AddMetadataAudit("secure_tier_unchanged", unchanged, unchanged);
            context.Journal.SetSecureContainerEntitlementRevision(entitlement.Revision);
            return;
        }

        var allContainerIds = states.Values
            .SelectMany(state => new[]
            {
                state.SecureContainerId,
                state.RecoveryStorageContainerId
            })
            .Distinct()
            .ToArray();
        var overflowItems = await LoadSecureOverflowItemsAsync(
            context,
            states.Values.Select(state => state.SecureContainerId),
            tier.SlotCapacity,
            cancellationToken);
        await context.LockMutationScopeAsync(
            overflowItems.Select(item => item.ItemInstanceId),
            allContainerIds,
            cancellationToken);
        overflowItems = await LoadSecureOverflowItemsAsync(
            context,
            states.Values.Select(state => state.SecureContainerId),
            tier.SlotCapacity,
            cancellationToken);

        var entitlementBefore = new SecureEntitlementAuditState(
            command.AccountId,
            entitlement.TierId,
            entitlement.Revision);
        var entitlementRevision = await context.Connection.QuerySingleAsync<long>(
            new CommandDefinition(
                """
                update account_secure_container_entitlements
                set tier_id = @TierId,
                    revision = revision + 1,
                    updated_at = now()
                where account_id = @AccountId
                returning revision;
                """,
                new { command.AccountId, TierId = tier.TierId },
                context.Transaction,
                cancellationToken: cancellationToken));

        foreach (var state in states.Values.OrderBy(value => value.CharacterId))
        {
            var secureContainer = await context.State.LoadOwnedContainerAsync(
                state.SecureContainerId,
                state.CharacterId,
                cancellationToken);
            var recoveryContainer = await context.State.LoadKernelPlacementContainerAsync(
                state.RecoveryStorageContainerId,
                state.CharacterId,
                cancellationToken);
            if (!string.Equals(secureContainer.ContainerType, "secure_container", StringComparison.Ordinal)
                || !string.Equals(recoveryContainer.ContainerType, "recovery_storage", StringComparison.Ordinal)
                || secureContainer.SlotCapacity is null)
            {
                throw new InvalidOperationException(
                    $"Character '{state.CharacterId}' has invalid Secure Container state.");
            }

            var characterOverflow = overflowItems
                .Where(item => item.CharacterId == state.CharacterId)
                .OrderByDescending(item => item.ContainerSlotIndex)
                .ThenBy(item => item.ItemInstanceId)
                .ToArray();
            if (characterOverflow.Length > 0)
            {
                var deliveryId = Guid.NewGuid();
                await context.Connection.ExecuteAsync(new CommandDefinition(
                    """
                    insert into recovery_deliveries (
                        id,
                        character_id,
                        recovery_storage_container_id,
                        source_kind,
                        source_event_id,
                        available_at)
                    values (
                        @DeliveryId,
                        @CharacterId,
                        @RecoveryStorageContainerId,
                        'secure_capacity_reduction',
                        @SourceEventId,
                        now());
                    """,
                    new
                    {
                        DeliveryId = deliveryId,
                        state.CharacterId,
                        RecoveryStorageContainerId = recoveryContainer.ContainerId,
                        SourceEventId = context.OperationId.ToString("N")
                    },
                    context.Transaction,
                    cancellationToken: cancellationToken));
                var nextRecoverySlotIndex = await RecoveryDeliveries.GetNextRecoverySlotIndexAsync(
                    context,
                    recoveryContainer.ContainerId,
                    cancellationToken);
                for (var index = 0; index < characterOverflow.Length; index++)
                {
                    var overflow = characterOverflow[index];
                    var item = await context.State.LoadOwnedItemAsync(
                        overflow.ItemInstanceId,
                        state.CharacterId,
                        overflow.Revision,
                        cancellationToken);
                    var beforeState = await context.Journal.CaptureItemStateJsonAsync(
                        item.ItemInstanceId,
                        cancellationToken);
                    await context.Connection.ExecuteAsync(new CommandDefinition(
                        """
                        insert into item_container_slots (container_id, slot_index, slot_kind)
                        values (@RecoveryStorageContainerId, @RecoverySlotIndex, 'general');

                        update item_instances
                        set container_id = @RecoveryStorageContainerId,
                            container_slot_index = @RecoverySlotIndex,
                            revision = revision + 1,
                            updated_at = now()
                        where id = @ItemInstanceId;

                        insert into recovery_delivery_items (
                            recovery_delivery_id,
                            item_instance_id,
                            item_order)
                        values (@DeliveryId, @ItemInstanceId, @ItemOrder);
                        """,
                        new
                        {
                            RecoveryStorageContainerId = recoveryContainer.ContainerId,
                            RecoverySlotIndex = nextRecoverySlotIndex + index,
                            item.ItemInstanceId,
                            DeliveryId = deliveryId,
                            ItemOrder = index
                        },
                        context.Transaction,
                        cancellationToken: cancellationToken));
                    var afterState = await context.Journal.CaptureItemStateJsonAsync(
                        item.ItemInstanceId,
                        cancellationToken);
                    context.Journal.AddItemAudit(
                        "secure_capacity_reduction_recovery",
                        item.ItemInstanceId,
                        beforeState,
                        afterState);
                    context.Journal.IncludeResultItem(item.ItemInstanceId);
                }

                context.Journal.TouchContainer(recoveryContainer.ContainerId);
                context.Journal.AddRecoveryDelivery(deliveryId);
            }

            await context.Connection.ExecuteAsync(new CommandDefinition(
                """
                delete from item_container_slots
                where container_id = @SecureContainerId
                  and slot_index >= @SlotCapacity;

                insert into item_container_slots (container_id, slot_index, slot_kind)
                select @SecureContainerId, generated.slot_index, 'general'
                from generate_series(@CurrentSlotCapacity, @SlotCapacity - 1) generated(slot_index)
                on conflict (container_id, slot_index) do nothing;

                update item_containers
                set slot_capacity = @SlotCapacity,
                    updated_at = now()
                where id = @SecureContainerId;
                """,
                new
                {
                    SecureContainerId = secureContainer.ContainerId,
                    SlotCapacity = tier.SlotCapacity,
                    CurrentSlotCapacity = secureContainer.SlotCapacity.Value
                },
                context.Transaction,
                cancellationToken: cancellationToken));
            context.Journal.TouchContainer(secureContainer.ContainerId);
            context.Journal.TouchCharacter(state.CharacterId);
        }

        var entitlementAfter = new SecureEntitlementAuditState(
            command.AccountId,
            tier.TierId,
            entitlementRevision);
        context.Journal.AddMetadataAudit(
            "secure_tier_changed",
            entitlementBefore,
            entitlementAfter);
        context.Journal.SetSecureContainerEntitlementRevision(entitlementRevision);
    }

    private static async Task<SecureOverflowItem[]> LoadSecureOverflowItemsAsync(
        ItemTransactionContext context,
        IEnumerable<Guid> secureContainerIds,
        int slotCapacity,
        CancellationToken cancellationToken)
    {
        var containerIds = secureContainerIds.Distinct().Order().ToArray();
        if (containerIds.Length == 0)
        {
            return [];
        }

        return (await context.Connection.QueryAsync<SecureOverflowItem>(new CommandDefinition(
            """
            select
                container.owner_character_id as "CharacterId",
                item.id as "ItemInstanceId",
                item.revision as "Revision",
                item.container_slot_index as "ContainerSlotIndex"
            from item_instances item
            join item_containers container on container.id = item.container_id
            where item.container_id = any(@ContainerIds)
              and item.container_slot_index >= @SlotCapacity
            order by
                container.owner_character_id,
                item.container_slot_index desc,
                item.id;
            """,
            new { ContainerIds = containerIds, SlotCapacity = slotCapacity },
            context.Transaction,
            cancellationToken: cancellationToken))).ToArray();
    }

    private sealed class CharacterLockCandidate
    {
        public Guid CharacterId { get; set; }

        public long Revision { get; set; }
    }

    private sealed class SecureEntitlementRow
    {
        public Guid AccountId { get; set; }

        public string TierId { get; set; } = string.Empty;

        public long Revision { get; set; }
    }

    private sealed class SecureTierRow
    {
        public string TierId { get; set; } = string.Empty;

        public int SlotCapacity { get; set; }
    }

    private sealed class SecureOverflowItem
    {
        public Guid CharacterId { get; set; }

        public Guid ItemInstanceId { get; set; }

        public long Revision { get; set; }

        public int ContainerSlotIndex { get; set; }
    }

    private sealed record SecureEntitlementAuditState(
        Guid AccountId,
        string TierId,
        long Revision);
}
