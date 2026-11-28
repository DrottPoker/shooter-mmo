using Dapper;
using Mmorpg.GameService.Characters.Contracts;
using Mmorpg.GameService.Platform.Database;
using Mmorpg.WorldData.Progression;

namespace Mmorpg.GameService.Characters;

// Character level and experience. characters.experience is what the character gathered inside
// its level. Every sum goes through the progression rules, and a grant writes the level and the
// experience in one update, which it skips when nothing changed.
internal sealed class CharacterProgression(ProgressionRules rules, CharacterProgressionMetrics metrics)
    : ICharacterProgression
{
    public int MaximumLevel => rules.MaximumCharacterLevel;

    public async Task<CharacterProgressionSnapshot?> ReadAsync(
        PostgresTransactionScope scope,
        Guid characterId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var row = await scope.Connection.QuerySingleOrDefaultAsync<ProgressionRow>(new CommandDefinition(
            """
            select level as "Level", experience as "Experience"
            from characters
            where id = @CharacterId
              and deleted_at is null;
            """,
            new { CharacterId = characterId },
            scope.Transaction,
            cancellationToken: cancellationToken));
        return row is null ? null : Snapshot(row.Level, row.Experience);
    }

    public Task<CharacterExperienceChange?> GrantExperienceAsync(
        PostgresTransactionScope scope,
        Guid characterId,
        long experience,
        string source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentOutOfRangeException.ThrowIfNegative(experience);
        if (source is not (CharacterExperienceSources.Quest or CharacterExperienceSources.Admin))
        {
            throw new ArgumentException(
                "Experience comes from a quest or an admin tool; a kill grants through GrantKillExperienceAsync.",
                nameof(source));
        }

        return GrantAsync(scope, characterId, _ => experience, source, cancellationToken);
    }

    public Task<CharacterExperienceChange?> GrantKillExperienceAsync(
        PostgresTransactionScope scope,
        Guid characterId,
        int mobLevel,
        int mobExperiencePercent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentOutOfRangeException.ThrowIfLessThan(mobLevel, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(mobExperiencePercent);
        return GrantAsync(
            scope,
            characterId,
            level => rules.KillExperience(level, mobLevel, mobExperiencePercent),
            CharacterExperienceSources.Kill,
            cancellationToken);
    }

    public async Task<bool> SetLevelAsync(
        PostgresTransactionScope scope,
        Guid characterId,
        int level,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentOutOfRangeException.ThrowIfLessThan(level, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(level, MaximumLevel);

        if (await LockAsync(scope, characterId, cancellationToken) is null)
        {
            return false;
        }

        await scope.Connection.ExecuteAsync(new CommandDefinition(
            "update characters set level = @Level, experience = 0 where id = @CharacterId;",
            new { CharacterId = characterId, Level = level },
            scope.Transaction,
            cancellationToken: cancellationToken));
        return true;
    }

    // The experience is decided under the lock, from the level the character has then.
    private async Task<CharacterExperienceChange?> GrantAsync(
        PostgresTransactionScope scope,
        Guid characterId,
        Func<int, long> experienceFor,
        string source,
        CancellationToken cancellationToken)
    {
        var row = await LockAsync(scope, characterId, cancellationToken);
        if (row is null)
        {
            return null;
        }

        var before = Snapshot(row.Level, row.Experience);
        var experience = experienceFor(row.Level);
        if (experience <= 0 || row.Level >= MaximumLevel)
        {
            return new CharacterExperienceChange(before, before, 0);
        }

        // A surplus that a lowered curve left levels up with any grant, also one that no longer
        // counts because the surplus already reaches the highest level.
        var gained = Counted(row.Level, row.Experience, experience);
        var state = rules.Add(new ProgressionState(row.Level, row.Experience), gained);
        if (state.Level == row.Level && state.Experience == row.Experience)
        {
            return new CharacterExperienceChange(before, before, 0);
        }

        await scope.Connection.ExecuteAsync(new CommandDefinition(
            "update characters set level = @Level, experience = @Experience where id = @CharacterId;",
            new { CharacterId = characterId, state.Level, state.Experience },
            scope.Transaction,
            cancellationToken: cancellationToken));
        var after = Snapshot(state.Level, state.Experience);
        metrics.RecordGrant(source, gained, after.Level - before.Level);
        return new CharacterExperienceChange(before, after, gained);
    }

    private static async Task<ProgressionRow?> LockAsync(
        PostgresTransactionScope scope,
        Guid characterId,
        CancellationToken cancellationToken)
    {
        return await scope.Connection.QuerySingleOrDefaultAsync<ProgressionRow>(new CommandDefinition(
            """
            select level as "Level", experience as "Experience"
            from characters
            where id = @CharacterId
              and deleted_at is null
            for no key update;
            """,
            new { CharacterId = characterId },
            scope.Transaction,
            cancellationToken: cancellationToken));
    }

    // Below the highest level the whole amount counts, up to what reaching the highest level still
    // takes. A character at or above it gains nothing, so a level that a lowered highest level left
    // above it stays as stored.
    private long Counted(int level, long stored, long experience)
    {
        var remaining = -stored;
        for (var current = Math.Max(1, level); current < MaximumLevel; current++)
        {
            remaining += rules.ExperienceToNextLevel(current);
            if (remaining >= experience)
            {
                return experience;
            }
        }

        return Math.Max(0, remaining);
    }

    // A lowered curve can leave more stored than the level needs: the snapshot shows the level
    // one short of full, and the next grant levels up through the rules, which keep the surplus.
    // At or above the highest level no Mob gives any experience, so every Mob level is without.
    private CharacterProgressionSnapshot Snapshot(int level, long stored)
    {
        var toNextLevel = rules.ExperienceToNextLevel(level);
        return toNextLevel == 0
            ? new CharacterProgressionSnapshot(level, 0, 0, int.MaxValue)
            : new CharacterProgressionSnapshot(
                level,
                Math.Clamp(stored, 0, toNextLevel - 1),
                toNextLevel,
                rules.HighestMobLevelWithoutExperience(level));
    }

    private sealed record ProgressionRow(int Level, long Experience);
}
