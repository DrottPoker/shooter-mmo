using Mmorpg.GameService.Platform.Database;

namespace Mmorpg.GameService.Characters.Contracts;

// A character's progression as clients show it: its level, the experience it gathered inside
// that level, what the level needs, and the highest Mob level that gives it no experience.
// Experience is 0 at the highest level, and ExperienceToNextLevel is 0 there too.
// HighestMobLevelWithoutExperience is int.MaxValue at the highest level, where no Mob gives any.
public sealed record CharacterProgressionSnapshot(
    int Level,
    long Experience,
    long ExperienceToNextLevel,
    int HighestMobLevelWithoutExperience);

// What a grant changed. Gained is the experience that counted: 0 at the highest level, and up to
// what reaching the highest level took when a grant reaches it.
public sealed record CharacterExperienceChange(
    CharacterProgressionSnapshot Before,
    CharacterProgressionSnapshot After,
    long Gained);

// Where granted experience came from, as the granted experience metric labels it. A kill grants
// through GrantKillExperienceAsync, every other source through GrantExperienceAsync.
public static class CharacterExperienceSources
{
    public const string Kill = "kill";
    public const string Quest = "quest";
    public const string Admin = "admin";
}

// Character level and experience for other modules, always in the caller's transaction. The
// highest level, the level curve, and the experience of a kill come from the progression rules;
// every sum goes through them, so the stored values never leave their range.
public interface ICharacterProgression
{
    // The highest character level, maximumCharacterLevel in the progression rules. Content
    // that requires a higher level can never be used, so the modules refuse to start with it.
    int MaximumLevel { get; }

    // Reads without a lock. Null when the character does not exist or is deleted.
    Task<CharacterProgressionSnapshot?> ReadAsync(
        PostgresTransactionScope scope,
        Guid characterId,
        CancellationToken cancellationToken);

    // Locks the active character FOR NO KEY UPDATE, adds the experience, and levels up as far as
    // it reaches. 0 only reads under the lock. source is CharacterExperienceSources.Quest or
    // Admin. Null when the character does not exist or is deleted.
    Task<CharacterExperienceChange?> GrantExperienceAsync(
        PostgresTransactionScope scope,
        Guid characterId,
        long experience,
        string source,
        CancellationToken cancellationToken);

    // The same for the kill of a Mob: the rules decide the experience from the character's level
    // under the lock, the Mob's level, and the Mob's experience percent.
    Task<CharacterExperienceChange?> GrantKillExperienceAsync(
        PostgresTransactionScope scope,
        Guid characterId,
        int mobLevel,
        int mobExperiencePercent,
        CancellationToken cancellationToken);

    // Locks the active character FOR NO KEY UPDATE, sets its level, and sets its experience to 0.
    // Returns false when the character does not exist or is deleted.
    Task<bool> SetLevelAsync(
        PostgresTransactionScope scope,
        Guid characterId,
        int level,
        CancellationToken cancellationToken);
}
