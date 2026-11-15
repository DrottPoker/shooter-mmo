using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.Entities;

namespace Mmorpg.SimulationWorker.Realtime;

// The development admin tools of the Admin menu: a player's walk speed, a teleport, full
// health, and invulnerability. Only a worker whose options allow them carries them out, and
// never for a synthetic bot. The walk speed and invulnerability last as long as the
// character's entity, and the owner receives them after every join and every change. A teleport
// to a place whose collision chunks still load waits for them and completes on a later tick.
// Loop-confined.
internal sealed class AdminIntentHandler(
    SimulationWorkerConfig config,
    DevelopmentAdminOptions options,
    RealtimePeerRegistry peers,
    RealtimePeerSender sender,
    EntityPresenceReplicator presence,
    CombatSystem combat,
    ICollisionWorld collisionWorld,
    CollisionChunkGate collisionChunks) : IRealtimeCompletionHandler
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

    private static readonly RealtimeRejection DeliveryRejection = new(
        "invalid_admin_delivery",
        "Admin intents require the reliable ordered control channel.");

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

        var waiting = false;
        var failure = !options.Enabled || entity.Session.IsSyntheticBot
            ? ToolsDisabled
            : Apply(context, entity, intent, out waiting);
        if (!waiting)
        {
            SendResult(peer, entity, intent, failure);
        }
    }

    // A teleport whose destination loaded runs again as it would have at once, and its result
    // goes to the player that asked when it still plays the same entity.
    public void Complete(RealtimeOperationResult completion)
    {
        if (completion is not AdminTeleportReady ready
            || !peers.TryGetCurrentPeer(ready.Context, out var peer)
            || !peers.TryGetBoundPlayer(ready.Context, out var entity)
            || entity.NetworkEntityId != ready.EntityId)
        {
            return;
        }

        var failure = Teleport(ready.Context, entity, ready.Intent, out var waiting);
        if (!waiting)
        {
            SendResult(peer, entity, ready.Intent, failure);
        }
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
        if (!collisionChunks.IsLoadedAround(destination))
        {
            collisionChunks.WaitFor(destination, this, new AdminTeleportReady(context, entity.NetworkEntityId, intent));
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
}
