using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.Tools.SimulationBotClient;

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
// the join, pauses after a rejected intent, and walking toward a far point in legs.
public abstract class PlayingRoleBase : IPlayingRole
{
    // A rejected intent is followed by a pause of this long, so no bot retries in a loop.
    public static readonly TimeSpan MinimumRejectionPause = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan MaximumRejectionPause = TimeSpan.FromSeconds(4);

    // A point this close counts as reached.
    protected const float ArrivalDistance = 1.5f;

    // A walk toward a far point gives up after this many legs that brought it no closer.
    private const int MaximumStalledLegs = 3;

    private readonly PlayingGround? home;
    private readonly bool teleport;
    private bool equipped;
    private bool teleported;
    private int teleportAttempts;
    private SimulationVector3? travelGoal;
    private float travelBestDistance;
    private int travelStalledLegs;

    protected PlayingRoleBase(PlayingGround? home, bool teleport, Random random)
    {
        this.home = home;
        this.teleport = teleport && home is not null;
        Random = random ?? throw new ArgumentNullException(nameof(random));
    }

    public abstract PlayingRole Role { get; }

    public abstract string State { get; }

    protected Random Random { get; }

    protected PlayingGround? Home => home;

    protected TimeSpan PauseUntil { get; set; }

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

            OnOutcome(body, outcome, now);
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

        Act(body, now);
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
