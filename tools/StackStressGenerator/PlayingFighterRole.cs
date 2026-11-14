using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.Tools.SimulationBotClient;
using Mmorpg.WorldData.Items;

namespace Mmorpg.Tools.StackStressGenerator;

// Hunts the Mobs of its hunting ground like a careful player: it equips its weapon, pulls one
// Mob at a time, preferring a Mob with no other aggressive Mob within reach of it, walks into
// range, stands and attacks until the Mob dies or it must flee, loots the Mob's corpse when it
// may, and rests before the next pull. A Mob that attacks it is fought first. When its slots or
// carry capacity run low it walks to the town's vendor, sells its loot, and walks back; a bow
// fighter buys arrows there too, and goes on as a walker when it has neither arrows nor the
// currency for more. After a death it waits for the respawn, walks back to its own corpse,
// loots it back, equips again, and hunts on; when the corpse is gone or out of reach it goes on
// as a walker, and the report counts it.
public sealed class FighterRole : PlayingRoleBase
{
    // Below this share of health the fighter stops attacking and walks away, and below the
    // second one already when more than one Mob attacks it.
    public const double FleeHealthShare = 0.35;
    public const double CrowdFleeHealthShare = 0.6;

    // Below this share it rests before it picks the next Mob, until RestUntilShare.
    public const double RestHealthShare = 0.85;
    public const double RestUntilShare = 0.95;

    // A bow fighter buys arrows below the first count, up to the second.
    public const int ArrowRestockThreshold = 100;
    public const int ArrowTarget = 500;

    // Every other aggressive Mob within its detection range and this margin of a Mob makes the
    // Mob as unattractive as a Mob this much farther away.
    public const float CrowdMargin = 4f;
    public const float CrowdPenalty = 25f;

    // A Mob counts for the ground this far outside its rectangle, because Mobs chase and walk.
    public const float GroundMargin = 12f;

    // The fighter stands this much inside its weapon's range.
    public const float RangeMargin = 0.6f;

    public static readonly TimeSpan CorpseWait = TimeSpan.FromSeconds(6);
    public static readonly TimeSpan RecoveryLimit = TimeSpan.FromMinutes(4);
    public static readonly TimeSpan MaximumRest = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan MaximumFlee = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan IgnoreTarget = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReplanInterval = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan TargetSettle = TimeSpan.FromSeconds(1.5);

    // How long a fighter in range waits for a blocked line of sight to clear, such as while the
    // Mob walks around a rock, before it gives the target up.
    public static readonly TimeSpan LineOfSightPatience = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan OwnCorpseSearch = TimeSpan.FromSeconds(5);
    private const int MaximumApproachLegs = 6;
    private const float FleeDistance = 30f;
    private const float CorpseReach = 2f;
    private const float CorpseSearchRadius = 5f;
    private const float DeathSpotReach = 4f;

    private readonly Dictionary<ulong, TimeSpan> ignored = [];
    private FighterState state = FighterState.Seek;
    private TimeSpan? sightBlockedSince;
    private TimeSpan stateSince;
    private WalkerRole? walker;
    private ulong target;
    private string targetDefinitionId = string.Empty;
    private SimulationVector3 targetPosition;
    private SimulationVector3 approachDestination;
    private TimeSpan lastPlanAt;
    private bool approachPlanned;
    private int approachLegs;
    private Guid corpse;
    private bool corpseOpened;
    private bool corpseLooted;
    private bool lootingOwnCorpse;
    private SimulationVector3 deathPosition;
    private uint deathLifeSequence;
    private TimeSpan? atDeathSpotSince;
    private bool lootRefusedFull;
    private bool restockTried;

    public FighterRole(PlayingGround home, bool teleport, Random random, PlayingGround? town = null)
        : base(home ?? throw new ArgumentNullException(nameof(home)), teleport, random, town)
    {
    }

    private enum FighterState
    {
        Seek,
        Approach,
        Fight,
        Flee,
        Rest,
        Loot,
        Dead,
        Recover
    }

    public override PlayingRole Role => PlayingRole.Fighter;

    public override string State => walker is not null ? "walker" : state.ToString().ToLowerInvariant();

    // Set once the fighter lost its corpse and went on as a walker.
    public bool ContinuesAsWalker => walker is not null;

    public ulong Target => target;

    protected override void OnOutcome(IPlayingBotBody body, PlayingIntentOutcome outcome, TimeSpan now)
    {
        switch (outcome.Operation)
        {
            case PlayingOperations.Teleport:
                OnTeleportOutcome(outcome);
                break;
            case PlayingOperations.Equip when !outcome.Succeeded:
                RequireEquip();
                break;
            case PlayingOperations.Attack when !outcome.Succeeded:
                Drop(now);
                break;
            case PlayingOperations.LootOpen:
                corpseOpened = outcome.Succeeded;
                if (!outcome.Succeeded)
                {
                    FinishLoot(body, now);
                }

                break;
            case PlayingOperations.LootAll when outcome.Succeeded && lootingOwnCorpse:
                body.Count(PlayingCounter.CorpsesRecovered);
                lootingOwnCorpse = false;
                break;
            case PlayingOperations.LootAll when outcome.Code == "inventory_full":
                // Nothing more fits: the next pull waits for a trip to the vendor.
                lootRefusedFull = true;
                break;
            case PlayingOperations.LootClose:
                corpseOpened = false;
                break;
        }
    }

    protected override void Act(IPlayingBotBody body, TimeSpan now)
    {
        if (walker is not null)
        {
            walker.Think(body, now);
            return;
        }

        if (IsDead(body) && state != FighterState.Dead)
        {
            deathPosition = body.Position;
            deathLifeSequence = body.View.OwnCombat!.LifeSequence;
            target = 0;
            corpse = Guid.Empty;
            corpseOpened = false;
            ForgetTravel();
            Enter(FighterState.Dead, now);
            return;
        }

        switch (state)
        {
            case FighterState.Seek:
                Seek(body, now);
                break;
            case FighterState.Approach:
                Approach(body, now);
                break;
            case FighterState.Fight:
                Fight(body, now);
                break;
            case FighterState.Flee:
                Flee(body, now);
                break;
            case FighterState.Rest:
                Rest(body, now);
                break;
            case FighterState.Loot:
                Loot(body, now);
                break;
            case FighterState.Dead:
                AwaitRespawn(body, now);
                break;
            case FighterState.Recover:
                Recover(body, now);
                break;
        }
    }

    private void Seek(IPlayingBotBody body, TimeSpan now)
    {
        if (!WearsKit(body))
        {
            if (HasKitToEquip(body))
            {
                RequireEquip();
            }
            else if (ArrowDefinition(body) is not null && !restockTried && StartVendorTrip(body, now))
            {
                // The arrows are gone: a bow fighter buys more before it gives up.
                restockTried = true;
            }
            else
            {
                // The weapon or its arrows are gone, so the fighter cannot fight any more.
                if (ArrowDefinition(body) is not null)
                {
                    body.Count(PlayingCounter.ArrowsRanOut);
                }

                body.Stand();
                walker = new WalkerRole(null, teleport: false, Random);
            }

            return;
        }

        restockTried = false;
        var attacker = FindAttacker(body);
        if (attacker is null && HealthShare(body) < RestHealthShare)
        {
            body.Stand();
            Enter(FighterState.Rest, now);
            return;
        }

        if (attacker is null
            && (lootRefusedFull || NeedsToSell(body) || NeedsArrows(body))
            && StartVendorTrip(body, now))
        {
            lootRefusedFull = false;
            return;
        }

        var choice = attacker ?? NearestHuntableMob(body, now);
        if (choice is null)
        {
            // Nothing to hunt nearby: walk to another part of the ground and look again.
            if (body.MoveState is not (PlayingMoveState.Planning or PlayingMoveState.Walking)
                && RandomGroundIn(body, Home!.Area) is { } point)
            {
                body.WalkTo(point);
            }

            return;
        }

        Choose(choice, now);
        Approach(body, now);
    }

    private void Approach(IPlayingBotBody body, TimeSpan now)
    {
        if (!TryGetLivingTarget(body, out var actor, out var combatant))
        {
            AfterTargetGone(body, now);
            return;
        }

        if (combatant.TaggedByEntityId != 0 && combatant.TaggedByEntityId != body.View.OwnEntityId)
        {
            body.Stand();
            Drop(now);
            return;
        }

        targetPosition = ToVector(actor.Position);
        if (PlanarDistance(body.Position, targetPosition) <= body.Kit.WeaponRange - RangeMargin)
        {
            body.Stand();
            if (body.View.OwnCombat?.TargetEntityId == target || body.Attack(target))
            {
                Enter(FighterState.Fight, now);
            }

            return;
        }

        if (!approachPlanned)
        {
            PlanApproach(body, now);
            return;
        }

        switch (body.MoveState)
        {
            case PlayingMoveState.Planning:
                return;
            case PlayingMoveState.Walking:
                if (PlanarDistance(targetPosition, approachDestination) > 3f && now - lastPlanAt >= ReplanInterval)
                {
                    PlanApproach(body, now);
                }

                return;
        }

        // The last walk ended out of range: walk again, a few times at most.
        if (++approachLegs > MaximumApproachLegs)
        {
            body.Stand();
            Drop(now);
            return;
        }

        PlanApproach(body, now);
    }

    private void PlanApproach(IPlayingBotBody body, TimeSpan now)
    {
        approachPlanned = true;
        approachDestination = targetPosition;
        lastPlanAt = now;
        body.WalkTo(PointToward(targetPosition, body.Position, body.Kit.WeaponRange - (RangeMargin * 2f)));
    }

    private void Fight(IPlayingBotBody body, TimeSpan now)
    {
        if (!TryGetLivingTarget(body, out var actor, out _))
        {
            AfterTargetGone(body, now);
            return;
        }

        targetPosition = ToVector(actor.Position);
        var health = HealthShare(body);
        if (health < FleeHealthShare || (health < CrowdFleeHealthShare && CountAttackers(body) > 1))
        {
            if (body.ClearTarget())
            {
                body.Count(PlayingCounter.FighterFlees);
                Enter(FighterState.Flee, now);
            }

            return;
        }

        var own = body.View.OwnCombat;
        switch (own?.AttackStatus)
        {
            case RealtimeCombatAttackStatus.WeaponMissing:
            case RealtimeCombatAttackStatus.AmmunitionMissing:
            case RealtimeCombatAttackStatus.AmmunitionIncompatible:
                Enter(FighterState.Seek, now);
                return;
            case RealtimeCombatAttackStatus.OutOfRange:
                approachLegs = 0;
                approachPlanned = false;
                Enter(FighterState.Approach, now);
                Approach(body, now);
                return;
            case RealtimeCombatAttackStatus.LineOfSightBlocked:
                // The fighter is in range and cover blocks the attack. Walking into range again
                // would stand still where it is, so it waits a moment for the Mob to move and
                // then gives this target up for another one.
                sightBlockedSince ??= now;
                if (now - sightBlockedSince >= LineOfSightPatience && body.ClearTarget())
                {
                    Drop(now);
                }

                return;
        }

        sightBlockedSince = null;

        // The worker cleared the target, such as after the Mob evaded; choose again. The owner
        // state follows the accepted intent a tick later, so a fresh fight waits for it.
        if (own is not null && own.TargetEntityId != target && now - stateSince >= TargetSettle)
        {
            Drop(now);
        }
    }

    private void Flee(IPlayingBotBody body, TimeSpan now)
    {
        var goal = PointToward(targetPosition, body.Position, FleeDistance, beyond: true);
        if (TravelTo(body, goal) != false || now - stateSince > MaximumFlee)
        {
            body.Stand();
            ForgetTravel();
            Enter(FighterState.Rest, now);
        }
    }

    private void Rest(IPlayingBotBody body, TimeSpan now)
    {
        if (FindAttacker(body) is { } attacker && HealthShare(body) >= FleeHealthShare)
        {
            Choose(attacker, now);
            return;
        }

        if (HealthShare(body) >= RestUntilShare || now - stateSince >= MaximumRest)
        {
            Enter(FighterState.Seek, now);
        }
    }

    private void Loot(IPlayingBotBody body, TimeSpan now)
    {
        if (corpseOpened)
        {
            if (body.View.OpenCorpseId != corpse)
            {
                // The worker closed the view, as it does once a Mob corpse is empty.
                FinishLoot(body, now);
                return;
            }

            if (!body.View.OpenCorpseReady)
            {
                if (now - stateSince > CorpseWait)
                {
                    body.CloseCorpse(corpse, Math.Max(0, body.View.OpenCorpseRevision));
                    FinishLoot(body, now);
                }

                return;
            }

            if (!corpseLooted && body.View.Corpses.GetValueOrDefault(corpse) is { IsEmpty: false })
            {
                corpseLooted = true;
                body.LootAll(corpse, body.View.OpenCorpseRevision);
                return;
            }

            body.CloseCorpse(corpse, body.View.OpenCorpseRevision);
            FinishLoot(body, now);
            return;
        }

        if (corpse == Guid.Empty)
        {
            var found = FindMobCorpse(body);
            if (found is null)
            {
                if (now - stateSince > CorpseWait)
                {
                    Enter(FighterState.Seek, now);
                }

                return;
            }

            if (!found.IsLootable)
            {
                body.Count(PlayingCounter.LootRefusedOwned);
                Enter(FighterState.Seek, now);
                return;
            }

            corpse = found.CorpseId;
            corpseLooted = false;
        }

        if (!body.View.Corpses.TryGetValue(corpse, out var current) || current.IsEmpty)
        {
            FinishLoot(body, now);
            return;
        }

        var reached = TravelTo(
            body,
            new SimulationVector3(current.PositionX, current.PositionY, current.PositionZ),
            CorpseReach);
        if (reached is null)
        {
            FinishLoot(body, now);
        }
        else if (reached == true)
        {
            body.Stand();
            if (body.View.OpenCorpseId != Guid.Empty && body.View.OpenCorpseId != corpse)
            {
                body.CloseCorpse(body.View.OpenCorpseId, Math.Max(0, body.View.OpenCorpseRevision));
                return;
            }

            body.OpenCorpse(corpse);
        }
    }

    private void AwaitRespawn(IPlayingBotBody body, TimeSpan now)
    {
        var own = body.View.OwnCombat;
        if (own is { LifeState: RealtimeCombatLifeState.Alive } && own.LifeSequence != deathLifeSequence)
        {
            Enter(FighterState.Recover, now);
        }
    }

    private void Recover(IPlayingBotBody body, TimeSpan now)
    {
        if (now - stateSince > RecoveryLimit)
        {
            LoseCorpse(body);
            return;
        }

        if (FindOwnCorpse(body) is { } own)
        {
            corpse = own.CorpseId;
            corpseOpened = false;
            corpseLooted = false;
            lootingOwnCorpse = true;
            ForgetTravel();
            Enter(FighterState.Loot, now);
            Loot(body, now);
            return;
        }

        var reached = TravelTo(body, deathPosition, DeathSpotReach);
        if (reached is null)
        {
            LoseCorpse(body);
        }
        else if (reached == true)
        {
            // At the death spot, the corpse must show in a presence snapshot within a few seconds.
            atDeathSpotSince ??= now;
            if (now - atDeathSpotSince.Value > OwnCorpseSearch)
            {
                LoseCorpse(body);
            }
        }
    }

    private void LoseCorpse(IPlayingBotBody body)
    {
        body.Count(PlayingCounter.CorpsesLost);
        body.Stand();
        walker = new WalkerRole(null, teleport: false, Random);
    }

    private void FinishLoot(IPlayingBotBody body, TimeSpan now)
    {
        corpse = Guid.Empty;
        corpseOpened = false;
        corpseLooted = false;
        lootingOwnCorpse = false;
        ForgetTravel();
        if (HasKitToEquip(body))
        {
            RequireEquip();
        }

        Enter(FighterState.Seek, now);
    }

    // The target died or left view: its corpse is looted when the fighter fought it.
    private void AfterTargetGone(IPlayingBotBody body, TimeSpan now)
    {
        var died = body.RecentCombatEvents.Any(combatEvent =>
            combatEvent.Kind == RealtimeCombatEventKind.Died && combatEvent.TargetEntityId == target);
        var dead = body.View.Combatants.TryGetValue(target, out var combatant)
            && combatant.LifeState == RealtimeCombatLifeState.Dead;
        var fought = state == FighterState.Fight
            || (combatant is not null && combatant.TaggedByEntityId == body.View.OwnEntityId);
        target = 0;
        body.Stand();
        if ((died || dead) && fought)
        {
            corpse = Guid.Empty;
            corpseOpened = false;
            lootingOwnCorpse = false;
            Enter(FighterState.Loot, now);
            return;
        }

        Enter(FighterState.Seek, now);
    }

    private void Choose(SimulationBotWorldActor actor, TimeSpan now)
    {
        target = actor.EntityId;
        targetDefinitionId = actor.ActorDefinitionId;
        targetPosition = ToVector(actor.Position);
        approachDestination = targetPosition;
        approachPlanned = false;
        approachLegs = 0;
        Enter(FighterState.Approach, now);
    }

    private void Drop(TimeSpan now)
    {
        if (target != 0)
        {
            ignored[target] = now + IgnoreTarget;
        }

        target = 0;
        Enter(FighterState.Seek, now);
    }

    private bool TryGetLivingTarget(
        IPlayingBotBody body,
        out SimulationBotWorldActor actor,
        out RealtimeCombatantState combatant)
    {
        combatant = null!;
        return body.View.Actors.TryGetValue(target, out actor!)
            && body.View.Combatants.TryGetValue(target, out combatant!)
            && combatant.LifeState == RealtimeCombatLifeState.Alive;
    }

    protected override PlayingPurchase? PlanPurchase(
        IPlayingBotBody body,
        VendorProfileDefinition vendor,
        PlayingVendorVisit visit)
    {
        if (ArrowDefinition(body) is not { } arrows || visit.HasFailed(arrows))
        {
            return null;
        }

        var wanted = ArrowTarget - body.View.Items.CountOwned(arrows);
        var quantity = PlayingVendorVisit.AffordableQuantity(body, vendor, arrows, wanted);
        return PlayingVendorVisit.PurchaseOf(body, vendor, arrows, quantity);
    }

    // The arrows of a bow fighter's kit, or null for a fighter without a bow.
    private static string? ArrowDefinition(IPlayingBotBody body)
    {
        return body.Kit.Items
            .Select(kitItem => kitItem.DefinitionId)
            .FirstOrDefault(definitionId => body.GameData.Item(definitionId)?.Ammunition is not null);
    }

    protected override bool StillNeedsToBuy(IPlayingBotBody body)
    {
        return NeedsArrows(body);
    }

    private static bool NeedsArrows(IPlayingBotBody body)
    {
        return ArrowDefinition(body) is { } arrows
            && body.View.Items.CountOwned(arrows) < ArrowRestockThreshold;
    }

    private static int CountAttackers(IPlayingBotBody body)
    {
        var own = body.View.OwnEntityId;
        return body.View.Combatants.Values.Count(combatant =>
            combatant.TargetEntityId == own
            && combatant.LifeState == RealtimeCombatLifeState.Alive
            && body.View.Actors.TryGetValue(combatant.EntityId, out var actor)
            && actor.Kind == RealtimeWorldActorKind.Mob);
    }

    // The other living aggressive Mobs that would join a fight with this Mob, because they
    // stand within their detection range and a margin of it.
    private static int CountAggressiveNeighbors(IPlayingBotBody body, SimulationBotWorldActor mob)
    {
        var count = 0;
        foreach (var other in body.View.Actors.Values)
        {
            if (other.EntityId == mob.EntityId || other.Kind != RealtimeWorldActorKind.Mob)
            {
                continue;
            }

            var reach = body.GameData.AggroRange(other.ActorDefinitionId);
            if (reach <= 0f
                || !body.View.Combatants.TryGetValue(other.EntityId, out var combatant)
                || combatant.LifeState != RealtimeCombatLifeState.Alive)
            {
                continue;
            }

            if (PlanarDistance(ToVector(other.Position), ToVector(mob.Position)) <= reach + CrowdMargin)
            {
                count++;
            }
        }

        return count;
    }

    // The living Mob of the ground that neither another player's attack owns nor another
    // player busies, nearest after a penalty for every aggressive Mob that would join the fight.
    private SimulationBotWorldActor? NearestHuntableMob(IPlayingBotBody body, TimeSpan now)
    {
        var own = body.View.OwnEntityId;
        var area = Home!.Area;
        SimulationBotWorldActor? nearest = null;
        SimulationBotWorldActor? closest = null;
        var nearestScore = float.MaxValue;
        var closestDistance = float.MaxValue;
        foreach (var actor in body.View.Actors.Values)
        {
            if (actor.Kind != RealtimeWorldActorKind.Mob
                || !area.Contains(actor.Position.X, actor.Position.Z, GroundMargin)
                || !body.View.Combatants.TryGetValue(actor.EntityId, out var combatant)
                || combatant.LifeState != RealtimeCombatLifeState.Alive
                || (combatant.TaggedByEntityId != 0 && combatant.TaggedByEntityId != own)
                || (combatant.TargetEntityId != 0 && combatant.TargetEntityId != own)
                || (ignored.TryGetValue(actor.EntityId, out var until) && now < until))
            {
                continue;
            }

            var distance = PlanarDistance(body.Position, ToVector(actor.Position));
            var score = distance + (CountAggressiveNeighbors(body, actor) * CrowdPenalty);
            if (score < nearestScore)
            {
                nearest = actor;
                nearestScore = score;
            }

            if (distance < closestDistance)
            {
                closest = actor;
                closestDistance = distance;
            }
        }

        if (nearest is not null && closest is not null && nearest.EntityId != closest.EntityId)
        {
            body.Count(PlayingCounter.CrowdedMobsSkipped);
        }

        return nearest;
    }

    private RealtimeCorpsePresence? FindMobCorpse(IPlayingBotBody body)
    {
        RealtimeCorpsePresence? best = null;
        var bestDistance = float.MaxValue;
        foreach (var candidate in body.View.Corpses.Values)
        {
            if (candidate.Source.ActorDefinitionId != targetDefinitionId || candidate.IsEmpty)
            {
                continue;
            }

            var distance = PlanarDistance(CorpsePosition(candidate), targetPosition);
            if (distance > CorpseSearchRadius)
            {
                continue;
            }

            // A lootable corpse beats a nearer one that another player owns.
            if (best is null
                || (candidate.IsLootable && !best.IsLootable)
                || (candidate.IsLootable == best.IsLootable && distance < bestDistance))
            {
                best = candidate;
                bestDistance = distance;
            }
        }

        return best;
    }

    private RealtimeCorpsePresence? FindOwnCorpse(IPlayingBotBody body)
    {
        RealtimeCorpsePresence? best = null;
        var bestDistance = float.MaxValue;
        foreach (var candidate in body.View.Corpses.Values)
        {
            if (candidate.Source.CharacterName != body.View.OwnCharacterName || candidate.IsEmpty)
            {
                continue;
            }

            var distance = PlanarDistance(CorpsePosition(candidate), deathPosition);
            if (distance < bestDistance)
            {
                best = candidate;
                bestDistance = distance;
            }
        }

        return best;
    }

    private static SimulationVector3 CorpsePosition(RealtimeCorpsePresence corpse)
    {
        return new SimulationVector3(corpse.PositionX, corpse.PositionY, corpse.PositionZ);
    }

    private static double HealthShare(IPlayingBotBody body)
    {
        var own = body.View.OwnCombat;
        return own is null || own.MaximumHealth <= 0 ? 1d : own.Health / (double)own.MaximumHealth;
    }

    // The point at a distance from the origin toward the other point, or beyond the origin
    // away from it.
    private static SimulationVector3 PointToward(
        SimulationVector3 origin,
        SimulationVector3 toward,
        float distance,
        bool beyond = false)
    {
        var dx = toward.X - origin.X;
        var dz = toward.Z - origin.Z;
        var length = MathF.Sqrt((dx * dx) + (dz * dz));
        if (length < 0.001f)
        {
            return toward;
        }

        if (!beyond && length <= distance)
        {
            return toward;
        }

        var scale = Math.Max(0f, distance) / length;
        return new SimulationVector3(origin.X + (dx * scale), toward.Y, origin.Z + (dz * scale));
    }

    private void Enter(FighterState next, TimeSpan now)
    {
        sightBlockedSince = null;
        state = next;
        stateSince = now;
        if (next != FighterState.Recover)
        {
            atDeathSpotSince = null;
        }
    }
}
