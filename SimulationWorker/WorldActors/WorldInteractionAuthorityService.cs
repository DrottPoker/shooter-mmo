using System.Diagnostics;
using ShooterMmo.GameProtocol;
using ShooterMmo.WorldData.Actors;
using SimulationWorker.Config;
using SimulationWorker.Entities;
using SimulationWorker.Sessions;

namespace SimulationWorker.WorldActors;

public sealed record WorldInteractionAuthorityResult(
    RealtimeWorldInteractionOpened? Opened,
    RealtimeWorldInteractionResult? Result,
    RealtimeWorldInteractionClosed? Closed,
    bool ShouldDisconnect = false)
{
    public bool Succeeded => Opened is not null || Result?.Succeeded == true;
}

public sealed record WorldInteractionLeaseClosure(
    int PeerId,
    RealtimeWorldInteractionClosed Closed);

public sealed class WorldInteractionAuthorityService(
    SimulationWorkerConfig config,
    ActiveSimulationSessionStore sessionStore,
    WorldActorStore actorStore,
    WorldInteractionLeaseRegistry leases,
    WorldActorCapabilityRegistry capabilities,
    WorldActorLineOfSightService lineOfSight,
    WorldActorMetrics metrics)
{
    public WorldInteractionAuthorityResult Process(
        int peerId,
        PlayerSimulationEntity player,
        RealtimeWorldInteractionIntent intent)
    {
        return ProcessAsync(peerId, player, intent, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
    }

    public async Task<WorldInteractionAuthorityResult> ProcessAsync(
        int peerId,
        PlayerSimulationEntity player,
        RealtimeWorldInteractionIntent intent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(intent);
        var started = Stopwatch.GetTimestamp();
        WorldInteractionAuthorityResult result;
        if (intent.SimulationSessionId != player.Session.SimulationSessionId
            || !sessionStore.IsCurrent(player.Session, DateTime.UtcNow))
        {
            result = Reject(
                intent,
                "world_interaction_session_invalid",
                "The active simulation session does not match this interaction request.");
        }
        else if (intent.TargetKind != RealtimeWorldInteractionTargetKind.WorldActor)
        {
            result = Reject(
                intent,
                "world_interaction_invalid",
                "This interaction target must use its established authority protocol.");
        }
        else if (!actorStore.TryGetByEntityId(intent.TargetEntityId, out var actor)
            || actor!.RuntimeActorId != intent.TargetRuntimeId)
        {
            result = Reject(
                intent,
                "world_actor_not_found",
                "The world actor is no longer available.");
        }
        else
        {
            result = await ProcessResolvedAsync(
                peerId,
                player,
                intent,
                actor,
                cancellationToken);
        }

        metrics.RecordInteraction(Stopwatch.GetElapsedTime(started), result.Succeeded);
        metrics.SetActiveInteractions(leases.ListWorldActorLeases().Count);
        return result;
    }

    public IReadOnlyList<WorldInteractionLeaseClosure> Revalidate(
        Func<int, PlayerSimulationEntity?> resolvePlayer)
    {
        ArgumentNullException.ThrowIfNull(resolvePlayer);
        var closures = new List<WorldInteractionLeaseClosure>();
        foreach (var lease in leases.ListWorldActorLeases())
        {
            var player = resolvePlayer(lease.PeerId);
            string code;
            string message;
            WorldActorRuntimeState? actor = null;
            if (player is null || !sessionStore.IsCurrent(player.Session, DateTime.UtcNow))
            {
                code = "world_interaction_session_invalid";
                message = "The simulation session is no longer active.";
            }
            else if (!actorStore.TryGetByEntityId(lease.TargetEntityId, out actor)
                || actor!.RuntimeActorId != lease.TargetRuntimeId)
            {
                code = "world_actor_not_found";
                message = "The world actor is no longer available.";
            }
            else if (!MatchesAssignment(player.Session, actor))
            {
                code = "world_interaction_session_invalid";
                message = "The worker assignment changed while the interaction was open.";
            }
            else if (!actor.IsActive)
            {
                code = "world_actor_unavailable";
                message = "The world actor is not active.";
            }
            else if (actor.InteractionRevision != lease.TargetRevision)
            {
                code = "world_interaction_target_changed";
                message = "The world actor changed while the interaction was open.";
            }
            else if (!IsWithinRange(
                player,
                actor,
                WorldInteractionRules.AuthoritativeMaintainRange))
            {
                code = "world_interaction_out_of_range";
                message = "Move closer to continue interacting.";
            }
            else if (!HasLineOfSight(player, actor))
            {
                code = "world_interaction_line_of_sight_blocked";
                message = "The world actor is no longer in line of sight.";
            }
            else
            {
                continue;
            }

            leases.ReleaseWorldActor(lease.PeerId, lease.InteractionSessionId);
            closures.Add(new WorldInteractionLeaseClosure(
                lease.PeerId,
                new RealtimeWorldInteractionClosed(
                    lease.InteractionSessionId,
                    lease.TargetKind,
                    lease.TargetEntityId,
                    lease.TargetRuntimeId,
                    code,
                    message)));
        }

        metrics.SetActiveInteractions(leases.ListWorldActorLeases().Count);
        return closures;
    }

    private async Task<WorldInteractionAuthorityResult> ProcessResolvedAsync(
        int peerId,
        PlayerSimulationEntity player,
        RealtimeWorldInteractionIntent intent,
        WorldActorRuntimeState actor,
        CancellationToken cancellationToken)
    {
        if (!MatchesAssignment(player.Session, actor))
        {
            return Reject(
                intent,
                "world_interaction_session_invalid",
                "The world actor does not belong to this worker assignment.",
                actor.InteractionRevision);
        }

        if (intent.ExpectedTargetRevision != actor.InteractionRevision)
        {
            return Reject(
                intent,
                "world_interaction_target_changed",
                "The world actor interaction revision is stale.",
                actor.InteractionRevision);
        }

        if (!actor.IsActive)
        {
            return Reject(
                intent,
                "world_actor_unavailable",
                "The world actor is not active.",
                actor.InteractionRevision);
        }

        if (intent.OperationKind == RealtimeWorldInteractionOperationKind.Open)
        {
            return Open(peerId, player, intent, actor);
        }

        if (!leases.MatchesWorldActor(peerId, intent.InteractionSessionId, actor))
        {
            return Reject(
                intent,
                "world_interaction_session_invalid",
                "Open this world actor before submitting another interaction operation.",
                actor.InteractionRevision);
        }

        if (intent.OperationKind == RealtimeWorldInteractionOperationKind.Close)
        {
            leases.ReleaseWorldActor(peerId, intent.InteractionSessionId);
            return new WorldInteractionAuthorityResult(
                null,
                new RealtimeWorldInteractionResult(
                    intent.OperationId,
                    intent.InteractionSessionId,
                    intent.OperationKind,
                    true,
                    actor.InteractionRevision,
                    null!),
                new RealtimeWorldInteractionClosed(
                    intent.InteractionSessionId,
                    intent.TargetKind,
                    intent.TargetEntityId,
                    intent.TargetRuntimeId,
                    "world_interaction_closed",
                    "The world interaction was closed."));
        }

        if (!IsWithinRange(
                player,
                actor,
                WorldInteractionRules.AuthoritativeMaintainRange))
        {
            return CloseAndReject(
                peerId,
                intent,
                actor,
                "world_interaction_out_of_range",
                "Move closer to continue interacting.");
        }

        if (!HasLineOfSight(player, actor))
        {
            return CloseAndReject(
                peerId,
                intent,
                actor,
                "world_interaction_line_of_sight_blocked",
                "The world actor is no longer in line of sight.");
        }

        var summary = capabilities.BuildSummary(player.Session, actor);
        var capability = summary.Capabilities.SingleOrDefault(value =>
            string.Equals(value.Id, intent.CapabilityId, StringComparison.Ordinal));
        if (capability is null
            || capability.Kind != intent.CapabilityKind
            || capability.Revision != intent.ExpectedCapabilityRevision
            || !capability.IsAvailable)
        {
            return Reject(
                intent,
                "world_interaction_capability_unavailable",
                "The requested capability is not available for this player.",
                actor.InteractionRevision);
        }

        var definition = actor.Definition.Capabilities.Single(value =>
            string.Equals(value.Id, intent.CapabilityId, StringComparison.Ordinal));
        var dispatch = await capabilities.DispatchAsync(
            player.Session,
            actor,
            definition,
            intent,
            cancellationToken);
        return new WorldInteractionAuthorityResult(
            null,
            new RealtimeWorldInteractionResult(
                intent.OperationId,
                intent.InteractionSessionId,
                intent.OperationKind,
                dispatch.Succeeded,
                actor.InteractionRevision,
                dispatch.Succeeded
                    ? null!
                    : new RealtimeError(dispatch.Code, dispatch.Message),
                dispatch.Succeeded ? dispatch.Message : string.Empty,
                dispatch.ItemStateRevision),
            null,
            dispatch.ShouldDisconnect);
    }

    private WorldInteractionAuthorityResult Open(
        int peerId,
        PlayerSimulationEntity player,
        RealtimeWorldInteractionIntent intent,
        WorldActorRuntimeState actor)
    {
        if (!IsWithinRange(
                player,
                actor,
                WorldInteractionRules.AuthoritativeStartRange))
        {
            return Reject(
                intent,
                "world_interaction_out_of_range",
                "Move within interaction range before opening this world actor.",
                actor.InteractionRevision);
        }

        if (!HasLineOfSight(player, actor))
        {
            return Reject(
                intent,
                "world_interaction_line_of_sight_blocked",
                "The world actor is blocked by authoritative world collision.",
                actor.InteractionRevision);
        }

        if (!leases.TryAcquireWorldActor(
                peerId,
                actor,
                out var lease,
                out var code,
                out var message))
        {
            return Reject(intent, code, message, actor.InteractionRevision);
        }

        var summary = capabilities.BuildSummary(player.Session, actor);
        return new WorldInteractionAuthorityResult(
            new RealtimeWorldInteractionOpened(
                intent.OperationId,
                lease!.InteractionSessionId,
                RealtimeWorldInteractionTargetKind.WorldActor,
                actor.NetworkEntityId,
                actor.RuntimeActorId,
                actor.InteractionRevision,
                summary.Revision,
                actor.Definition.DisplayName,
                summary.Capabilities),
            null,
            null);
    }

    private WorldInteractionAuthorityResult CloseAndReject(
        int peerId,
        RealtimeWorldInteractionIntent intent,
        WorldActorRuntimeState actor,
        string code,
        string message)
    {
        leases.ReleaseWorldActor(peerId, intent.InteractionSessionId);
        return new WorldInteractionAuthorityResult(
            null,
            new RealtimeWorldInteractionResult(
                intent.OperationId,
                intent.InteractionSessionId,
                intent.OperationKind,
                false,
                actor.InteractionRevision,
                new RealtimeError(code, message)),
            new RealtimeWorldInteractionClosed(
                intent.InteractionSessionId,
                intent.TargetKind,
                intent.TargetEntityId,
                intent.TargetRuntimeId,
                code,
                message));
    }

    private static WorldInteractionAuthorityResult Reject(
        RealtimeWorldInteractionIntent intent,
        string code,
        string message,
        long targetRevision = 0)
    {
        return new WorldInteractionAuthorityResult(
            null,
            new RealtimeWorldInteractionResult(
                intent.OperationId,
                intent.InteractionSessionId,
                intent.OperationKind,
                false,
                targetRevision,
                new RealtimeError(code, message)),
            null);
    }

    private bool MatchesAssignment(
        ActiveSimulationSession session,
        WorldActorRuntimeState actor)
    {
        return string.Equals(session.WorldId, actor.WorldId, StringComparison.Ordinal)
            && string.Equals(session.ShardId, actor.ShardId, StringComparison.Ordinal)
            && string.Equals(session.WorkerId, config.SimulationWorkerId, StringComparison.Ordinal)
            && string.Equals(session.WorkerRuntimeId, actor.WorkerRuntimeId, StringComparison.Ordinal);
    }

    private static bool IsWithinRange(
        PlayerSimulationEntity player,
        WorldActorRuntimeState actor,
        float range)
    {
        var state = player.Movement.State;
        var distanceSquared = WorldInteractionRules.DistanceSquaredToBounds(
            state.PositionX,
            state.PositionY,
            state.PositionZ,
            actor.BoundsCenterX,
            actor.BoundsCenterY,
            actor.BoundsCenterZ,
            actor.Definition.InteractionBounds.SizeX,
            actor.Definition.InteractionBounds.SizeY,
            actor.Definition.InteractionBounds.SizeZ);
        return distanceSquared <= range * range;
    }

    private bool HasLineOfSight(
        PlayerSimulationEntity player,
        WorldActorRuntimeState actor)
    {
        var state = player.Movement.State;
        return lineOfSight.HasLineOfSight(
            state.PositionX,
            state.PositionY,
            state.PositionZ,
            actor);
    }
}
