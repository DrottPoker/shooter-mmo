using Microsoft.Extensions.Logging;
using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.Entities;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Quests;
using Mmorpg.SimulationWorker.Sessions;

namespace Mmorpg.SimulationWorker.Realtime;

internal sealed record AdminProgressionCompleted(
    RealtimePeerContext PeerContext,
    ActiveSimulationSession Session,
    RealtimeAdminIntent Intent,
    QuestOperationOutcome Outcome)
    : RealtimeOperationResult(PeerContext);

// The development admin tools of the Admin menu: a player's walk speed, a teleport, full
// health, invulnerability, its level, and experience. Only a worker whose options allow them
// carries them out, and never for a synthetic bot. The walk speed and invulnerability last as
// long as the character's entity, and the owner receives them after every join and every change.
// A teleport to a place whose collision chunks still load waits for them and completes on a later
// tick. A newer teleport of the same character replaces a waiting one, which is answered as
// superseded, so the character ends where the player asked last. The level and the experience go
// through GameService on the character's authority lane, as the admin quest tools do, also for a
// dead character; the changed progression reaches the client before the result.
// Loop-confined; the GameService call runs off the loop.
internal sealed class AdminIntentHandler(
    SimulationWorkerConfig config,
    DevelopmentAdminOptions options,
    RealtimePeerRegistry peers,
    RealtimePeerSender sender,
    EntityPresenceReplicator presence,
    CombatSystem combat,
    ICollisionWorld collisionWorld,
    CollisionChunkGate collisionChunks,
    CarryStateSynchronizer carrySync,
    RealtimeSessionTeardown teardown,
    OperationCompletionTargets targets,
    AuthorityOperationScheduler authorityScheduler,
    RealtimeOperationTracker operationTracker,
    RealtimeCompletionQueue completions,
    QuestOperationExecutor? executor,
    ProgressionReplicator progression,
    ILogger logger,
    ICombatOperationGate? combatGate = null) : IRealtimeCompletionHandler
{
    public const string TeleportedReason = "teleported";

    private static readonly RealtimeError ToolsDisabled = new(
        "admin_tools_disabled",
        "Only a Development worker allows the admin tools.");

    private static readonly RealtimeError CharacterDead = new(
        "admin_character_dead",
        "A dead character cannot use this admin tool.");

    private static readonly RealtimeError CombatUnavailable = new(
        "admin_combat_unavailable",
        "The character takes no part in combat.");

    private static readonly RealtimeError OutsideWorld = new(
        "admin_teleport_outside_world",
        "The destination is outside the World.");

    private static readonly RealtimeError NoGround = new(
        "admin_teleport_no_ground",
        "No walkable ground is at the destination.");

    private static readonly RealtimeError TeleportSuperseded = new(
        "admin_teleport_superseded",
        "A newer teleport replaced this one.");

    private static readonly RealtimeError ProgressionUnavailable = new(
        "admin_progression_unavailable",
        "This worker runs no progression for the character.");

    private static readonly RealtimeError GameServiceUnavailable = new(
        GameServiceAvailability.UnavailableCode,
        GameServiceAvailability.UnavailableMessage);

    private static readonly RealtimeError QueueFull = new(
        "admin_intent_queue_full",
        "Too many authority operations are waiting for this peer.");

    private static readonly RealtimeRejection DeliveryRejection = new(
        "invalid_admin_delivery",
        "Admin intents require the reliable ordered control channel.");

    // Per entity with a waiting teleport: its newest teleport request, and how many wait.
    private readonly Dictionary<ulong, WaitingTeleports> waitingTeleports = [];

    public static void AddRoute(RealtimeMessageRoutes routes, RealtimeMessageHandler handler)
    {
        routes.AddAuthenticated(RealtimeMessageType.AdminIntent, DeliveryRejection, handler);
    }

    // A joined player learns its admin state, which also tells its client that the tools are on.
    public void OnPlayerJoined(RealtimePeerContext context, PlayerSimulationEntity entity)
    {
        if (options.Enabled
            && !entity.Session.IsSyntheticBot
            && peers.TryGetCurrentPeer(context, out var peer))
        {
            SendState(peer, entity);
        }
    }

    public void HandleIntent(in RealtimeInboundMessage message)
    {
        var peer = message.Peer;
        var context = message.Context;
        if (!peers.TryGetBoundPlayer(context, out var entity))
        {
            sender.RejectProtocol(peer, context, "admin_unavailable", "Admin tools are not active for this peer.");
            return;
        }

        if (!RealtimeProtocol.TryDecodeAdminIntent(message.Packet, out var intent, out var error))
        {
            sender.RejectProtocol(peer, context, "invalid_admin_intent", error);
            return;
        }

        if (!options.Enabled || entity.Session.IsSyntheticBot)
        {
            SendResult(peer, entity, intent, ToolsDisabled);
            return;
        }

        if (IsProgressionKind(intent.Kind))
        {
            if (RefuseProgression(context) is { } refusal)
            {
                SendResult(peer, entity, intent, refusal);
                return;
            }

            // Spent arrows commit first, so the admin tool sees the item state GameService has.
            combatGate?.BeforeClientOperation(context);
            authorityScheduler.Enqueue(context, new QueuedAdminProgression(this, intent));
            return;
        }

        var failure = Apply(context, entity, intent, out var waiting);
        if (!waiting)
        {
            SendResult(peer, entity, intent, failure);
        }
    }

    public void Complete(RealtimeOperationResult completion)
    {
        switch (completion)
        {
            case AdminTeleportReady ready:
                CompleteTeleport(ready);
                break;
            case AdminProgressionCompleted progressed:
                FinalizeProgression(progressed);
                break;
        }
    }

    private static bool IsProgressionKind(RealtimeAdminIntentKind kind)
    {
        return kind is RealtimeAdminIntentKind.SetLevel or RealtimeAdminIntentKind.GrantExperience;
    }

    // Why a level or experience tool cannot queue: no executor, GameService does not answer, or
    // the peer's lane is full.
    private RealtimeError? RefuseProgression(RealtimePeerContext context)
    {
        if (executor is null)
        {
            return ProgressionUnavailable;
        }

        if (authorityScheduler.RefusesDurableIntent(DurableIntentKinds.AdminIntent))
        {
            return GameServiceUnavailable;
        }

        return context.AuthorityOperationQueue.Count >= RealtimePeerContext.MaximumQueuedAuthorityOperations
            ? QueueFull
            : null;
    }

    private void StartProgression(RealtimePeerContext context, ActiveSimulationSession session, RealtimeAdminIntent intent)
    {
        combatGate?.ClientOperationStarting(context, false);
        operationTracker.Track(RunProgressionAsync(context, session, intent));
    }

    private async Task RunProgressionAsync(RealtimePeerContext context, ActiveSimulationSession session, RealtimeAdminIntent intent)
    {
        QuestOperationOutcome outcome;
        try
        {
            outcome = await executor!.ExecuteAsync(session, ProgressionOperationOf(intent), CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "[SIMULATION] Unhandled error while running an admin intent for simulation session {SimulationSessionId}.",
                session.SimulationSessionId);
            outcome = QuestOperationOutcome.Rejected(
                "admin_intent_failed",
                "SimulationWorker could not complete the admin tool.");
        }

        completions.Post(this, new AdminProgressionCompleted(context, session, intent, outcome));
    }

    private static QuestOperation ProgressionOperationOf(RealtimeAdminIntent intent)
    {
        return intent.Kind switch
        {
            RealtimeAdminIntentKind.SetLevel => new QuestOperation(
                intent.OperationId,
                QuestOperationKinds.AdminSetLevel,
                null,
                Level: intent.Level),
            RealtimeAdminIntentKind.GrantExperience => new QuestOperation(
                intent.OperationId,
                QuestOperationKinds.AdminGrantExperience,
                null,
                Experience: intent.Experience),
            _ => throw new InvalidOperationException($"Admin intent kind {intent.Kind} has no progression operation.")
        };
    }

    // The executor ran the tool off the loop, whether or not the peer that sent it is still there.
    // Here the progression reaches the character and the peer that plays it now, before the result
    // that goes to the peer that sent the tool. A session that cannot go on ends.
    private void FinalizeProgression(AdminProgressionCompleted completed)
    {
        combatGate?.ClientOperationFinished(completed.Session);
        authorityScheduler.Release(completed.Context);
        if (!targets.TryResolve(completed.Context, completed.Session, out var target))
        {
            return;
        }

        var requester = target.Requester;
        var outcome = completed.Outcome;
        if (outcome.ShouldDisconnect)
        {
            if (requester is not null)
            {
                SendProgressionResult(requester, completed.Intent, outcome);
            }

            teardown.EndForAuthorityFailure(target, outcome.Code, outcome.Message);
            return;
        }

        if (target.Owner is { } owner && outcome.ItemStateChange is not null)
        {
            sender.SendControl(owner, RealtimeProtocol.EncodeOwnerItemStateChanged(outcome.ItemStateChange));
        }

        if (outcome.Progression is { } change)
        {
            progression.Apply(target.Session, target.Owner, change, outcome.ProgressionReason);
        }

        if (target.Owner is { } questLogOwner && outcome.QuestLogChange is not null)
        {
            sender.SendControl(questLogOwner, RealtimeProtocol.EncodeQuestLogChanged(outcome.QuestLogChange));
        }

        carrySync.ApplyLatest(target.Entity);
        if (requester is not null)
        {
            SendProgressionResult(requester, completed.Intent, outcome);
        }

        authorityScheduler.StartNext(completed.Context);
    }

    private void CancelProgression(RealtimePeerContext context, RealtimeAdminIntent intent, string code, string message)
    {
        if (peers.TryGetCurrentPeer(context, out var peer))
        {
            SendProgressionResult(peer, intent, QuestOperationOutcome.Rejected(code, message));
        }
    }

    private void SendProgressionResult(IRealtimePeerConnection peer, RealtimeAdminIntent intent, QuestOperationOutcome outcome)
    {
        sender.SendControl(peer, RealtimeProtocol.EncodeAdminIntentResult(new RealtimeAdminIntentResult(
            intent.OperationId,
            intent.Kind,
            outcome.Succeeded,
            outcome.Succeeded ? null : new RealtimeError(outcome.Code, outcome.Message))));
    }

    // A teleport whose destination loaded runs again as it would have at once, and its result
    // goes to the player that asked when it still plays the same entity.
    private void CompleteTeleport(AdminTeleportReady ready)
    {
        var newest = StopWaiting(ready.EntityId, ready.Intent.OperationId);
        if (!peers.TryGetCurrentPeer(ready.Context, out var peer)
            || !peers.TryGetBoundPlayer(ready.Context, out var entity)
            || entity.NetworkEntityId != ready.EntityId)
        {
            return;
        }

        // A teleport asked for after this one already decided where the character is.
        var waiting = false;
        var failure = newest ? Teleport(ready.Context, entity, ready.Intent, out waiting) : TeleportSuperseded;
        if (!waiting)
        {
            SendResult(peer, entity, ready.Intent, failure);
        }
    }

    // One teleport of the entity no longer waits. Returns whether no newer teleport was asked for
    // since it began to wait.
    private bool StopWaiting(ulong entityId, Guid operationId)
    {
        if (!waitingTeleports.TryGetValue(entityId, out var teleports))
        {
            return true;
        }

        if (teleports.Count <= 1)
        {
            waitingTeleports.Remove(entityId);
        }
        else
        {
            waitingTeleports[entityId] = teleports with { Count = teleports.Count - 1 };
        }

        return teleports.NewestOperationId == operationId;
    }

    private void SendResult(
        IRealtimePeerConnection peer,
        PlayerSimulationEntity entity,
        RealtimeAdminIntent intent,
        RealtimeError? failure)
    {
        sender.SendControl(peer, RealtimeProtocol.EncodeAdminIntentResult(new RealtimeAdminIntentResult(
            intent.OperationId,
            intent.Kind,
            failure is null,
            failure)));
        if (failure is null
            && intent.Kind is RealtimeAdminIntentKind.SetMovementSpeed or RealtimeAdminIntentKind.SetInvulnerable)
        {
            SendState(peer, entity);
        }
    }

    private RealtimeError? Apply(
        RealtimePeerContext context,
        PlayerSimulationEntity entity,
        RealtimeAdminIntent intent,
        out bool waiting)
    {
        waiting = false;
        switch (intent.Kind)
        {
            case RealtimeAdminIntentKind.SetMovementSpeed:
                entity.Movement.SetWalkSpeedPercent(intent.SpeedPercent);
                return null;
            case RealtimeAdminIntentKind.Teleport:
                return Teleport(context, entity, intent, out waiting);
            case RealtimeAdminIntentKind.RestoreHealth:
                return combat.TryRestoreHealthAndMana(entity) ? null : CharacterDead;
            case RealtimeAdminIntentKind.SetInvulnerable:
                return combat.TrySetInvulnerable(entity, intent.Enabled) ? null : CombatUnavailable;
            case RealtimeAdminIntentKind.SetLevel:
            case RealtimeAdminIntentKind.GrantExperience:
                throw new InvalidOperationException($"Admin intent kind {intent.Kind} runs through GameService.");
            default:
                throw new InvalidOperationException($"Admin intent kind {intent.Kind} has no handler.");
        }
    }

    // Moves a living player onto walkable ground at the destination, standing still, and shows
    // it there to everyone who sees it, as a respawn does. While the collision chunks of the
    // destination still load, it waits for them and sends no result yet.
    private RealtimeError? Teleport(
        RealtimePeerContext context,
        PlayerSimulationEntity entity,
        RealtimeAdminIntent intent,
        out bool waiting)
    {
        waiting = false;
        if (combat.IsMovementBlocked(entity))
        {
            return CharacterDead;
        }

        var x = intent.DestinationX;
        var y = intent.DestinationY;
        var z = intent.DestinationZ;
        var settings = config.MovementSimulation;
        if (x < settings.MinimumX || x > settings.MaximumX || z < settings.MinimumZ || z > settings.MaximumZ)
        {
            return OutsideWorld;
        }

        var destination = new SimulationVector3(x, y, z);
        var entityId = entity.NetworkEntityId;
        waitingTeleports.TryGetValue(entityId, out var teleports);
        if (!collisionChunks.IsLoadedAround(destination))
        {
            collisionChunks.WaitFor(destination, this, new AdminTeleportReady(context, entityId, intent));
            waitingTeleports[entityId] = new WaitingTeleports(intent.OperationId, teleports.Count + 1);
            waiting = true;
            return null;
        }

        var state = PlayerMovementSimulation.CreateInitialState(
            settings,
            collisionWorld,
            x,
            y,
            z,
            entity.Movement.State.YawDegrees);
        if (!state.IsGrounded)
        {
            return NoGround;
        }

        // A teleport that moves the character replaces every one that still waits.
        if (teleports.Count > 0)
        {
            waitingTeleports[entityId] = teleports with { NewestOperationId = intent.OperationId };
        }

        entity.Movement.Teleport(state);
        presence.RespawnEntityForViewers(entity, TeleportedReason);
        return null;
    }

    private void SendState(IRealtimePeerConnection peer, PlayerSimulationEntity entity)
    {
        sender.SendControl(peer, RealtimeProtocol.EncodeAdminStateChanged(new RealtimeAdminStateChanged(
            (ushort)entity.Movement.WalkSpeedPercent,
            combat.IsAdminInvulnerable(entity))));
    }

    private readonly record struct WaitingTeleports(Guid NewestOperationId, int Count);

    private sealed record QueuedAdminProgression(AdminIntentHandler Owner, RealtimeAdminIntent Intent)
        : IRealtimeAuthorityOperation
    {
        public string? DurableIntentKind => DurableIntentKinds.AdminIntent;

        public void Start(RealtimePeerContext context, PlayerSimulationEntity entity, PlayerMovementState state)
        {
            Owner.StartProgression(context, context.Session!, Intent);
        }

        public void Cancel(RealtimePeerContext context, string code, string message)
        {
            Owner.CancelProgression(context, Intent, code, message);
        }
    }
}
