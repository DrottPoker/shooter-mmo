using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.Tools.SimulationBotClient;
using Mmorpg.WorldData.Items;

namespace Mmorpg.Tools.StackStressGenerator;

// A role's state machine. Think runs every few hundred milliseconds, at human speed, and sends
// at most one intent; the body measures it.
public interface IPlayingRole
{
    PlayingRole Role { get; }

    // The current state, for diagnostics and tests.
    string State { get; }

    void Think(IPlayingBotBody body, TimeSpan now);
}

// What every playing role shares: equipping its kit, the one teleport to its home ground after
// the join, pauses after a rejected intent, walking toward a far point in legs, and the trip to
// the town's vendor to sell what it carries and buy what it needs, and back home.
public abstract class PlayingRoleBase : IPlayingRole
{
    // A rejected intent is followed by a pause of this long, so no bot retries in a loop.
    public static readonly TimeSpan MinimumRejectionPause = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan MaximumRejectionPause = TimeSpan.FromSeconds(4);

    // A bot walks to the vendor once its free slots, or the carry capacity it has left, fall
    // below these.
    public const int MinimumFreeSlots = 3;
    public const double MinimumFreeCarryShare = 0.15;

    // A trip that left the bot without room, because nothing it carries sells, or that found
    // no vendor waits this long before the next.
    public static readonly TimeSpan TripCooldown = TimeSpan.FromMinutes(5);

    // The time between two trades at the vendor.
    public static readonly TimeSpan MinimumTradeSpacing = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaximumTradeSpacing = TimeSpan.FromSeconds(2.5);

    // The bot is home again this close to its ground's center.
    private const float HomeArrival = 8f;

    // A point this close counts as reached.
    protected const float ArrivalDistance = 1.5f;

    // A walk toward a far point gives up after this many legs that brought it no closer.
    private const int MaximumStalledLegs = 3;

    private readonly PlayingGround? home;
    private readonly PlayingGround? town;
    private readonly bool teleport;
    private PlayingVendorVisit? visit;
    private bool returning;
    private TimeSpan noTripUntil;
    private bool equipped;
    private bool teleported;
    private int teleportAttempts;
    private SimulationVector3? travelGoal;
    private float travelBestDistance;
    private int travelStalledLegs;

    protected PlayingRoleBase(PlayingGround? home, bool teleport, Random random, PlayingGround? town = null)
    {
        this.home = home;
        this.town = town;
        this.teleport = teleport && home is not null;
        Random = random ?? throw new ArgumentNullException(nameof(random));
    }

    public abstract PlayingRole Role { get; }

    public abstract string State { get; }

    protected Random Random { get; }

    protected PlayingGround? Home => home;

    protected TimeSpan PauseUntil { get; set; }

    // Set while the bot walks to the vendor, trades there, or walks home again.
    public bool OnVendorTrip => visit is not null || returning;

    // The state of the trip, or null, for diagnostics and tests.
    public string? VendorTripState => visit?.State ?? (returning ? "returning" : null);

    public void Think(IPlayingBotBody body, TimeSpan now)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (!body.IsReady)
        {
            return;
        }

        if (body.TakeOutcome() is { } outcome)
        {
            if (!outcome.Succeeded)
            {
                PauseUntil = now + RandomBetween(MinimumRejectionPause, MaximumRejectionPause);
                body.Count(PlayingCounter.RejectionPauses);
            }

            if (visit is not null && PlayingVendorVisit.Owns(outcome.Operation))
            {
                visit.OnOutcome(body, outcome, now);
            }
            else
            {
                OnOutcome(body, outcome, now);
            }
        }

        if (now < PauseUntil || body.HasPendingIntent)
        {
            return;
        }

        if (!equipped)
        {
            equipped = !TryEquipKit(body);
            return;
        }

        if (teleport && !teleported)
        {
            Teleport(body);
            return;
        }

        if (OnVendorTrip && !ContinueVendorTrip(body, now))
        {
            return;
        }

        Act(body, now);
    }

    // Whether the bot carries so much that it should sell at the vendor before it goes on.
    public static bool NeedsToSell(IPlayingBotBody body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var items = body.View.Items;
        if (!items.HasState)
        {
            return false;
        }

        var capacity = items.CarryCapacity;
        return items.CountFreeSlots() < MinimumFreeSlots
            || (capacity > 0 && capacity - items.CarriedWeight < capacity * MinimumFreeCarryShare);
    }

    // Whether the role still lacks something that only the vendor sells, after a visit.
    protected virtual bool StillNeedsToBuy(IPlayingBotBody body)
    {
        return false;
    }

    // Starts the trip to the town's vendor, unless the scenario has no town or a trip that
    // achieved nothing ended a short while ago.
    protected bool StartVendorTrip(IPlayingBotBody body, TimeSpan now)
    {
        if (town is null || now < noTripUntil)
        {
            return false;
        }

        body.Stand();
        ForgetTravel();
        visit = new PlayingVendorVisit(
            town.Area,
            PlanTripTrade,
            Random,
            MinimumTradeSpacing,
            MaximumTradeSpacing,
            countsAmmunition: Role == PlayingRole.Fighter);
        returning = false;
        body.Count(PlayingCounter.VendorTrips);
        return true;
    }

    // A trip keeps the kit, a bow fighter's arrows included, and sells everything else.
    private static bool KeepsItem(IPlayingBotBody body, SimulationBotOwnedItem item)
    {
        return body.Kit.Items.Any(kitItem => kitItem.DefinitionId == item.DefinitionId);
    }

    // What a role buys on its trip once it sold everything, one purchase at a time.
    protected virtual PlayingPurchase? PlanPurchase(
        IPlayingBotBody body,
        VendorProfileDefinition vendor,
        PlayingVendorVisit visit)
    {
        return null;
    }


    // The nearest living Mob that attacks the bot.
    protected static SimulationBotWorldActor? FindAttacker(IPlayingBotBody body)
    {
        var own = body.View.OwnEntityId;
        SimulationBotWorldActor? nearest = null;
        var nearestDistance = float.MaxValue;
        foreach (var combatant in body.View.Combatants.Values)
        {
            if (combatant.TargetEntityId != own
                || combatant.LifeState != RealtimeCombatLifeState.Alive
                || !body.View.Actors.TryGetValue(combatant.EntityId, out var actor)
                || actor.Kind != RealtimeWorldActorKind.Mob)
            {
                continue;
            }

            var distance = PlanarDistance(body.Position, ToVector(actor.Position));
            if (distance < nearestDistance)
            {
                nearest = actor;
                nearestDistance = distance;
            }
        }

        return nearest;
    }

    // True once the trip ended this think, so the role acts again.
    private bool ContinueVendorTrip(IPlayingBotBody body, TimeSpan now)
    {
        // A Mob that attacks the bot ends its trip, so the role can fight or flee.
        if (IsDead(body) || FindAttacker(body) is not null)
        {
            visit = null;
            returning = false;
            ForgetTravel();
            return true;
        }

        if (visit is not null)
        {
            var status = visit.Think(body, now, (goal, arrival) => TravelTo(body, goal, arrival));
            if (status == PlayingVisitStatus.Running)
            {
                return false;
            }

            visit = null;
            ForgetTravel();
            if (status == PlayingVisitStatus.Failed)
            {
                body.Count(PlayingCounter.VendorTripsFailed);
                noTripUntil = now + TripCooldown;
            }
            else if (NeedsToSell(body))
            {
                body.Count(PlayingCounter.VendorTripsWithoutRoom);
                noTripUntil = now + TripCooldown;
            }
            else if (StillNeedsToBuy(body))
            {
                // The visit could not buy what the role lacks, such as arrows it cannot pay
                // for. Without a pause the same need would send it straight back.
                noTripUntil = now + TripCooldown;
            }

            returning = home is not null;
            if (!returning)
            {
                return true;
            }
        }

        var area = home!.Area;
        var center = body.TryFindGround(area.CenterX, area.CenterZ, out var ground)
            ? ground
            : new SimulationVector3(area.CenterX, body.Position.Y, area.CenterZ);
        if (!area.Contains(body.Position.X, body.Position.Z) && TravelTo(body, center, HomeArrival) == false)
        {
            return false;
        }

        returning = false;
        ForgetTravel();
        body.Stand();
        return true;
    }

    // Sells every carried stack the bot does not keep and the vendor buys, then lets the role
    // buy what it needs.
    private PlayingTrade? PlanTripTrade(IPlayingBotBody body, VendorProfileDefinition vendor, PlayingVendorVisit current)
    {
        foreach (var item in body.View.Items.Items
                     .Where(item => !item.IsEquipped)
                     .OrderBy(item => item.ContainerId == body.View.Items.PermanentInventoryId ? 0 : 1)
                     .ThenBy(item => item.SlotIndex))
        {
            if (KeepsItem(body, item) || current.HasFailed(item))
            {
                continue;
            }

            if (PlayingVendorVisit.SaleOf(body, vendor, item, item.Quantity) is { } sale)
            {
                return sale;
            }
        }

        return PlanPurchase(body, vendor, current);
    }

    // Reacts to the outcome of the role's own intent.
    protected abstract void OnOutcome(IPlayingBotBody body, PlayingIntentOutcome outcome, TimeSpan now);

    protected abstract void Act(IPlayingBotBody body, TimeSpan now);

    // Equips the next kit item that is carried but not worn; false once every kit item is
    // worn or none is carried.
    protected bool TryEquipKit(IPlayingBotBody body)
    {
        foreach (var kitItem in body.Kit.Items)
        {
            var worn = body.View.Items.FindEquipped(kitItem.EquipmentSlotId);
            if (worn is not null && worn.DefinitionId == kitItem.DefinitionId)
            {
                continue;
            }

            var carried = body.View.Items.FindCarried(kitItem.DefinitionId);
            if (carried is not null && worn is null)
            {
                body.Stand();
                return body.Equip(carried, kitItem.EquipmentSlotId);
            }
        }

        return false;
    }

    // Whether a kit item is carried while its equipment slot is empty, so equipping helps.
    protected static bool HasKitToEquip(IPlayingBotBody body)
    {
        return body.Kit.Items.Any(kitItem =>
            body.View.Items.FindEquipped(kitItem.EquipmentSlotId) is null
            && body.View.Items.FindCarried(kitItem.DefinitionId) is not null);
    }

    // Whether every kit item is worn, as a fighter needs to attack.
    protected static bool WearsKit(IPlayingBotBody body)
    {
        return body.Kit.Items.All(kitItem =>
            body.View.Items.FindEquipped(kitItem.EquipmentSlotId)?.DefinitionId == kitItem.DefinitionId);
    }

    protected void RequireEquip()
    {
        equipped = false;
    }

    protected void OnTeleportOutcome(PlayingIntentOutcome outcome)
    {
        // A failed teleport is retried after the pause, and the bot walks from where it is
        // after three attempts.
        if (outcome.Succeeded || ++teleportAttempts >= 3)
        {
            teleported = true;
        }
    }

    private void Teleport(IPlayingBotBody body)
    {
        var area = home!.Area;
        if (!body.TryFindGround(area.CenterX, area.CenterZ, out var ground))
        {
            throw new InvalidOperationException(
                $"The home ground '{home.Name}' has no walkable ground at its center.");
        }

        body.Stand();
        if (!body.Teleport(ground))
        {
            teleported = true;
        }
    }

    // Walks toward a point that may lie beyond one route's reach. True once the bot stands
    // within the arrival distance; false while it walks; null when it gave up.
    protected bool? TravelTo(IPlayingBotBody body, SimulationVector3 goal, float arrivalDistance = ArrivalDistance)
    {
        var distance = PlanarDistance(body.Position, goal);
        if (distance <= arrivalDistance)
        {
            travelGoal = null;
            if (body.MoveState is PlayingMoveState.Walking or PlayingMoveState.Planning)
            {
                body.Stand();
            }

            return true;
        }

        if (travelGoal is not { } current || PlanarDistance(current, goal) > 0.5f)
        {
            travelGoal = goal;
            travelBestDistance = distance;
            travelStalledLegs = 0;
            body.WalkTo(goal);
            return false;
        }

        switch (body.MoveState)
        {
            case PlayingMoveState.Planning:
            case PlayingMoveState.Walking:
                return false;
            case PlayingMoveState.Failed:
            case PlayingMoveState.Arrived:
            case PlayingMoveState.Standing:
                if (distance < travelBestDistance - 1f)
                {
                    travelBestDistance = distance;
                    travelStalledLegs = 0;
                }
                else if (++travelStalledLegs >= MaximumStalledLegs)
                {
                    travelGoal = null;
                    body.Stand();
                    return null;
                }

                body.WalkTo(goal);
                return false;
            default:
                return false;
        }
    }

    protected void ForgetTravel()
    {
        travelGoal = null;
    }

    protected TimeSpan RandomBetween(TimeSpan minimum, TimeSpan maximum)
    {
        return minimum + TimeSpan.FromTicks((long)((maximum - minimum).Ticks * Random.NextDouble()));
    }

    // A random point of an area that has walkable ground, or null after a few tries.
    protected SimulationVector3? RandomGroundIn(IPlayingBotBody body, PlayingRectangle area)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var x = area.MinimumX + ((area.MaximumX - area.MinimumX) * (float)Random.NextDouble());
            var z = area.MinimumZ + ((area.MaximumZ - area.MinimumZ) * (float)Random.NextDouble());
            if (body.TryFindGround(x, z, out var ground))
            {
                return ground;
            }
        }

        return null;
    }

    protected static float PlanarDistance(SimulationVector3 left, SimulationVector3 right)
    {
        var dx = left.X - right.X;
        var dz = left.Z - right.Z;
        return MathF.Sqrt((dx * dx) + (dz * dz));
    }

    protected static SimulationVector3 ToVector(SimulationBotPosition position)
    {
        return new SimulationVector3(position.X, position.Y, position.Z);
    }

    protected static bool IsDead(IPlayingBotBody body)
    {
        return body.View.OwnCombat?.LifeState == RealtimeCombatLifeState.Dead;
    }
}

// Walks to random reachable destinations over the whole World with pauses, as the active
// simulation bots do: about one behavior interval of walking away, at most 60 m, inside the
// World, and sometimes it just stands.
public sealed class WalkerRole : PlayingRoleBase
{
    public const double IdleChance = 0.25;
    public static readonly TimeSpan MinimumBehavior = TimeSpan.FromSeconds(4);
    public static readonly TimeSpan MaximumBehavior = TimeSpan.FromSeconds(12);
    private const float MaximumWalkDistance = 60f;
    private const float BoundsMargin = 2f;

    private TimeSpan behaviorEnd;
    private bool walking;

    public WalkerRole(PlayingGround? home, bool teleport, Random random)
        : base(home, teleport, random)
    {
    }

    public override PlayingRole Role => PlayingRole.Walker;

    public override string State => walking ? "walking" : "pausing";

    protected override void OnOutcome(IPlayingBotBody body, PlayingIntentOutcome outcome, TimeSpan now)
    {
        if (outcome.Operation == PlayingOperations.Teleport)
        {
            OnTeleportOutcome(outcome);
        }
    }

    protected override void Act(IPlayingBotBody body, TimeSpan now)
    {
        if (IsDead(body))
        {
            // The worker respawns the character at the World spawn; it walks on from there.
            walking = false;
            return;
        }

        if (walking
            && now < behaviorEnd
            && body.MoveState is PlayingMoveState.Planning or PlayingMoveState.Walking)
        {
            return;
        }

        if (!walking && now < behaviorEnd)
        {
            return;
        }

        var duration = RandomBetween(MinimumBehavior, MaximumBehavior);
        behaviorEnd = now + duration;
        if (Random.NextDouble() < IdleChance || body.View.Session is not { } session)
        {
            walking = false;
            body.Stand();
            return;
        }

        var settings = session.MovementSettings;
        var angle = Random.NextDouble() * Math.PI * 2d;
        var distance = Math.Min(settings.WalkSpeed * duration.TotalSeconds, MaximumWalkDistance);
        var x = (float)Math.Clamp(
            body.Position.X + (Math.Sin(angle) * distance),
            settings.MinimumX + BoundsMargin,
            settings.MaximumX - BoundsMargin);
        var z = (float)Math.Clamp(
            body.Position.Z + (Math.Cos(angle) * distance),
            settings.MinimumZ + BoundsMargin,
            settings.MaximumZ - BoundsMargin);
        walking = true;
        body.WalkTo(new SimulationVector3(x, body.Position.Y, z));
    }
}
