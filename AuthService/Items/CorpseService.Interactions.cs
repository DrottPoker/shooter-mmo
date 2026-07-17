using System.Data;
using System.Security.Cryptography;
using System.Text;
using AuthService.Auth;
using AuthService.Http;
using Dapper;
using Npgsql;

namespace AuthService.Items;

public sealed partial class CorpseService
{
    public async Task<ServiceResult<CorpseViewSnapshotResponse>> OpenForSimulationAsync(
        string authenticatedWorkerId,
        Guid simulationSessionId,
        Guid corpseId,
        SimulationCorpseOpenRequest request,
        CancellationToken cancellationToken)
    {
        var validation = ValidateSimulationRequest(
            authenticatedWorkerId,
            simulationSessionId,
            corpseId,
            request.AccountId,
            request.CharacterId,
            request.WorkerId,
            request.WorkerRuntimeId,
            request.ShardId,
            request.SessionToken);
        if (validation is not null)
        {
            return MapRequestValidation<CorpseViewSnapshotResponse>(validation);
        }

        await using var connection = await DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "set transaction read only;",
            transaction: transaction,
            cancellationToken: cancellationToken));
        try
        {
            var authorityError = await ValidateReadAuthorityAsync(
                connection,
                transaction,
                simulationSessionId,
                request,
                cancellationToken);
            if (authorityError is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return MapSnapshotError(authorityError);
            }

            var loaded = await LoadSnapshotAsync(
                connection,
                transaction,
                corpseId,
                request.ShardId,
                cancellationToken);
            if (loaded.Snapshot is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return MapSnapshotError(loaded.Error!);
            }

            await transaction.CommitAsync(cancellationToken);
            return ServiceResult<CorpseViewSnapshotResponse>.Ok(loaded.Snapshot);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<ServiceResult<CorpseMutationResponse>> MutateForSimulationAsync(
        string authenticatedWorkerId,
        Guid simulationSessionId,
        Guid corpseId,
        SimulationCorpseMutationRequest request,
        CancellationToken cancellationToken)
    {
        var validation = ValidateSimulationRequest(
            authenticatedWorkerId,
            simulationSessionId,
            corpseId,
            request.AccountId,
            request.CharacterId,
            request.WorkerId,
            request.WorkerRuntimeId,
            request.ShardId,
            request.SessionToken);
        if (validation is not null)
        {
            return MapRequestValidation<CorpseMutationResponse>(validation);
        }

        if (request.OperationId == Guid.Empty
            || request.ExpectedCorpseRevision < 0
            || string.IsNullOrWhiteSpace(request.OperationKind))
        {
            return ServiceResult<CorpseMutationResponse>.BadRequest(
                ItemApiErrorCodes.SimulationOperationInvalid,
                "The simulation corpse mutation is incomplete or invalid.");
        }

        var actor = ItemTransactionActor.ForSimulationWorker(
            request.AccountId,
            request.CharacterId,
            simulationSessionId,
            request.WorkerId.Trim(),
            request.WorkerRuntimeId.Trim(),
            request.ShardId.Trim(),
            TokenGenerator.HashToken(request.SessionToken.Trim()),
            ItemTransactionLiveAccess.None);
        var operation = CreateCorpseMutation(
            actor,
            corpseId,
            request,
            cancellationToken);
        if (operation is null)
        {
            return ServiceResult<CorpseMutationResponse>.BadRequest(
                ItemApiErrorCodes.SimulationOperationInvalid,
                "The requested corpse mutation shape is invalid.");
        }

        var transactionResult = await operation;
        if (!transactionResult.Succeeded)
        {
            return MapTransactionResult<CorpseMutationResponse>(transactionResult, null);
        }

        var snapshot = await LoadCurrentSnapshotAsync(
            corpseId,
            request.ShardId,
            cancellationToken);
        return ServiceResult<CorpseMutationResponse>.Ok(
            new CorpseMutationResponse(transactionResult, snapshot));
    }

    private Task<ItemTransactionResult>? CreateCorpseMutation(
        ItemTransactionActor actor,
        Guid corpseId,
        SimulationCorpseMutationRequest request,
        CancellationToken cancellationToken)
    {
        var operationKind = request.OperationKind.Trim();
        if (string.Equals(
                operationKind,
                CorpseInteractionOperationKinds.LootItem,
                StringComparison.Ordinal)
            && HasItemTransferShape(request, requiresPartialQuantity: false))
        {
            return TransactionService.ExecuteAsync(
                new ItemTransactionRequest<LootCorpseItemCommand>(
                    request.OperationId,
                    actor,
                    new LootCorpseItemCommand(
                        request.CharacterId,
                        corpseId,
                        request.ItemInstanceId!.Value,
                        request.ExpectedItemRevision!.Value,
                        request.DestinationContainerId!.Value,
                        request.ExpectedDestinationContainerRevision!.Value,
                        request.DestinationSlotIndex!.Value,
                        NormalizeGuid(request.TargetItemInstanceId),
                        NormalizeRevision(
                            request.TargetItemInstanceId,
                            request.ExpectedTargetItemRevision))),
                cancellationToken);
        }

        if (string.Equals(
                operationKind,
                CorpseInteractionOperationKinds.LootPartialStack,
                StringComparison.Ordinal)
            && HasItemTransferShape(request, requiresPartialQuantity: true))
        {
            return TransactionService.ExecuteAsync(
                new ItemTransactionRequest<LootCorpsePartialStackCommand>(
                    request.OperationId,
                    actor,
                    new LootCorpsePartialStackCommand(
                        request.CharacterId,
                        corpseId,
                        request.ItemInstanceId!.Value,
                        request.ExpectedItemRevision!.Value,
                        request.Quantity!.Value,
                        request.DestinationContainerId!.Value,
                        request.ExpectedDestinationContainerRevision!.Value,
                        request.DestinationSlotIndex!.Value,
                        NormalizeGuid(request.TargetItemInstanceId),
                        NormalizeRevision(
                            request.TargetItemInstanceId,
                            request.ExpectedTargetItemRevision))),
                cancellationToken);
        }

        if (string.Equals(
                operationKind,
                CorpseInteractionOperationKinds.DepositItem,
                StringComparison.Ordinal)
            && HasItemTransferShape(request, requiresPartialQuantity: false))
        {
            return TransactionService.ExecuteAsync(
                new ItemTransactionRequest<DepositCorpseItemCommand>(
                    request.OperationId,
                    actor,
                    new DepositCorpseItemCommand(
                        request.CharacterId,
                        corpseId,
                        request.ItemInstanceId!.Value,
                        request.ExpectedItemRevision!.Value,
                        request.DestinationContainerId!.Value,
                        request.ExpectedDestinationContainerRevision!.Value,
                        request.DestinationSlotIndex!.Value,
                        NormalizeGuid(request.TargetItemInstanceId),
                        NormalizeRevision(
                            request.TargetItemInstanceId,
                            request.ExpectedTargetItemRevision))),
                cancellationToken);
        }

        if (string.Equals(
                operationKind,
                CorpseInteractionOperationKinds.DepositPartialStack,
                StringComparison.Ordinal)
            && HasItemTransferShape(request, requiresPartialQuantity: true))
        {
            return TransactionService.ExecuteAsync(
                new ItemTransactionRequest<DepositCorpsePartialStackCommand>(
                    request.OperationId,
                    actor,
                    new DepositCorpsePartialStackCommand(
                        request.CharacterId,
                        corpseId,
                        request.ItemInstanceId!.Value,
                        request.ExpectedItemRevision!.Value,
                        request.Quantity!.Value,
                        request.DestinationContainerId!.Value,
                        request.ExpectedDestinationContainerRevision!.Value,
                        request.DestinationSlotIndex!.Value,
                        NormalizeGuid(request.TargetItemInstanceId),
                        NormalizeRevision(
                            request.TargetItemInstanceId,
                            request.ExpectedTargetItemRevision))),
                cancellationToken);
        }

        if (string.Equals(
                operationKind,
                CorpseInteractionOperationKinds.MoveItem,
                StringComparison.Ordinal)
            && HasItemTransferShape(request, requiresPartialQuantity: false))
        {
            return TransactionService.ExecuteAsync(
                new ItemTransactionRequest<MoveCorpseItemCommand>(
                    request.OperationId,
                    actor,
                    new MoveCorpseItemCommand(
                        request.CharacterId,
                        corpseId,
                        request.ItemInstanceId!.Value,
                        request.ExpectedItemRevision!.Value,
                        request.DestinationContainerId!.Value,
                        request.ExpectedDestinationContainerRevision!.Value,
                        request.DestinationSlotIndex!.Value,
                        NormalizeGuid(request.TargetItemInstanceId),
                        NormalizeRevision(
                            request.TargetItemInstanceId,
                            request.ExpectedTargetItemRevision))),
                cancellationToken);
        }

        if (string.Equals(
                operationKind,
                CorpseInteractionOperationKinds.MovePartialStack,
                StringComparison.Ordinal)
            && HasItemTransferShape(request, requiresPartialQuantity: true))
        {
            return TransactionService.ExecuteAsync(
                new ItemTransactionRequest<MoveCorpsePartialStackCommand>(
                    request.OperationId,
                    actor,
                    new MoveCorpsePartialStackCommand(
                        request.CharacterId,
                        corpseId,
                        request.ItemInstanceId!.Value,
                        request.ExpectedItemRevision!.Value,
                        request.Quantity!.Value,
                        request.DestinationContainerId!.Value,
                        request.ExpectedDestinationContainerRevision!.Value,
                        request.DestinationSlotIndex!.Value,
                        NormalizeGuid(request.TargetItemInstanceId),
                        NormalizeRevision(
                            request.TargetItemInstanceId,
                            request.ExpectedTargetItemRevision))),
                cancellationToken);
        }

        if (string.Equals(
                operationKind,
                CorpseInteractionOperationKinds.SwapBag,
                StringComparison.Ordinal)
            && HasBagSwapShape(request))
        {
            return TransactionService.ExecuteAsync(
                new ItemTransactionRequest<SwapCorpseBagCommand>(
                    request.OperationId,
                    actor,
                    new SwapCorpseBagCommand(
                        request.CharacterId,
                        corpseId,
                        request.ItemInstanceId!.Value,
                        request.ExpectedItemRevision!.Value,
                        request.CorpseBagContentsContainerId!.Value,
                        request.ExpectedCorpseBagContentsRevision!.Value,
                        request.PlayerBagItemInstanceId!.Value,
                        request.ExpectedPlayerBagRevision!.Value,
                        request.PlayerBagContentsContainerId!.Value,
                        request.ExpectedPlayerBagContentsRevision!.Value)),
                cancellationToken);
        }

        return null;
    }

    private async Task<CorpseViewSnapshotResponse?> LoadCurrentSnapshotAsync(
        Guid corpseId,
        string shardId,
        CancellationToken cancellationToken)
    {
        await using var connection = await DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "set transaction read only;",
            transaction: transaction,
            cancellationToken: cancellationToken));
        try
        {
            var result = await LoadSnapshotAsync(
                connection,
                transaction,
                corpseId,
                shardId,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result.Snapshot;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task<SnapshotError?> ValidateReadAuthorityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid simulationSessionId,
        SimulationCorpseOpenRequest request,
        CancellationToken cancellationToken)
    {
        var row = await connection.QuerySingleOrDefaultAsync<CorpseReadAuthorityRow>(
            new CommandDefinition(
                """
                select
                    session.account_id as "AccountId",
                    session.character_id as "CharacterId",
                    session.shard_id as "ShardId",
                    session.simulation_worker_id as "WorkerId",
                    session.worker_runtime_id as "WorkerRuntimeId",
                    session.session_token_hash as "SessionTokenHash",
                    session.released_at is null
                        and session.expires_at > now()
                        and (
                            session.account_session_id is null
                            or exists (
                                select 1
                                from account_sessions account_session
                                where account_session.id = session.account_session_id
                                  and account_session.revoked_at is null
                                  and account_session.expires_at > now())) as "SessionIsActive",
                    worker.is_online
                        and worker.last_heartbeat_at is not null
                        and worker.last_heartbeat_at > now()
                            - make_interval(secs => @HeartbeatTimeoutSeconds)
                        and worker.runtime_id = session.worker_runtime_id
                        as "WorkerHasFreshLease",
                    exists (
                        select 1
                        from simulation_assignments assignment
                        where assignment.worker_id = session.simulation_worker_id
                          and assignment.shard_id = session.shard_id
                          and assignment.released_at is null) as "WorkerHasAssignment"
                from character_simulation_sessions session
                join simulation_workers worker
                  on worker.id = session.simulation_worker_id
                where session.id = @SimulationSessionId;
                """,
                new
                {
                    SimulationSessionId = simulationSessionId,
                    HeartbeatTimeoutSeconds = ServiceConfig.SimulationWorkerHeartbeatTimeout.TotalSeconds
                },
                transaction,
                cancellationToken: cancellationToken));
        if (row is null || !row.SessionIsActive)
        {
            return new SnapshotError(
                ItemTransactionErrorCodes.SimulationSessionInvalid,
                "The simulation session is invalid, released, or expired.");
        }

        if (row.AccountId != request.AccountId
            || row.CharacterId != request.CharacterId
            || !string.Equals(row.ShardId, request.ShardId, StringComparison.Ordinal)
            || !string.Equals(row.WorkerId, request.WorkerId, StringComparison.Ordinal)
            || !HashesMatch(
                row.SessionTokenHash,
                TokenGenerator.HashToken(request.SessionToken.Trim())))
        {
            return new SnapshotError(
                ItemTransactionErrorCodes.SimulationSessionInvalid,
                "The corpse view credentials do not match the active simulation session.");
        }

        if (!string.Equals(
                row.WorkerRuntimeId,
                request.WorkerRuntimeId,
                StringComparison.Ordinal)
            || !row.WorkerHasFreshLease)
        {
            return new SnapshotError(
                ItemTransactionErrorCodes.WorkerRuntimeChanged,
                "The simulation worker runtime no longer owns a fresh registry lease.");
        }

        return row.WorkerHasAssignment
            ? null
            : new SnapshotError(
                ItemTransactionErrorCodes.WrongSimulationWorker,
                "The simulation worker no longer owns the requested Shard assignment.");
    }

    private static async Task<SnapshotLoadResult> LoadSnapshotAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid corpseId,
        string shardId,
        CancellationToken cancellationToken)
    {
        var corpse = await connection.QuerySingleOrDefaultAsync<CorpseViewRow>(
            new CommandDefinition(
                """
                select
                    id as "CorpseId",
                    source_character_id as "SourceCharacterId",
                    source_display_name as "SourceDisplayName",
                    shard_id as "ShardId",
                    position_x as "PositionX",
                    position_y as "PositionY",
                    position_z as "PositionZ",
                    presentation_key as "PresentationKey",
                    revision as "Revision",
                    created_at as "CreatedAt",
                    expires_at as "ExpiresAt",
                    closed_at as "ClosedAt",
                    close_reason as "CloseReason",
                    expires_at <= now() as "IsExpired"
                from corpses
                where id = @CorpseId;
                """,
                new { CorpseId = corpseId },
                transaction,
                cancellationToken: cancellationToken));
        if (corpse is null)
        {
            return SnapshotLoadResult.Failed(
                ItemTransactionErrorCodes.CorpseNotFound,
                "The corpse was not found.");
        }

        if (!string.Equals(corpse.ShardId, shardId, StringComparison.Ordinal))
        {
            return SnapshotLoadResult.Failed(
                ItemTransactionErrorCodes.WrongSimulationWorker,
                "The corpse belongs to another Shard.");
        }

        if (corpse.IsExpired
            || string.Equals(corpse.CloseReason, "expired", StringComparison.Ordinal))
        {
            return SnapshotLoadResult.Failed(
                ItemTransactionErrorCodes.CorpseExpired,
                "The corpse has reached its absolute expiry time.");
        }

        if (corpse.ClosedAt is not null)
        {
            return SnapshotLoadResult.Failed(
                ItemTransactionErrorCodes.CorpseInvalidated,
                "The corpse is no longer available for interaction.");
        }

        var rows = (await connection.QueryAsync<CorpseViewSlotRow>(new CommandDefinition(
            """
            select
                section.section_kind as "SectionKind",
                container.id as "ContainerId",
                container.container_type as "ContainerType",
                container.revision as "ContainerRevision",
                container.slot_capacity as "SlotCapacity",
                slot.slot_index as "SlotIndex",
                slot.slot_kind as "SlotKind",
                coalesce(equipment_slot.id, '') as "EquipmentSlotId",
                tag.tag_id as "AcceptedTag",
                item.id as "ItemInstanceId",
                item.definition_id as "DefinitionId",
                item.quantity as "Quantity",
                item.revision as "ItemRevision",
                bag_container.id as "BagContentsContainerId",
                bag_container.revision as "BagContentsRevision"
            from corpse_sections section
            join item_containers container on container.id = section.container_id
            join item_container_slots slot on slot.container_id = container.id
            left join equipment_slots equipment_slot
              on section.section_kind = 'equipment'
             and equipment_slot.sort_order = slot.slot_index
            left join item_container_slot_tags tag
              on tag.container_id = slot.container_id
             and tag.slot_index = slot.slot_index
            left join item_instances item
              on item.container_id = slot.container_id
             and item.container_slot_index = slot.slot_index
            left join item_containers bag_container
              on bag_container.bound_bag_item_instance_id = item.id
             and bag_container.container_type = 'bag_contents'
            where section.corpse_id = @CorpseId
            order by
                case section.section_kind
                    when 'general_inventory' then 0
                    when 'equipment' then 1
                    else 2
                end,
                slot.slot_index,
                tag.tag_id;
            """,
            new { CorpseId = corpseId },
            transaction,
            cancellationToken: cancellationToken))).ToArray();
        var sections = rows
            .GroupBy(row => new
            {
                row.SectionKind,
                row.ContainerId,
                row.ContainerType,
                row.ContainerRevision,
                row.SlotCapacity
            })
            .Select(group => new CorpseViewSectionResponse(
                group.Key.SectionKind,
                group.Key.ContainerId,
                group.Key.ContainerType,
                group.Key.ContainerRevision,
                group.Key.SlotCapacity,
                group.GroupBy(row => new
                {
                    row.SlotIndex,
                    row.SlotKind,
                    row.EquipmentSlotId,
                    row.ItemInstanceId,
                    row.DefinitionId,
                    row.Quantity,
                    row.ItemRevision,
                    row.BagContentsContainerId,
                    row.BagContentsRevision
                })
                    .Select(slot => new CorpseViewSlotResponse(
                        slot.Key.SlotIndex,
                        slot.Key.SlotKind,
                        slot.Where(row => row.AcceptedTag is not null)
                            .Select(row => row.AcceptedTag!)
                            .Distinct(StringComparer.Ordinal)
                            .Order(StringComparer.Ordinal)
                            .ToArray(),
                        slot.Key.ItemInstanceId is null
                            ? null
                            : new CorpseViewItemResponse(
                                slot.Key.ItemInstanceId.Value,
                                slot.Key.DefinitionId!,
                                slot.Key.Quantity!.Value,
                                 slot.Key.ItemRevision!.Value,
                                 slot.Key.BagContentsContainerId,
                                 slot.Key.BagContentsRevision),
                        slot.Key.EquipmentSlotId))
                    .OrderBy(slot => slot.SlotIndex)
                    .ToArray()))
            .ToArray();
        if (sections.Length != 3
            || sections.Select(section => section.SectionKind).Distinct().Count() != 3)
        {
            throw new InvalidOperationException(
                $"Corpse '{corpseId}' does not have the canonical three-section view.");
        }

        var presentations = (await connection.QueryAsync<CorpsePresentationSnapshotResponse>(
            new CommandDefinition(
                """
                select
                    snapshot_kind as "SnapshotKind",
                    sort_order as "SortOrder",
                    definition_id as "DefinitionId",
                    equipment_slot_id as "EquipmentSlotId",
                    policy_kind as "PolicyKind",
                    presentation_payload::text as "PresentationPayload"
                from corpse_snapshots
                where corpse_id = @CorpseId
                order by sort_order, id;
                """,
                new { CorpseId = corpseId },
                transaction,
                cancellationToken: cancellationToken))).ToArray();
        return SnapshotLoadResult.Loaded(new CorpseViewSnapshotResponse(
            corpse.CorpseId,
            corpse.SourceCharacterId,
            corpse.SourceDisplayName,
            corpse.ShardId,
            corpse.PositionX,
            corpse.PositionY,
            corpse.PositionZ,
            corpse.PresentationKey,
            corpse.Revision,
            corpse.CreatedAt,
            corpse.ExpiresAt,
            sections,
            presentations));
    }

    private static SnapshotError? ValidateSimulationRequest(
        string authenticatedWorkerId,
        Guid simulationSessionId,
        Guid corpseId,
        Guid accountId,
        Guid characterId,
        string? workerId,
        string? workerRuntimeId,
        string? shardId,
        string? sessionToken)
    {
        if (!string.Equals(authenticatedWorkerId, workerId, StringComparison.Ordinal))
        {
            return new SnapshotError(
                ItemTransactionErrorCodes.WrongSimulationWorker,
                "The authenticated worker cannot submit another worker's corpse interaction.");
        }

        return simulationSessionId == Guid.Empty
            || corpseId == Guid.Empty
            || accountId == Guid.Empty
            || characterId == Guid.Empty
            || !IsValidIdentifier(workerId)
            || !IsValidIdentifier(workerRuntimeId)
            || !IsValidIdentifier(shardId)
            || string.IsNullOrWhiteSpace(sessionToken)
            || sessionToken.Length > 1024
                ? new SnapshotError(
                    ItemApiErrorCodes.SimulationOperationInvalid,
                    "The simulation corpse interaction is incomplete or invalid.")
                : null;
    }

    private static bool HasItemTransferShape(
        SimulationCorpseMutationRequest request,
        bool requiresPartialQuantity)
    {
        var hasTarget = NormalizeGuid(request.TargetItemInstanceId) is not null;
        return request.ItemInstanceId is not null
            && request.ItemInstanceId != Guid.Empty
            && request.ExpectedItemRevision >= 0
            && request.DestinationContainerId is not null
            && request.DestinationContainerId != Guid.Empty
            && request.ExpectedDestinationContainerRevision >= 0
            && request.DestinationSlotIndex >= 0
            && hasTarget == (request.ExpectedTargetItemRevision >= 0)
            && (requiresPartialQuantity
                ? request.Quantity > 0
                : request.Quantity is null or 0);
    }

    private static bool HasBagSwapShape(SimulationCorpseMutationRequest request)
    {
        return request.ItemInstanceId is not null
            && request.ItemInstanceId != Guid.Empty
            && request.ExpectedItemRevision >= 0
            && request.CorpseBagContentsContainerId is not null
            && request.CorpseBagContentsContainerId != Guid.Empty
            && request.ExpectedCorpseBagContentsRevision >= 0
            && request.PlayerBagItemInstanceId is not null
            && request.PlayerBagItemInstanceId != Guid.Empty
            && request.ExpectedPlayerBagRevision >= 0
            && request.PlayerBagContentsContainerId is not null
            && request.PlayerBagContentsContainerId != Guid.Empty
            && request.ExpectedPlayerBagContentsRevision >= 0;
    }

    private static Guid? NormalizeGuid(Guid? value)
    {
        return value is null || value == Guid.Empty ? null : value;
    }

    private static long? NormalizeRevision(Guid? id, long? revision)
    {
        return NormalizeGuid(id) is null ? null : revision;
    }

    private static bool HashesMatch(string expected, string supplied)
    {
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        return expectedBytes.Length == suppliedBytes.Length
            && CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
    }

    private static ServiceResult<CorpseViewSnapshotResponse> MapSnapshotError(
        SnapshotError error)
    {
        return error.Code switch
        {
            ItemTransactionErrorCodes.SimulationSessionInvalid =>
                ServiceResult<CorpseViewSnapshotResponse>.Unauthorized(
                    error.Code,
                    error.Message),
            ItemTransactionErrorCodes.WrongSimulationWorker =>
                ServiceResult<CorpseViewSnapshotResponse>.Forbidden(
                    error.Code,
                    error.Message),
            ItemTransactionErrorCodes.CorpseNotFound =>
                ServiceResult<CorpseViewSnapshotResponse>.NotFound(
                    error.Code,
                    error.Message),
            _ => ServiceResult<CorpseViewSnapshotResponse>.Conflict(
                error.Code,
                error.Message)
        };
    }

    private static ServiceResult<T> MapRequestValidation<T>(SnapshotError error)
    {
        return error.Code == ItemTransactionErrorCodes.WrongSimulationWorker
            ? ServiceResult<T>.Forbidden(error.Code, error.Message)
            : ServiceResult<T>.BadRequest(error.Code, error.Message);
    }

    private sealed record SnapshotError(string Code, string Message);

    private sealed record SnapshotLoadResult(
        CorpseViewSnapshotResponse? Snapshot,
        SnapshotError? Error)
    {
        public static SnapshotLoadResult Loaded(CorpseViewSnapshotResponse snapshot)
        {
            return new SnapshotLoadResult(snapshot, null);
        }

        public static SnapshotLoadResult Failed(string code, string message)
        {
            return new SnapshotLoadResult(null, new SnapshotError(code, message));
        }
    }

    private sealed class CorpseReadAuthorityRow
    {
        public Guid AccountId { get; set; }

        public Guid CharacterId { get; set; }

        public string ShardId { get; set; } = string.Empty;

        public string WorkerId { get; set; } = string.Empty;

        public string WorkerRuntimeId { get; set; } = string.Empty;

        public string SessionTokenHash { get; set; } = string.Empty;

        public bool SessionIsActive { get; set; }

        public bool WorkerHasFreshLease { get; set; }

        public bool WorkerHasAssignment { get; set; }
    }

    private sealed class CorpseViewRow
    {
        public Guid CorpseId { get; set; }

        public Guid? SourceCharacterId { get; set; }

        public string SourceDisplayName { get; set; } = string.Empty;

        public string ShardId { get; set; } = string.Empty;

        public double PositionX { get; set; }

        public double PositionY { get; set; }

        public double PositionZ { get; set; }

        public string PresentationKey { get; set; } = string.Empty;

        public long Revision { get; set; }

        public DateTime CreatedAt { get; set; }

        public DateTime ExpiresAt { get; set; }

        public DateTime? ClosedAt { get; set; }

        public string? CloseReason { get; set; }

        public bool IsExpired { get; set; }
    }

    private sealed class CorpseViewSlotRow
    {
        public string SectionKind { get; set; } = string.Empty;

        public Guid ContainerId { get; set; }

        public string ContainerType { get; set; } = string.Empty;

        public long ContainerRevision { get; set; }

        public int SlotCapacity { get; set; }

        public int SlotIndex { get; set; }

        public string SlotKind { get; set; } = string.Empty;

        public string EquipmentSlotId { get; set; } = string.Empty;

        public string? AcceptedTag { get; set; }

        public Guid? ItemInstanceId { get; set; }

        public string? DefinitionId { get; set; }

        public int? Quantity { get; set; }

        public long? ItemRevision { get; set; }

        public Guid? BagContentsContainerId { get; set; }

        public long? BagContentsRevision { get; set; }
    }
}
