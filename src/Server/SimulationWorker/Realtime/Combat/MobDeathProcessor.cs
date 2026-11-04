using Microsoft.Extensions.Logging;
using Mmorpg.Server.Common.Identity;
using Mmorpg.SimulationWorker.Combat;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.Corpses;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.WorldActors;
using Mmorpg.WorldData.Actors;

namespace Mmorpg.SimulationWorker.Realtime;

internal sealed record MobCorpseCreated(MobDeath Death, MobDeathCorpse Corpse, MobCorpseCreationResult Result)
    : RealtimeOperationResult(RealtimePeerContext.System);

// One corpse that a Mob death leaves: the loot of one credited player, or loot for everyone.
internal sealed class MobDeathCorpse(
    Guid corpseId,
    Guid? ownerCharacterId,
    IReadOnlyList<MobCorpseLootSeed> loot)
{
    public Guid CorpseId { get; } = corpseId;

    public Guid? OwnerCharacterId { get; } = ownerCharacterId;

    public IReadOnlyList<MobCorpseLootSeed> Loot { get; } = loot;

    public int Attempts { get; set; }

    public uint? RetryAtTick { get; set; }

    // Created, or never to be created.
    public bool Finished { get; set; }
}

// One Mob death between its lethal hit and its despawn.
internal sealed class MobDeath(
    Combatant combatant,
    WorldActorRuntimeState actor,
    Guid deathEventId,
    IReadOnlyList<MobKillCredit> credits,
    IReadOnlyList<MobDeathCorpse> corpses)
{
    public Combatant Combatant { get; } = combatant;

    public WorldActorRuntimeState Actor { get; } = actor;

    public Guid DeathEventId { get; } = deathEventId;

    // The players the Mob credits with its kill.
    public IReadOnlyList<MobKillCredit> Credits { get; } = credits;

    // Every corpse of the death shares it, so every viewer sees one body.
    public Guid LootGroupId => MobDeathProcessor.LootGroupIdOf(DeathEventId);

    public IReadOnlyList<MobDeathCorpse> Corpses { get; } = corpses;

    public bool IsFinished => Corpses.All(corpse => corpse.Finished);
}

// Turns a dead Mob into its corpses and its respawn. A tagged Mob leaves one corpse for the
// player it credits, a world boss with contributor credit one for every credited player, each
// with a roll of its own, and a Mob that credits no one one for everyone. A corpse is live on
// the worker or durable in GameService, which retries with the same corpse id until it
// commits. Once every corpse exists, the actor despawns, and its spawn instance respawns after
// the respawn profile's delay. A death whose rolls are empty leaves no corpse. Loop-confined;
// corpse creation runs off the loop.
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

    // The corpse of a death that leaves one, and the id that every corpse of the death shares.
    public static Guid LootGroupIdOf(Guid deathEventId)
    {
        return ScopedGuid.Create("mob-corpse", deathEventId);
    }

    public void Begin(Combatant combatant, IReadOnlyList<MobKillCredit> credits)
    {
        var actor = combatant.Actor!;
        if (deaths.ContainsKey(actor.RuntimeActorId))
        {
            return;
        }

        var deathEventId = ScopedGuid.Create("mob-death", actor.RuntimeActorId);
        var death = new MobDeath(
            combatant,
            actor,
            deathEventId,
            credits,
            PlanCorpses(actor, deathEventId, credits));
        if (death.Corpses.Count == 0 || corpses is null)
        {
            // Viewers receive the lethal hit and the death before the despawn.
            despawnsAfterFlush.Add(death);
            return;
        }

        deaths.Add(actor.RuntimeActorId, death);
        foreach (var corpse in death.Corpses)
        {
            Create(death, corpse);
        }
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
            foreach (var corpse in death.Corpses)
            {
                if (!corpse.Finished
                    && corpse.RetryAtTick is { } retry
                    && CombatTicks.IsReached(clock.ServerTick, retry))
                {
                    corpse.RetryAtTick = null;
                    Create(death, corpse);
                }
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

        var corpse = created.Corpse;
        if (created.Result.Succeeded)
        {
            corpse.Finished = true;
        }
        else
        {
            var error = created.Result.Error!;
            logger.LogWarning(
                "[SIMULATION] Corpse {CorpseId} of Mob {ActorDefinitionId} ({RuntimeActorId}) was not created on attempt {Attempt}: {Code}.",
                corpse.CorpseId,
                death.Actor.Definition.Id,
                death.Actor.RuntimeActorId,
                corpse.Attempts,
                error.Code);
            if (error.Code is "mob_corpse_source_invalid" or "mob_corpse_authority_invalid" or "mob_corpse_persistence_invalid")
            {
                // The Mob can never get this corpse, so it leaves without it.
                corpse.Finished = true;
            }
            else
            {
                corpse.RetryAtTick = clock.ServerTick + Backoff(corpse.Attempts);
            }
        }

        if (death.IsFinished)
        {
            deaths.Remove(death.Actor.RuntimeActorId);
            Despawn(death);
        }
    }

    public void Clear()
    {
        deaths.Clear();
        despawnsAfterFlush.Clear();
        pendingRespawns.Clear();
    }

    // A tagged Mob, and a Mob that credits no one, leave one corpse with the id of the death's
    // loot group. A world boss with contributor credit leaves one for every credited player, in
    // character id order, so the rolls are reproducible.
    private IReadOnlyList<MobDeathCorpse> PlanCorpses(
        WorldActorRuntimeState actor,
        Guid deathEventId,
        IReadOnlyList<MobKillCredit> credits)
    {
        var lootProfile = string.IsNullOrEmpty(actor.Definition.LootProfileId)
            ? null
            : loot.GetValueOrDefault(actor.Definition.LootProfileId);
        var personal = credits.Count > 0
            && string.Equals(actor.Definition.KillCredit, WorldActorKillCreditIds.Contributors, StringComparison.Ordinal);
        if (!personal)
        {
            var shared = MobLootRoller.Roll(lootProfile, random);
            return shared.Count == 0
                ? []
                : [new MobDeathCorpse(
                    LootGroupIdOf(deathEventId),
                    credits.Count == 1 ? credits[0].CharacterId : null,
                    shared)];
        }

        var planned = new List<MobDeathCorpse>(credits.Count);
        foreach (var credit in credits)
        {
            var own = MobLootRoller.Roll(lootProfile, random);
            if (own.Count > 0)
            {
                planned.Add(new MobDeathCorpse(
                    ScopedGuid.Create("mob-corpse:" + credit.CharacterId.ToString("N"), deathEventId),
                    credit.CharacterId,
                    own));
            }
        }

        return planned;
    }

    private void Create(MobDeath death, MobDeathCorpse corpse)
    {
        corpse.Attempts++;
        operationTracker.Track(CreateAsync(death, corpse));
    }

    private async Task CreateAsync(MobDeath death, MobDeathCorpse corpse)
    {
        MobCorpseCreationResult result;
        try
        {
            result = await corpses!.CreateAsync(
                new CreateMobCorpseRequest(
                    death.DeathEventId,
                    corpse.CorpseId,
                    death.LootGroupId,
                    death.Actor.RuntimeActorId,
                    corpse.Loot,
                    corpse.OwnerCharacterId),
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

        completions.Post(this, new MobCorpseCreated(death, corpse, result));
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
