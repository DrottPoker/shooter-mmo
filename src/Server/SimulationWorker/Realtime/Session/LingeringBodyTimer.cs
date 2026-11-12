using Microsoft.Extensions.Logging;
using Mmorpg.SimulationWorker.Combat;
using Mmorpg.SimulationWorker.Sessions;

namespace Mmorpg.SimulationWorker.Realtime;

// Ends lingering bodies once per tick. A body whose session ended, because GameService revoked or
// replaced it or its lease expired, goes at once and its session is not released. A body whose
// death committed, or whose time is up, goes as a disconnected player went before: its final
// state is captured, and its session is released once the durable combat work it still holds
// committed. A body whose session a reconnect took over waits for that join. Nothing is done and
// nothing is allocated while no body is in the world. Loop-confined.
internal sealed class LingeringBodyTimer(
    LingeringBodies bodies,
    ActiveSimulationSessionStore sessionStore,
    RealtimeSessionTeardown teardown,
    SessionAuthorityOperations sessionOperations,
    PlayerCombatAuthority? combatAuthority,
    ILogger logger)
{
    // The despawn reasons of a body that went.
    public const string TimeUpReason = "left_shard";
    public const string DiedReason = "died";
    public const string SessionEndedReason = "session_revoked";

    private readonly List<LingeringBody> buffer = [];

    public void AddTickPhase(SimulationTickPhases phases)
    {
        phases.Add(SimulationTickOrder.LingeringBodies, "lingering bodies", (in SimulationTick tick) => Run(tick.ServerTick));
    }

    // Takes every body out of the world at shutdown, after the flushes saved them.
    public void Clear()
    {
        bodies.Clear();
        buffer.Clear();
    }

    private void Run(uint serverTick)
    {
        if (bodies.Count == 0)
        {
            return;
        }

        bodies.CopyTo(buffer);
        var now = DateTime.UtcNow;
        foreach (var body in buffer)
        {
            if (!sessionStore.IsCurrent(body.Session, now))
            {
                if (sessionStore.IsSupersededByReconnect(body.Session))
                {
                    continue;
                }

                EndWithSession(body);
                continue;
            }

            if (body.EndRequested)
            {
                End(body, DiedReason);
            }
            else if (CombatTicks.IsReached(serverTick, body.EndTick))
            {
                End(body, TimeUpReason);
            }
        }

        buffer.Clear();
    }

    // The session goes on until the body goes, and is then released with the body's final
    // state, after the combat work that still has to commit.
    private void End(LingeringBody body, string reason)
    {
        var session = body.Session;
        var deferred = combatAuthority?.TryDeferRelease(session) == true;
        teardown.RemoveBody(body, reason);
        if (!deferred)
        {
            sessionOperations.ReleaseDetachedInBackground(session);
        }

        Log(body, reason);
    }

    // The session ended, so nothing is released and nothing more is saved.
    private void EndWithSession(LingeringBody body)
    {
        var reason = sessionStore.TryTakeInvalidation(body.Session.SimulationSessionId, out var invalidation)
            ? invalidation!.Code
            : SessionEndedReason;
        teardown.RemoveBody(body, reason);
        Log(body, reason);
    }

    private void Log(LingeringBody body, string reason)
    {
        var session = body.Session;
        if (session.IsSyntheticBot)
        {
            logger.LogDebug(
                "[SIMULATION] The body of synthetic bot {CharacterName} left shard {ShardId}: {Reason}.",
                session.CharacterName,
                session.ShardId,
                reason);
            return;
        }

        logger.LogInformation(
            "[SIMULATION] The body of character {CharacterName} ({CharacterId}) left shard {ShardId}: {Reason}. Simulation session {SimulationSessionId}.",
            session.CharacterName,
            session.CharacterId,
            session.ShardId,
            reason,
            session.SimulationSessionId);
    }
}
