using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.Entities;
using Mmorpg.SimulationWorker.WorldCollision;

namespace Mmorpg.SimulationWorker.Realtime;

// The development admin tools of the Admin menu: a player's walk speed, a teleport, full
// health, and invulnerability. Only a worker whose options allow them carries them out, and
// never for a synthetic bot. The walk speed and invulnerability last as long as the
// character's entity, and the owner receives them after every join and every change.
// Loop-confined.
internal sealed class AdminIntentHandler(
    SimulationWorkerConfig config,
    DevelopmentAdminOptions options,
    RealtimePeerRegistry peers,
    RealtimePeerSender sender,
    EntityPresenceReplicator presence,
    CombatSystem combat,
    ICollisionWorld collisionWorld,
    WorldCollisionStreamingStore? collisionStreamingStore)
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

        var failure = !options.Enabled || entity.Session.IsSyntheticBot
            ? ToolsDisabled
            : Apply(entity, intent);
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

    private RealtimeError? Apply(PlayerSimulationEntity entity, RealtimeAdminIntent intent)
    {
        switch (intent.Kind)
        {
            case RealtimeAdminIntentKind.SetMovementSpeed:
                entity.Movement.SetWalkSpeedPercent(intent.SpeedPercent);
                return null;
            case RealtimeAdminIntentKind.Teleport:
                return Teleport(entity, intent.DestinationX, intent.DestinationY, intent.DestinationZ);
            case RealtimeAdminIntentKind.RestoreHealth:
                return combat.TryRestoreHealthAndMana(entity) ? null : CharacterDead;
            case RealtimeAdminIntentKind.SetInvulnerable:
                return combat.TrySetInvulnerable(entity, intent.Enabled) ? null : CombatUnavailable;
            default:
                throw new InvalidOperationException($"Admin intent kind {intent.Kind} has no handler.");
        }
    }

    // Moves a living player onto walkable ground at the destination, standing still, and shows
    // it there to everyone who sees it, as a respawn does.
    private RealtimeError? Teleport(PlayerSimulationEntity entity, float x, float y, float z)
    {
        if (combat.IsMovementBlocked(entity))
        {
            return CharacterDead;
        }

        var settings = config.MovementSimulation;
        if (x < settings.MinimumX || x > settings.MaximumX || z < settings.MinimumZ || z > settings.MaximumZ)
        {
            return OutsideWorld;
        }

        collisionStreamingStore?.EnsureLoaded(new SimulationVector3(x, y, z));
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
