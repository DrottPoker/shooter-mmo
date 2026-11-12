using Mmorpg.GameService.Platform.Database;

namespace Mmorpg.GameService.Characters.Contracts;

// A character's saved place and vitals: the World it was in, the collision revision of that
// World when the state was saved, its position and facing as the simulation's floats, and its
// health and mana.
public sealed record CharacterSimulationState(
    string WorldId,
    string CollisionRevision,
    float PositionX,
    float PositionY,
    float PositionZ,
    float YawDegrees,
    int Health,
    int Mana);

// One state to save, with the simulation session that wrote it and its sequence within that
// session.
public sealed record CharacterSimulationStateWrite(
    Guid CharacterId,
    Guid SimulationSessionId,
    long Sequence,
    CharacterSimulationState State);

// Every character's last saved place and vitals for other modules, always in the caller's
// transaction. Characters owns the rows; the caller decides which simulation session may write.
public interface ICharacterSimulationStates
{
    // The saved state of a character, or null when it has none.
    Task<CharacterSimulationState?> ReadAsync(
        PostgresTransactionScope scope,
        Guid characterId,
        CancellationToken cancellationToken);

    // Locks the characters FOR KEY SHARE in ascending id order: the lock that saving a first
    // state takes on a character anyway, so a caller takes it before its own row locks.
    Task LockForSaveAsync(
        PostgresTransactionScope scope,
        IReadOnlyCollection<Guid> characterIds,
        CancellationToken cancellationToken);

    // Saves every state in one statement, except where the stored state comes from the same
    // simulation session with an equal or higher sequence, and returns the characters whose
    // state it saved. Each character may appear once.
    Task<IReadOnlySet<Guid>> SaveManyAsync(
        PostgresTransactionScope scope,
        IReadOnlyList<CharacterSimulationStateWrite> writes,
        CancellationToken cancellationToken);
}
