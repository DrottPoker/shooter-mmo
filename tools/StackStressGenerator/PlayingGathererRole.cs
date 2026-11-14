using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.Tools.SimulationBotClient;
using Mmorpg.WorldData.Actors;

namespace Mmorpg.Tools.StackStressGenerator;

// Works the resource nodes of its node field like a player: it equips the tool of its kit,
// picks the nearest node with charges that its tool works, walks to a spot within reach of the
// node with a clear line of sight, opens it, starts gathering, and stands still while the
// yields arrive, until the node depletes and the worker closes the interaction; then it picks
// the next node. A node that depleted before it got there counts as found empty, a field
// without charges waits for their respawn, and a node behind cover is given up for a while.
// When its slots or carry capacity run low it walks to the town's vendor, sells its yields, and
// walks back. A Mob that attacks it makes it close the node and flee. After a death it recovers
// its corpse; when the corpse is lost it goes on as a walker.
public sealed class GathererRole : PlayingRoleBase
{
    // A node counts for the field this far outside its rectangle.
    public const float FieldMargin = 4f;

    // A node that depleted is passed over this long, and one behind cover or out of reach longer.
    public static readonly TimeSpan EmptyNodeIgnore = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan BlockedNodeIgnore = TimeSpan.FromSeconds(60);

    // A started gathering that sends no state within this long is closed and tried elsewhere,
    // and a depleted node whose interaction the worker did not close within the second is
    // closed by the gatherer.
    public static readonly TimeSpan StartWait = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan DepletedCloseWait = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan MaximumFlee = TimeSpan.FromSeconds(15);

    // The gatherer stands this far outside a node's interaction box, inside the start range.
    private const float MinimumReach = 0.5f;
    private const float MaximumReach = 1.6f;
    private const float StandArrival = 0.5f;
    private const float FleeDistance = 30f;
    private const int MaximumStandAttempts = 3;

    private readonly Dictionary<ulong, TimeSpan> ignored = [];
    private GathererState state = GathererState.Seek;
    private TimeSpan stateSince;
    private WalkerRole? walker;
    private PlayingCorpseRecovery? recovery;
    private ulong node;
    private SimulationVector3 standPoint;
    private int standAttempts;
    private bool fieldEmpty;
    private bool fleeAfterClose;
    private SimulationVector3 fleeFrom;
    private TimeSpan? depletedSince;

    public GathererRole(PlayingGround home, bool teleport, Random random, PlayingGround? town = null)
        : base(home ?? throw new ArgumentNullException(nameof(home)), teleport, random, town)
    {
    }

    private enum GathererState
    {
        Seek,
        Approach,
        Opening,
        Starting,
        Gathering,
        Closing,
        Flee,
        Dead
    }

    public override PlayingRole Role => PlayingRole.Gatherer;

    public override string State => walker is not null
        ? "walker"
        : VendorTripState is { } trip ? "vendor-" + trip : state.ToString().ToLowerInvariant();

    public bool ContinuesAsWalker => walker is not null;

    public ulong Node => node;

    protected override void OnOutcome(IPlayingBotBody body, PlayingIntentOutcome outcome, TimeSpan now)
    {
        if (recovery is not null && PlayingCorpseRecovery.Owns(outcome.Operation))
        {
            recovery.OnOutcome(body, outcome, now);
            return;
        }

        switch (outcome.Operation)
        {
            case PlayingOperations.Teleport:
                OnTeleportOutcome(outcome);
                break;
            case PlayingOperations.Equip when !outcome.Succeeded:
                RequireEquip();
                break;
            case PlayingOperations.GatherOpen when state == GathererState.Opening:
                if (outcome.Succeeded)
                {
                    Enter(GathererState.Starting, now);
                }
                else
                {
                    GiveUpNode(body, outcome.Code, now);
                }

                break;
            case PlayingOperations.GatherStart when state == GathererState.Starting:
                if (outcome.Succeeded)
                {
                    Enter(GathererState.Gathering, now);
                }
                else
                {
                    GiveUpNode(body, outcome.Code, now);
                    if (body.View.OpenInteraction is not null)
                    {
                        Enter(GathererState.Closing, now);
                    }
                }

                break;
            case PlayingOperations.GatherClose:
                if (state == GathererState.Closing)
                {
                    Enter(fleeAfterClose ? GathererState.Flee : GathererState.Seek, now);
                    fleeAfterClose = false;
                }

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

        if (IsDead(body) && state != GathererState.Dead)
        {
            recovery = new PlayingCorpseRecovery(body.Position, body.View.OwnCombat!.LifeSequence);
            node = 0;
            ForgetTravel();
            Enter(GathererState.Dead, now);
            return;
        }

        if (state is not (GathererState.Dead or GathererState.Flee or GathererState.Closing)
            && FindAttacker(body) is { } attacker)
        {
            Flee(body, attacker, now);
            return;
        }

        switch (state)
        {
            case GathererState.Seek:
                Seek(body, now);
                break;
            case GathererState.Approach:
                Approach(body, now);
                break;
            case GathererState.Starting:
                StartGathering(body, now);
                break;
            case GathererState.Gathering:
                Gather(body, now);
                break;
            case GathererState.Closing:
                Close(body, now);
                break;
            case GathererState.Flee:
                Escape(body, now);
                break;
            case GathererState.Dead:
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
            else
            {
                // The tool is gone, so the gatherer cannot gather any more.
                body.Stand();
                walker = new WalkerRole(null, teleport: false, Random);
            }

            return;
        }

        if (body.View.OpenInteraction is not null)
        {
            // An interaction left open, such as by a stop the bot did not see, closes first.
            Enter(GathererState.Closing, now);
            Close(body, now);
            return;
        }

        if (NeedsToSell(body) && StartVendorTrip(body, now))
        {
            return;
        }

        var area = Home!.Area;
        var choice = NearestNode(body, now, out var knowsNodes);
        if (choice is null)
        {
            if (knowsNodes)
            {
                // Every node of the field waits for its respawn.
                if (!fieldEmpty)
                {
                    fieldEmpty = true;
                    body.Count(PlayingCounter.NodeFieldsEmpty);
                }

                return;
            }

            // No node in view yet: walk into the field and look.
            if (body.MoveState is not (PlayingMoveState.Planning or PlayingMoveState.Walking)
                && RandomGroundIn(body, area) is { } point)
            {
                body.WalkTo(point);
            }

            return;
        }

        fieldEmpty = false;
        node = choice.EntityId;
        standAttempts = 0;
        if (!PickStandPoint(body, choice, now))
        {
            return;
        }

        body.Count(PlayingCounter.GatherAttempts);
        Enter(GathererState.Approach, now);
        Approach(body, now);
    }

    private void Approach(IPlayingBotBody body, TimeSpan now)
    {
        if (!body.View.Actors.TryGetValue(node, out var actor))
        {
            Enter(GathererState.Seek, now);
            return;
        }

        if (!actor.IsActive)
        {
            // Another gatherer took the last charge while this one walked there.
            body.Count(PlayingCounter.NodesFoundEmpty);
            Ignore(EmptyNodeIgnore, now);
            body.Stand();
            Enter(GathererState.Seek, now);
            return;
        }

        switch (TravelTo(body, standPoint, StandArrival))
        {
            case false:
                return;
            case null:
                body.Count(PlayingCounter.NodesBlockedFromSight);
                Ignore(BlockedNodeIgnore, now);
                Enter(GathererState.Seek, now);
                return;
        }

        body.Stand();
        var center = PlayingVendorVisit.BoundsCenter(body, actor);
        if (!PlayingVendorVisit.IsWithinReach(body, actor, WorldInteractionRules.AuthoritativeStartRange - 0.2f)
            || !body.HasLineOfSight(body.Position, center))
        {
            PickStandPoint(body, actor, now);
            return;
        }

        if (body.OpenInteraction(actor, PlayingOperations.GatherOpen))
        {
            Enter(GathererState.Opening, now);
        }
    }

    private void StartGathering(IPlayingBotBody body, TimeSpan now)
    {
        if (body.View.OpenInteraction is not { } open || open.TargetEntityId != node)
        {
            Enter(GathererState.Seek, now);
            return;
        }

        body.CapabilityAction(
            PlayingOperations.GatherStart,
            new PlayingCapabilityAction(
                RealtimeWorldActorCapabilityKind.Gathering,
                RealtimeCapabilityActionKind.StartGathering));
    }

    private void Gather(IPlayingBotBody body, TimeSpan now)
    {
        if (body.View.OpenInteraction is not { } open || open.TargetEntityId != node)
        {
            // The worker closed the interaction, as it does once the node depleted.
            Enter(GathererState.Seek, now);
            return;
        }

        var gathering = body.View.Gathering;
        var stopped = gathering is not null
            && gathering.InteractionSessionId == open.InteractionSessionId
            && gathering.Kind == RealtimeGatheringStateKind.Stopped;
        if (stopped && gathering!.Code == "resource_node_depleted")
        {
            // The worker closes the interaction of a depleted node itself, as the client sees.
            depletedSince ??= now;
            if (now - depletedSince.Value < DepletedCloseWait)
            {
                return;
            }
        }

        if (stopped
            || (gathering is null && now - stateSince > StartWait)
            || NeedsToSell(body))
        {
            Enter(GathererState.Closing, now);
            Close(body, now);
        }
    }

    private void Close(IPlayingBotBody body, TimeSpan now)
    {
        if (body.View.OpenInteraction is null)
        {
            Enter(fleeAfterClose ? GathererState.Flee : GathererState.Seek, now);
            fleeAfterClose = false;
            return;
        }

        body.CloseInteraction(PlayingOperations.GatherClose);
    }

    private void Flee(IPlayingBotBody body, SimulationBotWorldActor attacker, TimeSpan now)
    {
        body.Count(PlayingCounter.NonFighterFlees);
        fleeFrom = ToVector(attacker.Position);
        ForgetTravel();
        if (body.View.OpenInteraction is not null)
        {
            fleeAfterClose = true;
            Enter(GathererState.Closing, now);
            Close(body, now);
            return;
        }

        Enter(GathererState.Flee, now);
        Escape(body, now);
    }

    private void Escape(IPlayingBotBody body, TimeSpan now)
    {
        var dx = body.Position.X - fleeFrom.X;
        var dz = body.Position.Z - fleeFrom.Z;
        var length = MathF.Max(0.001f, MathF.Sqrt((dx * dx) + (dz * dz)));
        var goal = new SimulationVector3(
            fleeFrom.X + (dx / length * FleeDistance),
            body.Position.Y,
            fleeFrom.Z + (dz / length * FleeDistance));
        if (TravelTo(body, goal) != false || now - stateSince > MaximumFlee)
        {
            body.Stand();
            ForgetTravel();
            Enter(GathererState.Seek, now);
        }
    }

    private void Recover(IPlayingBotBody body, TimeSpan now)
    {
        switch (recovery!.Think(body, now, (goal, arrival) => TravelTo(body, goal, arrival)))
        {
            case PlayingVisitStatus.Done:
                recovery = null;
                ForgetTravel();
                if (HasKitToEquip(body))
                {
                    RequireEquip();
                }

                Enter(GathererState.Seek, now);
                break;
            case PlayingVisitStatus.Failed:
                recovery = null;
                body.Count(PlayingCounter.CorpsesLost);
                body.Stand();
                walker = new WalkerRole(null, teleport: false, Random);
                break;
        }
    }

    private bool PickStandPoint(IPlayingBotBody body, SimulationBotWorldActor actor, TimeSpan now)
    {
        if (++standAttempts > MaximumStandAttempts
            || PlayingVendorVisit.FindStandPoint(body, actor, Random, MinimumReach, MaximumReach) is not { } point)
        {
            // Cover or the slope leaves no spot within reach with a clear line of sight.
            body.Count(PlayingCounter.NodesBlockedFromSight);
            Ignore(BlockedNodeIgnore, now);
            Enter(GathererState.Seek, now);
            return false;
        }

        standPoint = point;
        ForgetTravel();
        return true;
    }

    private void GiveUpNode(IPlayingBotBody body, string? code, TimeSpan now)
    {
        switch (code)
        {
            case "world_actor_unavailable":
            case "world_interaction_target_changed":
            case "resource_node_depleted":
                body.Count(PlayingCounter.NodesFoundEmpty);
                Ignore(EmptyNodeIgnore, now);
                break;
            case "world_interaction_line_of_sight_blocked":
            case "world_interaction_out_of_range":
                body.Count(PlayingCounter.NodesBlockedFromSight);
                Ignore(BlockedNodeIgnore, now);
                break;
            default:
                Ignore(BlockedNodeIgnore, now);
                break;
        }

        Enter(GathererState.Seek, now);
    }

    private void Ignore(TimeSpan duration, TimeSpan now)
    {
        if (node != 0)
        {
            ignored[node] = now + duration;
        }
    }

    // The nearest node of the field with charges that the kit's tool works, and whether the
    // field shows any node of that kind at all.
    private SimulationBotWorldActor? NearestNode(IPlayingBotBody body, TimeSpan now, out bool knowsNodes)
    {
        knowsNodes = false;
        var area = Home!.Area;
        SimulationBotWorldActor? nearest = null;
        var nearestDistance = float.MaxValue;
        foreach (var actor in body.View.Actors.Values)
        {
            if (actor.Kind != RealtimeWorldActorKind.ResourceNode
                || !area.Contains(actor.Position.X, actor.Position.Z, FieldMargin)
                || body.GameData.RequiredToolKind(actor.ActorDefinitionId) is not { } toolKind
                || !body.Kit.Items.Any(kitItem => body.GameData.HasToolKind(kitItem.DefinitionId, toolKind)))
            {
                continue;
            }

            knowsNodes = true;
            if (!actor.IsActive || (ignored.TryGetValue(actor.EntityId, out var until) && now < until))
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

    private void Enter(GathererState next, TimeSpan now)
    {
        state = next;
        stateSince = now;
        depletedSince = null;
    }
}
