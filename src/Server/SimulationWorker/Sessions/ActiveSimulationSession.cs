using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.GameServiceApi;

namespace Mmorpg.SimulationWorker.Sessions;

// How long a session's lease lasts and when it started, on the monotonic clock of the store that
// holds the session. The worker ends a lease by the time that passed since the request was sent,
// so a step of the host's wall clock neither drops every session at once nor extends a lease
// beyond the one GameService keeps.
public readonly record struct SessionLease(long StartedTimestamp, TimeSpan Duration)
{
    // A lease without a length was never granted.
    public bool IsSet => Duration > TimeSpan.Zero;
}

public sealed record ActiveSimulationSession(
    Guid SimulationSessionId,
    string SimulationSessionToken,
    Guid AccountId,
    Guid CharacterId,
    string CharacterName,
    string ShardId,
    string WorldId,
    string WorkerId,
    string WorkerRuntimeId,
    DateTime JoinedAt,
    DateTime SessionExpiresAt,
    PlayerCarryState CarryState,
    bool IsReconnect)
{
    public bool IsSyntheticBot { get; init; }

    // The character's saved place and vitals when this session started, or null when it has none.
    public SimulationSavedCharacterState? SavedState { get; init; }

    // The order in which GameService issued the session's tokens, which grows with every rotation:
    // of two generations of one session the higher is the newer, whichever registered here first.
    // 0 when GameService gives none, as for a development bot, and then nothing is ordered.
    public long Generation { get; init; }

    // The lease that decides whether the session is still current. SessionExpiresAt is only what
    // the client is told. A session that arrives without a lease gets one from SessionExpiresAt
    // when the store registers it, the only place where a wall clock time becomes a lease.
    public SessionLease Lease { get; init; }

    // requestedAt and requestedTimestamp are when the consume request was sent, on the worker's wall
    // clock and on the clock of the store that will hold the session, so the local deadline counts
    // from the send time and never outlives the database lease.
    public static ActiveSimulationSession FromJoinTicket(
        ConsumedSimulationJoinTicketResponse ticket,
        DateTime requestedAt,
        long? requestedTimestamp = null)
    {
        var duration = TimeSpan.FromMilliseconds(ticket.SessionLeaseDurationMilliseconds);
        return new ActiveSimulationSession(
            ticket.SimulationSessionId,
            ticket.SimulationSessionToken,
            ticket.AccountId,
            ticket.CharacterId,
            ticket.CharacterName,
            ticket.ShardId,
            ticket.WorldId,
            ticket.WorkerId,
            ticket.WorkerRuntimeId,
            DateTime.UtcNow,
            requestedAt + duration,
            new PlayerCarryState(
                ticket.ItemStateRevision,
                ticket.CarriedWeight,
                ticket.CarryCapacity),
            ticket.IsReconnect)
        {
            IsSyntheticBot = ticket.IsSyntheticBot,
            SavedState = ticket.SavedState,
            Generation = ticket.SessionGeneration,
            Lease = new SessionLease(requestedTimestamp ?? TimeProvider.System.GetTimestamp(), duration)
        };
    }
}
