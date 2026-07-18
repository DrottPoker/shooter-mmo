using System.Security.Cryptography;
using System.Text;
using ShooterMmo.WorldData.Actors;
using SimulationWorker.Auth;
using SimulationWorker.Config;
using SimulationWorker.Registry;
using SimulationWorker.WorldActors;

namespace SimulationWorker.Corpses;

public sealed record MobCorpseLootSeed(string DefinitionId, int Quantity);

public sealed record CreateMobCorpseRequest(
    Guid DeathEventId,
    Guid SourceActorRuntimeId,
    IReadOnlyList<MobCorpseLootSeed> Loot);

public sealed record MobCorpseCreationResult(
    LiveMobCorpseState? LiveCorpse,
    DurableCorpseResponse? DurableCorpse,
    SimulationWorkerErrorResponse? Error)
{
    public bool Succeeded => Error is null;

    public static MobCorpseCreationResult Live(LiveMobCorpseState corpse)
    {
        return new MobCorpseCreationResult(corpse, null, null);
    }

    public static MobCorpseCreationResult Durable(DurableCorpseResponse corpse)
    {
        return new MobCorpseCreationResult(null, corpse, null);
    }

    public static MobCorpseCreationResult Rejected(SimulationWorkerErrorResponse error)
    {
        return new MobCorpseCreationResult(null, null, error);
    }
}

public sealed class MobCorpseLifecycleService(
    WorldActorStore actorStore,
    LiveMobCorpseStore liveCorpseStore,
    DurableCorpseStore durableCorpseStore,
    AuthServiceClient authServiceClient,
    SimulationWorkerConfig config,
    SimulationWorkerIdentity identity,
    TimeProvider timeProvider)
{
    public const string DefaultPresentationKey = "corpse.generic_loot_crate";

    public async Task<MobCorpseCreationResult> CreateAsync(
        CreateMobCorpseRequest request,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        if (!actorStore.TryGetByRuntimeId(request.SourceActorRuntimeId, out var actor)
            || actor is null
            || !actor.IsActive
            || !string.Equals(actor.Definition.Kind, WorldActorKindIds.Mob, StringComparison.Ordinal))
        {
            return MobCorpseCreationResult.Rejected(new SimulationWorkerErrorResponse(
                "mob_corpse_source_invalid",
                "The Mob corpse source is not an active authoritative Mob actor."));
        }

        if (!string.Equals(actor.ShardId, config.ShardId, StringComparison.Ordinal)
            || !string.Equals(actor.WorkerRuntimeId, identity.RuntimeId, StringComparison.Ordinal))
        {
            return MobCorpseCreationResult.Rejected(new SimulationWorkerErrorResponse(
                "mob_corpse_authority_invalid",
                "The Mob corpse source is not owned by this worker runtime and Shard."));
        }

        var corpseId = CreateScopedGuid("mob-corpse", request.DeathEventId);
        var loot = request.Loot
            .Select((seed, index) => new ResolvedLoot(
                CreateIndexedScopedGuid("mob-loot-entry", request.DeathEventId, index),
                CreateIndexedScopedGuid("mob-loot-grant", request.DeathEventId, index),
                seed.DefinitionId.Trim(),
                seed.Quantity,
                index))
            .ToArray();
        var definition = actor.Definition;
        if (string.Equals(
                definition.CorpsePersistenceMode,
                WorldActorCorpsePersistenceModeIds.Live,
                StringComparison.Ordinal))
        {
            var createdAt = liveCorpseStore.TryGetActive(corpseId, out var existing)
                ? existing!.CreatedAt
                : timeProvider.GetUtcNow().UtcDateTime;
            var liveCorpse = liveCorpseStore.CreateOrGet(new LiveMobCorpseState(
                corpseId,
                actor.RuntimeActorId,
                definition.Id,
                definition.DisplayName,
                actor.ShardId,
                actor.PositionX,
                actor.PositionY,
                actor.PositionZ,
                DefaultPresentationKey,
                CreateScopedGuid("mob-corpse-general", corpseId),
                CreateScopedGuid("mob-corpse-equipment", corpseId),
                CreateScopedGuid("mob-corpse-bag", corpseId),
                1,
                createdAt,
                createdAt.AddSeconds(definition.CorpseLifetimeSeconds),
                loot.Select(value => new LiveMobCorpseLootEntry(
                    value.LootEntryId,
                    value.GrantId,
                    value.DefinitionId,
                    value.Quantity,
                    value.SlotIndex))
                    .ToArray()));
            return MobCorpseCreationResult.Live(liveCorpse);
        }

        if (!string.Equals(
                definition.CorpsePersistenceMode,
                WorldActorCorpsePersistenceModeIds.Durable,
                StringComparison.Ordinal))
        {
            return MobCorpseCreationResult.Rejected(new SimulationWorkerErrorResponse(
                "mob_corpse_persistence_invalid",
                "The Mob content does not define a supported corpse persistence mode."));
        }

        var halfYawRadians = actor.YawDegrees * Math.PI / 360d;
        var operationId = CreateScopedGuid("mob-corpse-create", request.DeathEventId);
        var durableResult = await authServiceClient.CreatePersistentMobCorpseAsync(
            new CreatePersistentMobCorpseRequest(
                operationId,
                corpseId,
                config.SimulationWorkerId,
                identity.RuntimeId,
                actor.ShardId,
                definition.Id,
                definition.DisplayName,
                actor.PositionX,
                actor.PositionY,
                actor.PositionZ,
                0d,
                Math.Sin(halfYawRadians),
                0d,
                Math.Cos(halfYawRadians),
                DefaultPresentationKey,
                definition.CorpseLifetimeSeconds,
                loot.Select(value => new MobCorpseLootEntryRequest(
                    value.GrantId,
                    value.DefinitionId,
                    value.Quantity))
                    .ToArray()),
            cancellationToken);
        if (!durableResult.Succeeded)
        {
            return MobCorpseCreationResult.Rejected(durableResult.Error!);
        }

        durableCorpseStore.Apply(durableResult.Value!.Corpse);
        return MobCorpseCreationResult.Durable(durableResult.Value.Corpse);
    }

    private static void ValidateRequest(CreateMobCorpseRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.DeathEventId == Guid.Empty
            || request.SourceActorRuntimeId == Guid.Empty
            || request.Loot is null
            || request.Loot.Count is < 1 or > WorldActorCorpseRules.MaximumLootEntries
            || request.Loot.Any(seed =>
                !IsContentIdentifier(seed.DefinitionId)
                || seed.Quantity <= 0))
        {
            throw new ArgumentException(
                "The Mob corpse creation request is incomplete or invalid.",
                nameof(request));
        }
    }

    private static bool IsContentIdentifier(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Length <= 128
            && string.Equals(value, value.Trim(), StringComparison.Ordinal)
            && value.All(character =>
                char.IsAsciiLetterOrDigit(character)
                || character is '-' or '_' or '.');
    }

    private static Guid CreateIndexedScopedGuid(string scope, Guid sourceId, int index)
    {
        return CreateScopedGuid(scope + ":" + index, sourceId);
    }

    private static Guid CreateScopedGuid(string scope, Guid sourceId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(
            scope + ":" + sourceId.ToString("N")));
        var bytes = hash[..16];
        bytes[7] = (byte)((bytes[7] & 0x0f) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new Guid(bytes);
    }

    private sealed record ResolvedLoot(
        Guid LootEntryId,
        Guid GrantId,
        string DefinitionId,
        int Quantity,
        int SlotIndex);
}
