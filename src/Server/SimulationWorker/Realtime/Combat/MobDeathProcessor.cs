using Microsoft.Extensions.Logging;
using Mmorpg.Server.Common.Identity;
using Mmorpg.SimulationWorker.Combat;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.Corpses;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.WorldActors;
using Mmorpg.WorldData.Actors;

namespace Mmorpg.SimulationWorker.Realtime;

internal sealed record MobCorpseCreated(MobDeath Death, MobCorpseCreationResult Result)
    : RealtimeOperationResult(RealtimePeerContext.System);

// One Mob death between its lethal hit and its despawn.
internal sealed class MobDeath(
    Combatant combatant,
    WorldActorRuntimeState actor,
    Guid deathEventId,
    IReadOnlyList<MobCorpseLootSeed> loot)
{
    public Combatant Combatant { get; } = combatant;

    public WorldActorRuntimeState Actor { get; } = actor;

    public Guid DeathEventId { get; } = deathEventId;

    public IReadOnlyList<MobCorpseLootSeed> Loot { get; } = loot;

    public int Attempts { get; set; }

    public uint? RetryAtTick { get; set; }
}

// Turns a dead Mob into its corpse and its respawn: the loot profile is rolled, the
// corpse is created with that loot (a live corpse on the worker or a durable one in
// GameService, which retries with the same death event id until it commits), the actor
// despawns, and its spawn instance respawns after the respawn profile's delay. A Mob
// whose roll is empty leaves no corpse. Loop-confined; corpse creation runs off the loop.
internal sealed class MobDeathProcessor(
    SimulationWorkerConfig config,
    RealtimeSimulationClock clock,
    WorldActorStore? worldActorStore,
    WorldActorTimerQueue timers,
    IReadOnlyList<WorldActorLootProfileDefinition> lootProfiles,
    IReadOnlyList<WorldActorRespawnProfileDefinition> respawnProfiles,
    CombatantRegistry combatants,
    EntityPresenceReplicator presence,
    MobCorpseLifecycleService? corpses,
    RealtimeCompletionQueue completions,
    RealtimeOperationTracker operationTracker,
    ICombatRandom random,
    ILogger logger) : IRealtimeCompletionHandler
{
    private readonly Dictionary<string, WorldActorLootProfileDefinition> loot = lootProfiles
        .ToDictionary(profile => profile.Id, StringComparer.Ordinal);
    private readonly Dictionary<string, WorldActorRespawnProfileDefinition> respawn = respawnProfiles
        .ToDictionary(profile => profile.Id, StringComparer.Ordinal);
    private readonly Dictionary<Guid, MobDeath> deaths = [];
    private readonly Dictionary<Guid, WorldActorRuntimeState> pendingRespawns = [];
    private readonly List<MobDeath> deathBuffer = [];
    private readonly List<MobDeath> despawnsAfterFlush = [];

    // Hears every Mob that respawned, so combat and presence add it.
    public Action<WorldActorRuntimeState>? Respawned { get; set; }

    // Hears every Mob that left the world, so its brain stops.
    public Action<ulong>? Despawned { get; set; }

    public int PendingDeaths => deaths.Count;

    public void Begin(Combatant combatant, SimulationDeathKillerRequest? killer)
    {
        var actor = combatant.Actor!;
        if (deaths.ContainsKey(actor.RuntimeActorId))
        {
            return;
        }

        var lootProfile = string.IsNullOrEmpty(actor.Definition.LootProfileId)
            ? null
            : loot.GetValueOrDefault(actor.Definition.LootProfileId);
        var death = new MobDeath(
            combatant,
            actor,
            ScopedGuid.Create("mob-death", actor.RuntimeActorId),
            MobLootRoller.Roll(lootProfile, random));
        if (death.Loot.Count == 0 || corpses is null)
        {
            // Viewers receive the lethal hit and the death before the despawn.
            despawnsAfterFlush.Add(death);
            return;
        }

        deaths.Add(actor.RuntimeActorId, death);
        Create(death);
    }

    // Retries durable corpses that did not commit yet.
    public void ProcessTick()
    {
        if (deaths.Count == 0)
        {
            return;
        }

        deathBuffer.Clear();
        deathBuffer.AddRange(deaths.Values);
        foreach (var death in deathBuffer)
        {
            if (death.RetryAtTick is { } retry && CombatTicks.IsReached(clock.ServerTick, retry))
            {
                death.RetryAtTick = null;
                Create(death);
            }
        }
    }

    // Despawns the Mobs without a corpse that died this tick, after the tick's combat
    // state and events reached their viewers.
    public void DespawnAfterFlush()
    {
        foreach (var death in despawnsAfterFlush)
        {
            Despawn(death);
        }

        despawnsAfterFlush.Clear();
    }

    public void Respawn(WorldActorTimer timer)
    {
        if (worldActorStore is null || !pendingRespawns.Remove(timer.RuntimeActorId, out var previous))
        {
            return;
        }

        var actor = worldActorStore.Respawn(previous);
        Respawned?.Invoke(actor);
    }

    public void Complete(RealtimeOperationResult completion)
    {
        if (completion is not MobCorpseCreated created
            || !deaths.TryGetValue(created.Death.Actor.RuntimeActorId, out var death)
            || !ReferenceEquals(death, created.Death))
        {
            return;
        }

        if (created.Result.Succeeded)
        {
            deaths.Remove(death.Actor.RuntimeActorId);
            Despawn(death);
            return;
        }

        var error = created.Result.Error!;
        logger.LogWarning(
            "[SIMULATION] The corpse of Mob {ActorDefinitionId} ({RuntimeActorId}) was not created on attempt {Attempt}: {Code}.",
            death.Actor.Definition.Id,
            death.Actor.RuntimeActorId,
            death.Attempts,
            error.Code);
        if (error.Code is "mob_corpse_source_invalid" or "mob_corpse_authority_invalid" or "mob_corpse_persistence_invalid")
        {
            // The Mob can never get this corpse, so it leaves without one.
            deaths.Remove(death.Actor.RuntimeActorId);
            Despawn(death);
            return;
        }

        death.RetryAtTick = clock.ServerTick + Backoff(death.Attempts);
    }

    public void Clear()
    {
        deaths.Clear();
        despawnsAfterFlush.Clear();
        pendingRespawns.Clear();
    }

    private void Create(MobDeath death)
    {
        death.Attempts++;
        operationTracker.Track(CreateAsync(death));
    }

    private async Task CreateAsync(MobDeath death)
    {
        MobCorpseCreationResult result;
        try
        {
            result = await corpses!.CreateAsync(
                new CreateMobCorpseRequest(death.DeathEventId, death.Actor.RuntimeActorId, death.Loot),
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "[SIMULATION] Unhandled error while creating the corpse of Mob {RuntimeActorId}.",
                death.Actor.RuntimeActorId);
            result = MobCorpseCreationResult.Rejected(new SimulationWorkerErrorResponse(
                "mob_corpse_creation_failed",
                "SimulationWorker could not create the Mob corpse."));
        }

        completions.Post(this, new MobCorpseCreated(death, result));
    }

    // The actor leaves every view, and its spawn instance returns after the delay.
    private void Despawn(MobDeath death)
    {
        var actor = death.Actor;
        combatants.Remove(actor.NetworkEntityId, out _);
        Despawned?.Invoke(actor.NetworkEntityId);
        if (worldActorStore is null || !worldActorStore.TryDespawn(actor.NetworkEntityId, out _))
        {
            return;
        }

        presence.BroadcastWorldActorDespawn(actor, "died");
        if (string.IsNullOrEmpty(actor.Definition.RespawnProfileId)
            || !respawn.TryGetValue(actor.Definition.RespawnProfileId, out var profile))
        {
            return;
        }

        pendingRespawns[actor.RuntimeActorId] = actor;
        timers.Schedule(
            clock.ServerTick + Math.Max(1u, CombatTicks.FromSeconds(profile.DelaySeconds, config.MovementSimulation.TickRateHz)),
            actor.RuntimeActorId,
            0,
            WorldActorTimerKind.MobRespawn);
    }

    // 0.5 s, doubling up to 10 s.
    private uint Backoff(int attempts)
    {
        var seconds = Math.Min(10d, 0.5d * Math.Pow(2d, Math.Max(0, attempts - 1)));
        return Math.Max(1u, (uint)Math.Ceiling(seconds * config.MovementSimulation.TickRateHz));
    }
}
