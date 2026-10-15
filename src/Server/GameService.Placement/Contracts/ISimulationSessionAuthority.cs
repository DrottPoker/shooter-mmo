using Mmorpg.GameService.Platform.Database;

namespace Mmorpg.GameService.Placement.Contracts;

// A simulation session lease as stored. IsActive is true while the lease is unreleased,
// unexpired, and its account session is still valid.
public sealed record LiveSimulationSession(
    Guid AccountId,
    Guid CharacterId,
    string ShardId,
    string WorkerId,
    string WorkerRuntimeId,
    string SessionTokenHash,
    bool IsActive);

// A simulation session lease with its worker's registry lease and Shard assignment.
public sealed record SimulationSessionReadAuthority(
    Guid AccountId,
    Guid CharacterId,
    string ShardId,
    string WorkerId,
    string WorkerRuntimeId,
    string SessionTokenHash,
    bool SessionIsActive,
    bool WorkerHasFreshLease,
    bool WorkerHasAssignment);

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
