using Mmorpg.GameService.Accounts.Contracts;
using Mmorpg.GameService.Platform.Database;

namespace Mmorpg.GameService.Placement.Contracts;

// How the account session of a simulation session ended while the simulation session goes on, so
// the character's body stays in the world: a new login replaced it, or a logout, a revoke, or its
// expiry ended it.
public enum SimulationAccountSessionEnd
{
    Replaced = 1,
    Ended = 2
}

// The codes that refuse what a client asks for on a session whose account session ended. They
// tell the worker to end the connection and keep the body; the heartbeat names the same codes.
public static class SimulationAccountSessionEndCodes
{
    public const string Replaced = AccountSessionErrorCode.SessionReplaced;
    public const string Ended = "account_session_ended";

    public static string Of(SimulationAccountSessionEnd end)
    {
        return end == SimulationAccountSessionEnd.Replaced ? Replaced : Ended;
    }

    public static string MessageOf(SimulationAccountSessionEnd end)
    {
        return end == SimulationAccountSessionEnd.Replaced
            ? "This account logged in from another client."
            : "The account session of this simulation session ended.";
    }
}

// A simulation session lease as stored. IsLive is true while the lease is unreleased and
// unexpired, AccountSessionEnd is set once its account session ended, and IsActive is true while
// it is live and its account session is still valid. The body's own work may run on a live
// session; everything a client asks for needs an active one.
public sealed record LiveSimulationSession(
    Guid AccountId,
    Guid CharacterId,
    string ShardId,
    string WorkerId,
    string WorkerRuntimeId,
    string SessionTokenHash,
    bool IsLive,
    SimulationAccountSessionEnd? AccountSessionEnd)
{
    public bool IsActive => IsLive && AccountSessionEnd is null;
}

// A simulation session lease with its worker's registry lease and Shard assignment, with the
// same facts about the session as LiveSimulationSession.
public sealed record SimulationSessionReadAuthority(
    Guid AccountId,
    Guid CharacterId,
    string ShardId,
    string WorkerId,
    string WorkerRuntimeId,
    string SessionTokenHash,
    bool SessionIsLive,
    SimulationAccountSessionEnd? AccountSessionEnd,
    bool WorkerHasFreshLease,
    bool WorkerHasAssignment)
{
    public bool SessionIsActive => SessionIsLive && AccountSessionEnd is null;
}

// Raw simulation session authority for other modules. It returns stored facts, and the
// caller decides which check fails first and which error it reports.
public interface ISimulationSessionAuthority
{
    // Locks the session lease FOR UPDATE in the caller's transaction, or returns null
    // when the session does not exist.
    Task<LiveSimulationSession?> LockForItemMutationAsync(
        PostgresTransactionScope scope,
        Guid simulationSessionId,
        CancellationToken cancellationToken);

    // Reads the session with its worker lease and assignment, or returns null when the
    // session or its worker does not exist.
    Task<SimulationSessionReadAuthority?> GetReadAuthorityAsync(
        PostgresTransactionScope scope,
        Guid simulationSessionId,
        CancellationToken cancellationToken);
}
