using System.Diagnostics;
using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.Combat;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.Corpses;
using Mmorpg.SimulationWorker.Entities;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Items;
using Mmorpg.SimulationWorker.Sessions;
using Mmorpg.SimulationWorker.WorldActors;
using Mmorpg.WorldData.Actors;

namespace Mmorpg.SimulationWorker.Realtime;

// Owns target-based combat on the realtime loop. A character picks a target once and
// attacks on its weapon's cooldown whenever it stands still in range with line of sight,
// a weapon, and matching arrows. Arrows fly and hit at their impact tick; damage that
// reaches 0 kills, a dead character respawns after the respawn delay at the World spawn,
// and a character out of combat regenerates. Mobs are targets: they take damage, die,
// leave loot, and respawn. Every change reaches clients at the end of the tick.
// Loop-confined.
internal sealed class CombatSystem : IPlayerEntityRemovalObserver, IOwnerAmmunitionSource
{
    private readonly SimulationWorkerConfig config;
    private readonly CombatRules rules;
    private readonly RealtimeSimulationClock clock;
    private readonly RealtimePeerRegistry peers;
    private readonly RealtimePeerSender sender;
    private readonly CharacterItemStateStore itemStateStore;
    private readonly SimulationEntityRegistry entityRegistry;
    private readonly ConnectionEntityBindingRegistry connectionBindings;
    private readonly EntityPresenceReplicator presence;
    private readonly WorldActorStore? worldActorStore;
    private readonly CombatantRegistry combatants;
    private readonly CombatReplicator replicator;
    private readonly PlayerCombatAuthority authority;
    private readonly MobDeathProcessor mobDeaths;
    private readonly CombatItemCatalog itemCatalog;
    private readonly CombatLineOfSight lineOfSight;
    private readonly ICombatRandom random;
    private readonly SimulationShardRules shardRules;
    private readonly CorpseViewerRegistry corpseViewers;
    private readonly CorpseViewDistributor corpseViews;
    private readonly CombatMetrics? metrics;
    private readonly ICollisionWorld collisionWorld;
    private readonly PriorityQueue<Projectile, (uint ImpactTick, long Sequence)> projectiles = new();
    private readonly List<Combatant> combatantBuffer = [];
    private readonly List<Combatant> deaths = [];
    private readonly Dictionary<string, WorldActorCombatProfileDefinition> combatProfiles;
    private readonly uint combatTimeoutTicks;
    private readonly uint respawnDelayTicks;
    private readonly uint lockoutTicks;
    private long projectileSequence;

    public CombatSystem(
        SimulationWorkerConfig config,
        CombatRules rules,
        RealtimeSimulationClock clock,
        RealtimePeerRegistry peers,
        RealtimePeerSender sender,
        CharacterItemStateStore itemStateStore,
        SimulationEntityRegistry entityRegistry,
        ConnectionEntityBindingRegistry connectionBindings,
        EntityPresenceReplicator presence,
        WorldActorStore? worldActorStore,
        IReadOnlyList<WorldActorCombatProfileDefinition> combatProfiles,
        CombatantRegistry combatants,
        CombatReplicator replicator,
        PlayerCombatAuthority authority,
        MobDeathProcessor mobDeaths,
        CombatItemCatalog itemCatalog,
        CombatLineOfSight lineOfSight,
        ICombatRandom random,
        SimulationShardRules shardRules,
        CorpseViewerRegistry corpseViewers,
        CorpseViewDistributor corpseViews,
        ICollisionWorld collisionWorld,
        CombatMetrics? metrics)
    {
        this.collisionWorld = collisionWorld;
        this.config = config;
        this.rules = rules;
        this.clock = clock;
        this.peers = peers;
        this.sender = sender;
        this.itemStateStore = itemStateStore;
        this.entityRegistry = entityRegistry;
        this.connectionBindings = connectionBindings;
        this.presence = presence;
        this.worldActorStore = worldActorStore;
        this.combatants = combatants;
        this.replicator = replicator;
        this.authority = authority;
        this.mobDeaths = mobDeaths;
        this.itemCatalog = itemCatalog;
        this.lineOfSight = lineOfSight;
        this.random = random;
        this.shardRules = shardRules;
        this.corpseViewers = corpseViewers;
        this.corpseViews = corpseViews;
        this.metrics = metrics;
        this.combatProfiles = combatProfiles.ToDictionary(profile => profile.Id, StringComparer.Ordinal);
        var tickRate = config.MovementSimulation.TickRateHz;
        combatTimeoutTicks = CombatTicks.FromSeconds(rules.CombatTimeoutSeconds, tickRate);
        respawnDelayTicks = CombatTicks.FromSeconds(rules.RespawnDelaySeconds, tickRate);
        lockoutTicks = CombatTicks.FromSeconds(rules.EquipmentLockoutSeconds, tickRate);
        authority.OwnerStateChanged = OnOwnerStateChanged;
        replicator.Ammunition = this;
        mobDeaths.Respawned = OnMobRespawned;
    }

    // Closes the world interaction of a character that died; set by the composition.
    public Action<RealtimePeerContext, string, string>? CloseWorldInteraction { get; set; }

    public int CombatantCount => combatants.Count;

    // Every damageable Mob of the activated assignment becomes a combatant.
    public void RegisterWorldActors(IReadOnlyList<WorldActorRuntimeState> actors)
    {
        foreach (var actor in actors)
        {
            TryRegisterMob(actor);
        }

        metrics?.SetActiveCombatants(combatants.Count);
    }

    // A joining character becomes a combatant with full health; a reconnect keeps its state.
    // A synthetic bot has no durable character to die, so it is never a combatant.
    public void OnPlayerJoined(RealtimePeerContext context, PlayerSimulationEntity entity)
    {
        if (entity.Session.IsSyntheticBot)
        {
            return;
        }

        var combatSession = authority.Attach(context, entity.Session);
        if (!combatants.TryGet(entity.NetworkEntityId, out var combatant))
        {
            combatant = Combatant.ForPlayer(entity, rules.PlayerMaximumHealth);
            combatants.Add(combatant);
            metrics?.SetActiveCombatants(combatants.Count);
        }

        if (combatSession.Death is not null && combatant.IsAlive)
        {
            // The session's death survived a reconnect of the same session.
            MarkDead(combatant, 0);
        }

        // The owner state follows at the end of the next combat phase, after the baseline.
        combatant.MarkOwnerStateChanged();
    }

    public void OnPlayerEntityRemoved(RealtimePeerContext context, ActiveSimulationSession session, ulong entityId)
    {
        if (combatants.Remove(entityId, out _))
        {
            combatants.ClearTargetsOn(entityId);
            metrics?.SetActiveCombatants(combatants.Count);
        }

        authority.OnSessionEnded(session);
    }

    public bool IsMovementBlocked(PlayerSimulationEntity entity)
    {
        return combatants.TryGet(entity.NetworkEntityId, out var combatant) && !combatant.IsAlive;
    }

    public void HandleIntent(in RealtimeInboundMessage message)
    {
        var peer = message.Peer;
        var context = message.Context;
        if (!peers.TryGetBoundPlayer(context, out var player)
            || !combatants.TryGet(player.NetworkEntityId, out var attacker))
        {
            sender.RejectProtocol(peer, context, "combat_unavailable", "Combat is not active for this peer.");
            return;
        }

        if (!RealtimeProtocol.TryDecodeCombatIntent(message.Packet, out var intent, out var error))
        {
            sender.RejectProtocol(peer, context, RealtimeCombatErrorCodes.IntentInvalid, error);
            return;
        }

        if (!context.CombatIntentQuota.TryConsume(1d))
        {
            SendIntentResult(peer, intent, attacker, "combat_intent_rate_limited", "Combat intents exceeded the per-peer rate limit.");
            return;
        }

        if (!attacker.IsAlive)
        {
            SendIntentResult(peer, intent, attacker, RealtimeCombatErrorCodes.CharacterDead, "The character is dead.");
            return;
        }

        if (intent.Kind == RealtimeCombatIntentKind.ClearTarget)
        {
            SetTarget(attacker, 0);
            SendIntentResult(peer, intent, attacker, null, null);
            return;
        }

        var rejection = ValidateTarget(context, attacker, intent.TargetEntityId);
        if (rejection is not null)
        {
            SendIntentResult(peer, intent, attacker, rejection.Value.Code, rejection.Value.Message);
            return;
        }

        SetTarget(attacker, intent.TargetEntityId);
        SendIntentResult(peer, intent, attacker, null, null);
    }

    // The combat phase of a tick, after movement and world actor activity.
    public void ProcessTick()
    {
        var phaseStarted = Stopwatch.GetTimestamp();
        var serverTick = clock.ServerTick;
        authority.ProcessTick();
        mobDeaths.ProcessTick();
        ResolveDueImpacts(serverTick);
        combatants.CopyTo(combatantBuffer);
        foreach (var combatant in combatantBuffer)
        {
            if (combatant.Kind == CombatantKind.Player)
            {
                UpdatePlayer(combatant, serverTick);
            }
        }

        ProcessDeaths(serverTick);
        foreach (var combatant in combatantBuffer)
        {
            UpdateCombatFlag(combatant, serverTick);
            if (combatant.Kind == CombatantKind.Player)
            {
                RegenerateOrRespawn(combatant, serverTick);
            }
        }

        replicator.Flush(serverTick);
        metrics?.RecordPhase(Stopwatch.GetElapsedTime(phaseStarted));
    }

    public (Guid ItemInstanceId, int Quantity) GetLiveAmmunition(Combatant player)
    {
        if (player.Player is null
            || !authority.TryGet(player.Player.Session.SimulationSessionId, out var combatSession)
            || !TryGetItemState(player.Player, out var itemState))
        {
            return (Guid.Empty, 0);
        }

        return authority.GetLiveAmmunition(combatSession, itemCatalog.Resolve(itemState).Ammunition);
    }

    public void Clear()
    {
        combatants.Clear();
        projectiles.Clear();
        replicator.Clear();
        authority.Clear();
        mobDeaths.Clear();
    }

    private (string Code, string Message)? ValidateTarget(RealtimePeerContext context, Combatant attacker, ulong targetEntityId)
    {
        if (targetEntityId == attacker.EntityId)
        {
            return (RealtimeCombatErrorCodes.TargetSelf, "A character cannot attack itself.");
        }

        if (!presence.IsVisibleTo(context.PeerId, targetEntityId))
        {
            return (RealtimeCombatErrorCodes.TargetNotFound, "The target is not in view.");
        }

        if (!combatants.TryGet(targetEntityId, out var target))
        {
            return (RealtimeCombatErrorCodes.TargetNotAttackable, "The target cannot be attacked.");
        }

        if (!target.IsAlive)
        {
            return target.LifeState == RealtimeCombatLifeState.Dead
                ? (RealtimeCombatErrorCodes.TargetDead, "The target is dead.")
                : (RealtimeCombatErrorCodes.TargetNotAttackable, "The target cannot be attacked now.");
        }

        if (target.Kind == CombatantKind.Player && !shardRules.Current.PlayerVersusPlayer)
        {
            return (RealtimeCombatErrorCodes.PlayerVersusPlayerDisabled, "This Shard does not allow attacking other characters.");
        }

        return null;
    }

    private void SetTarget(Combatant attacker, ulong targetEntityId)
    {
        if (attacker.TargetEntityId == targetEntityId)
        {
            return;
        }

        attacker.TargetEntityId = targetEntityId;
        attacker.AttackStatus = targetEntityId == 0
            ? RealtimeCombatAttackStatus.Idle
            : RealtimeCombatAttackStatus.Attacking;
        attacker.LineOfSightTargetEntityId = 0;
        attacker.MarkStateChanged();
    }

    private void UpdatePlayer(Combatant attacker, uint serverTick)
    {
        var player = attacker.Player!;
        if (!attacker.IsAlive
            || !TryGetItemState(player, out var itemState)
            || !authority.TryGet(player.Session.SimulationSessionId, out var combatSession))
        {
            return;
        }

        TrackEquipmentChange(attacker, itemState, serverTick);
        if (attacker.TargetEntityId == 0)
        {
            return;
        }

        if (!combatants.TryGet(attacker.TargetEntityId, out var target)
            || !target.IsAlive
            || (target.Kind == CombatantKind.Player && !shardRules.Current.PlayerVersusPlayer))
        {
            SetTarget(attacker, 0);
            return;
        }

        var loadout = itemCatalog.Resolve(itemState);
        var status = DetermineStatus(attacker, combatSession, loadout, target, serverTick, out var distance);
        SetAttackStatus(attacker, status);
        if (status != RealtimeCombatAttackStatus.Attacking
            || !CombatTicks.IsReached(serverTick, attacker.NextAttackTick))
        {
            return;
        }

        var attack = loadout.Attack!;
        var ammunitionBonus = 0;
        if (attack.ConsumesAmmunition)
        {
            if (!authority.TrySpendArrow(combatSession, loadout.Ammunition))
            {
                SetAttackStatus(attacker, RealtimeCombatAttackStatus.AmmunitionMissing);
                return;
            }

            ammunitionBonus = loadout.Ammunition!.DamageBonusPercent;
            attacker.MarkOwnerStateChanged();
        }

        attacker.NextAttackTick = serverTick + attack.IntervalTicks;
        attacker.MarkOwnerStateChanged();
        EnterCombat(attacker, serverTick);
        Release(attacker, target, attack, ammunitionBonus, distance, serverTick);
    }

    private RealtimeCombatAttackStatus DetermineStatus(
        Combatant attacker,
        PlayerCombatSession combatSession,
        PlayerCombatLoadout loadout,
        Combatant target,
        uint serverTick,
        out float distance)
    {
        distance = 0f;
        switch (loadout.Status)
        {
            case CombatLoadoutStatus.WeaponMissing:
                return RealtimeCombatAttackStatus.WeaponMissing;
            case CombatLoadoutStatus.AmmunitionMissing:
                return RealtimeCombatAttackStatus.AmmunitionMissing;
            case CombatLoadoutStatus.AmmunitionIncompatible:
                return RealtimeCombatAttackStatus.AmmunitionIncompatible;
        }

        if (combatSession.EquipmentChangePending
            || combatSession.LeaveRequested
            || !CombatTicks.IsReached(serverTick, attacker.LockoutEndTick))
        {
            return RealtimeCombatAttackStatus.EquipmentLockout;
        }

        var movement = attacker.Player!.Movement;
        if (!movement.IsStandingStill)
        {
            return RealtimeCombatAttackStatus.Moving;
        }

        var origin = movement.State.Position;
        var bounds = BoundsOf(target);
        distance = bounds.DistanceFrom(origin);
        if (distance > loadout.Attack!.Range)
        {
            return RealtimeCombatAttackStatus.OutOfRange;
        }

        // Line of sight costs a raycast, so it is checked when an attack is due and a few
        // times per second otherwise.
        var attackDue = CombatTicks.IsReached(serverTick, attacker.NextAttackTick);
        if (attackDue
            || attacker.LineOfSightTargetEntityId != target.EntityId
            || CombatTicks.IsReached(serverTick, attacker.NextLineOfSightCheckTick))
        {
            attacker.LastLineOfSight = lineOfSight.HasLineOfSight(
                CombatLineOfSight.EyeOf(origin),
                bounds.Center);
            attacker.LineOfSightTargetEntityId = target.EntityId;
            attacker.NextLineOfSightCheckTick = serverTick + (uint)Math.Max(1, config.MovementSimulation.TickRateHz / 5);
        }

        return attacker.LastLineOfSight
            ? RealtimeCombatAttackStatus.Attacking
            : RealtimeCombatAttackStatus.LineOfSightBlocked;
    }

    private void Release(
        Combatant attacker,
        Combatant target,
        CombatAttackProfile attack,
        int ammunitionBonusPercent,
        float distance,
        uint serverTick)
    {
        metrics?.RecordAttack(attacker.Kind);
        var damage = CombatDamageCalculator.Calculate(
            attack.MinimumDamage,
            attack.MaximumDamage,
            attack.DamageType,
            ammunitionBonusPercent,
            random);
        var flightTicks = CombatTicks.FlightTicks(distance, attack.ProjectileSpeed, config.MovementSimulation.TickRateHz);
        if (flightTicks == 0)
        {
            replicator.AttackReleased(attacker.EntityId, target.EntityId, serverTick);
            ApplyHit(attacker.EntityId, target, damage, serverTick);
            return;
        }

        var impactTick = serverTick + flightTicks;
        replicator.AttackReleased(attacker.EntityId, target.EntityId, impactTick);
        projectiles.Enqueue(
            new Projectile(attacker.EntityId, target.EntityId, damage),
            (impactTick, projectileSequence++));
    }

    private void ResolveDueImpacts(uint serverTick)
    {
        while (projectiles.TryPeek(out var projectile, out var priority)
            && CombatTicks.IsReached(serverTick, priority.ImpactTick))
        {
            projectiles.Dequeue();
            // The arrow is spent even when its target died or left in the meantime.
            if (combatants.TryGet(projectile.TargetEntityId, out var target) && target.IsAlive)
            {
                ApplyHit(projectile.AttackerEntityId, target, projectile.Damage, serverTick);
            }
        }
    }

    private void ApplyHit(ulong attackerEntityId, Combatant target, CombatDamage damage, uint serverTick)
    {
        if (!target.IsAlive || IsInvulnerable(target))
        {
            return;
        }

        metrics?.RecordHit(target.Kind);
        target.Health = Math.Max(0, target.Health - damage.Amount);
        target.LastAttackerEntityId = attackerEntityId;
        if (target.Kind == CombatantKind.Mob)
        {
            target.DamageLedger[attackerEntityId] =
                target.DamageLedger.GetValueOrDefault(attackerEntityId) + damage.Amount;
        }

        EnterCombat(target, serverTick);
        if (combatants.TryGet(attackerEntityId, out var attacker))
        {
            EnterCombat(attacker, serverTick);
        }

        target.MarkStateChanged();
        replicator.Hit(attackerEntityId, target.EntityId, damage.Amount, damage.DamageType, target.Health);
        if (target.Health == 0 && !deaths.Contains(target))
        {
            deaths.Add(target);
        }
    }

    private void ProcessDeaths(uint serverTick)
    {
        foreach (var dead in deaths)
        {
            metrics?.RecordDeath(dead.Kind);
            replicator.Died(dead.LastAttackerEntityId, dead.EntityId);
            if (dead.Kind == CombatantKind.Player)
            {
                KillPlayer(dead, serverTick);
            }
            else
            {
                MarkDead(dead, serverTick);
                mobDeaths.Begin(dead, KillerOf(dead));
            }
        }

        deaths.Clear();
    }

    private void KillPlayer(Combatant dead, uint serverTick)
    {
        var player = dead.Player!;
        MarkDead(dead, serverTick);
        player.Movement.Halt();
        if (connectionBindings.TryGetConnectionId(player.NetworkEntityId, out var connectionId)
            && peers.TryGetContext(connectionId, out var context))
        {
            CloseWorldInteraction?.Invoke(context, RealtimeCombatErrorCodes.CharacterDead, "The character died.");
            if (corpseViewers.TryGetActiveCorpse(context.PeerId, out var corpseId)
                && peers.TryGetCurrentPeer(context, out var peer))
            {
                corpseViews.CloseCorpseForPeer(context, peer, corpseId, RealtimeCombatErrorCodes.CharacterDead, "The character died.");
            }
        }

        if (authority.TryGet(player.Session.SimulationSessionId, out var combatSession))
        {
            authority.BeginDeath(combatSession, KillerOf(dead), player.Movement.State);
        }
    }

    private void MarkDead(Combatant dead, uint serverTick)
    {
        dead.Health = 0;
        dead.LifeState = RealtimeCombatLifeState.Dead;
        dead.TargetEntityId = 0;
        dead.AttackStatus = RealtimeCombatAttackStatus.Dead;
        dead.LifeSequence++;
        dead.RespawnTick = serverTick + respawnDelayTicks;
        dead.MarkStateChanged();
        combatants.ClearTargetsOn(dead.EntityId);
    }

    // A dead character respawns at the World spawn once its death committed and the
    // respawn delay passed; a living one out of combat regenerates every second.
    private void RegenerateOrRespawn(Combatant combatant, uint serverTick)
    {
        var player = combatant.Player!;
        if (!combatant.IsAlive)
        {
            if (CombatTicks.IsReached(serverTick, combatant.RespawnTick)
                && authority.TryGet(player.Session.SimulationSessionId, out var combatSession)
                && combatSession.Death is { Committed: true })
            {
                Respawn(combatant, combatSession);
            }

            return;
        }

        if (combatant.InCombat
            || combatant.Health >= combatant.MaximumHealth
            || rules.RegenerationPercentPerSecond <= 0
            || !CombatTicks.IsReached(serverTick, combatant.NextRegenerationTick))
        {
            return;
        }

        var amount = Math.Max(1, combatant.MaximumHealth * rules.RegenerationPercentPerSecond / 100);
        combatant.Health = Math.Min(combatant.MaximumHealth, combatant.Health + amount);
        combatant.NextRegenerationTick = serverTick + (uint)config.MovementSimulation.TickRateHz;
        combatant.MarkStateChanged();
    }

    private void Respawn(Combatant combatant, PlayerCombatSession combatSession)
    {
        var player = combatant.Player!;
        authority.ClearDeath(combatSession);
        player.Movement.Teleport(PlayerMovementSimulation.CreateInitialState(
            config.MovementSimulation,
            collisionWorld,
            config.MovementSpawn.X,
            config.MovementSpawn.Y,
            config.MovementSpawn.Z,
            config.MovementSpawn.YawDegrees));
        combatant.Health = combatant.MaximumHealth;
        combatant.LifeState = RealtimeCombatLifeState.Alive;
        combatant.AttackStatus = RealtimeCombatAttackStatus.Idle;
        combatant.RespawnTick = 0;
        combatant.LifeSequence++;
        combatant.InCombat = false;
        combatant.InCombatUntilTick = clock.ServerTick;
        combatant.MarkStateChanged();
        presence.RespawnEntityForViewers(player, "respawned");
    }

    private void UpdateCombatFlag(Combatant combatant, uint serverTick)
    {
        var inCombat = combatant.IsAlive && !CombatTicks.IsReached(serverTick, combatant.InCombatUntilTick);
        if (inCombat == combatant.InCombat)
        {
            return;
        }

        combatant.InCombat = inCombat;
        if (!inCombat)
        {
            combatant.NextRegenerationTick = serverTick + (uint)config.MovementSimulation.TickRateHz;
            combatant.DamageLedger.Clear();
        }

        combatant.MarkStateChanged();
    }

    private void EnterCombat(Combatant combatant, uint serverTick)
    {
        combatant.InCombatUntilTick = serverTick + Math.Max(1u, combatTimeoutTicks);
    }

    // A new committed item state can change the arrow count, so the owner state follows it.
    // A change of the item in any equipment slot also holds attacks for the lockout; arrow
    // count changes keep the same item and do not count. A committed state with the same
    // revision is the same state, so nothing is compared until the revision changes.
    private void TrackEquipmentChange(Combatant attacker, CharacterItemState itemState, uint serverTick)
    {
        if (itemState.ItemStateRevision == attacker.ObservedItemStateRevision)
        {
            return;
        }

        var firstObservation = attacker.ObservedItemStateRevision < 0;
        attacker.ObservedItemStateRevision = itemState.ItemStateRevision;
        attacker.MarkOwnerStateChanged();
        var signature = string.Join(
            "|",
            itemState.Equipment.Select(item => item.EquipmentSlotId + "=" + item.ItemInstanceId.ToString("N")));
        if (string.Equals(signature, attacker.EquipmentSignature, StringComparison.Ordinal))
        {
            return;
        }

        attacker.EquipmentSignature = signature;
        if (!firstObservation && lockoutTicks > 0)
        {
            attacker.LockoutEndTick = serverTick + lockoutTicks;
        }
    }

    private void SetAttackStatus(Combatant attacker, RealtimeCombatAttackStatus status)
    {
        if (attacker.AttackStatus == status)
        {
            return;
        }

        attacker.AttackStatus = status;
        attacker.MarkOwnerStateChanged();
    }

    private void SendIntentResult(
        IRealtimePeerConnection peer,
        RealtimeCombatIntent intent,
        Combatant attacker,
        string? code,
        string? message)
    {
        sender.SendControl(peer, RealtimeProtocol.EncodeCombatIntentResult(new RealtimeCombatIntentResult(
            intent.OperationId,
            intent.Kind,
            code is null,
            attacker.IsAlive ? attacker.TargetEntityId : 0,
            code is null ? null : new RealtimeError(code, message!))));
    }

    private void OnOwnerStateChanged(PlayerCombatSession combatSession)
    {
        if (entityRegistry.TryGetPlayerByCharacterId(combatSession.Session.CharacterId, out var entity)
            && combatants.TryGet(entity!.NetworkEntityId, out var combatant))
        {
            combatant.MarkOwnerStateChanged();
        }
    }

    private void OnMobRespawned(WorldActorRuntimeState actor)
    {
        TryRegisterMob(actor);
        presence.TrackSpawnedWorldActor(actor);
        metrics?.SetActiveCombatants(combatants.Count);
    }

    private void TryRegisterMob(WorldActorRuntimeState actor)
    {
        var definition = actor.Definition;
        if (!string.Equals(definition.Kind, WorldActorKindIds.Mob, StringComparison.Ordinal)
            || !string.Equals(definition.DamagePolicy, WorldActorDamagePolicyIds.Damageable, StringComparison.Ordinal)
            || string.IsNullOrEmpty(definition.CombatProfileId)
            || !combatProfiles.TryGetValue(definition.CombatProfileId, out var profile)
            || combatants.TryGet(actor.NetworkEntityId, out _))
        {
            return;
        }

        combatants.Add(Combatant.ForMob(
            actor,
            profile.MaximumHealth,
            new CombatAttackProfile(
                profile.Id,
                profile.AttackStyle,
                profile.AttackDamageType,
                profile.MinimumDamage,
                profile.MaximumDamage,
                Math.Max(1u, CombatTicks.FromSeconds(profile.AttackIntervalSeconds, config.MovementSimulation.TickRateHz)),
                profile.AttackRange,
                0f,
                null)));
    }

    private SimulationDeathKillerRequest? KillerOf(Combatant dead)
    {
        if (!combatants.TryGet(dead.LastAttackerEntityId, out var killer) || killer.EntityId == dead.EntityId)
        {
            return null;
        }

        return killer.Kind == CombatantKind.Player
            ? new SimulationDeathKillerRequest("character", killer.Player!.Session.CharacterId.ToString("D"))
            : new SimulationDeathKillerRequest("mob", killer.Actor!.Definition.Id);
    }

    private static bool IsInvulnerable(Combatant target)
    {
        return target.LifeState == RealtimeCombatLifeState.Evading;
    }

    private CombatBounds BoundsOf(Combatant target)
    {
        if (target.Kind == CombatantKind.Player)
        {
            var collision = config.MovementSimulation.CharacterCollision;
            return CombatBounds.Capsule(target.Player!.Movement.State.Position, collision.Radius, collision.Height);
        }

        var actor = target.Actor!;
        var bounds = actor.Definition.InteractionBounds;
        return CombatBounds.Box(
            new SimulationVector3(actor.PositionX, actor.PositionY, actor.PositionZ),
            new SimulationVector3(actor.BoundsCenterX, actor.BoundsCenterY, actor.BoundsCenterZ),
            new SimulationVector3(bounds.SizeX, bounds.SizeY, bounds.SizeZ));
    }

    private bool TryGetItemState(PlayerSimulationEntity player, out CharacterItemState itemState)
    {
        if (itemStateStore.TryGetItemState(
                player.Session.CharacterId,
                player.Session.SimulationSessionId,
                out var state))
        {
            itemState = state!;
            return true;
        }

        itemState = null!;
        return false;
    }

    private readonly record struct Projectile(ulong AttackerEntityId, ulong TargetEntityId, CombatDamage Damage);
}
