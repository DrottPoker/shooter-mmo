using Dapper;

namespace Mmorpg.GameService.Items.Transactions.Domain;

// The durable corpse row shared by player death and persistent Mob corpses, and the
// advisory lock that serializes work on one death event or corpse id.
internal static class DurableCorpseRecords
{
    public static Task<CreatedCorpseTimes> CreateDurableCorpseRecordAsync(
        ItemTransactionContext context,
        Guid corpseId,
        string sourceType,
        Guid? sourceCharacterId,
        string? sourceNpcDefinitionId,
        string sourceDisplayName,
        string shardId,
        double positionX,
        double positionY,
        double positionZ,
        double rotationX,
        double rotationY,
        double rotationZ,
        double rotationW,
        string presentationKey,
        long initialRevision,
        double lifetimeSeconds,
        Guid expiryOperationId,
        CancellationToken cancellationToken)
    {
        return context.Connection.QuerySingleAsync<CreatedCorpseTimes>(
            new CommandDefinition(
                """
                insert into corpses (
                    id,
                    source_type,
                    source_character_id,
                    source_npc_definition_id,
                    source_display_name,
                    shard_id,
                    position_x,
                    position_y,
                    position_z,
                    rotation_x,
                    rotation_y,
                    rotation_z,
                    rotation_w,
                    persistence_mode,
                    presentation_key,
                    revision,
                    expires_at,
                    expiry_operation_id)
                values (
                    @CorpseId,
                    @SourceType,
                    @SourceCharacterId,
                    @SourceNpcDefinitionId,
                    @SourceDisplayName,
                    @ShardId,
                    @PositionX,
                    @PositionY,
                    @PositionZ,
                    @RotationX,
                    @RotationY,
                    @RotationZ,
                    @RotationW,
                    'durable',
                    @PresentationKey,
                    @InitialRevision,
                    now() + make_interval(secs => @LifetimeSeconds),
                    @ExpiryOperationId)
                returning
                    created_at as "CreatedAt",
                    expires_at as "ExpiresAt";
                """,
                new
                {
                    CorpseId = corpseId,
                    SourceType = sourceType,
                    SourceCharacterId = sourceCharacterId,
                    SourceNpcDefinitionId = sourceNpcDefinitionId,
                    SourceDisplayName = sourceDisplayName,
                    ShardId = shardId,
                    PositionX = positionX,
                    PositionY = positionY,
                    PositionZ = positionZ,
                    RotationX = rotationX,
                    RotationY = rotationY,
                    RotationZ = rotationZ,
                    RotationW = rotationW,
                    PresentationKey = presentationKey,
                    InitialRevision = initialRevision,
                    LifetimeSeconds = lifetimeSeconds,
                    ExpiryOperationId = expiryOperationId
                },
                context.Transaction,
                cancellationToken: cancellationToken));
    }

    public static async Task LockDeathEventKeyAsync(
        ItemTransactionContext context,
        Guid deathEventId,
        CancellationToken cancellationToken)
    {
        var (firstKey, secondKey) = DeathEventLockKeys(deathEventId);
        await context.Connection.ExecuteAsync(new CommandDefinition(
            "select pg_advisory_xact_lock(@FirstKey, @SecondKey);",
            new { FirstKey = firstKey, SecondKey = secondKey },
            context.Transaction,
            cancellationToken: cancellationToken));
    }

    // The advisory lock key pair of a death event id or a durable corpse id.
    public static (int FirstKey, int SecondKey) DeathEventLockKeys(Guid deathEventId)
    {
        var bytes = deathEventId.ToByteArray();
        var firstKey = BitConverter.ToInt32(bytes, 0) ^ 0x44_45_41_54;
        var secondKey = BitConverter.ToInt32(bytes, 4) ^ BitConverter.ToInt32(bytes, 8);
        return (firstKey, secondKey);
    }
}

internal sealed class CreatedCorpseTimes
{
    public DateTime CreatedAt { get; set; }

    public DateTime ExpiresAt { get; set; }
}
