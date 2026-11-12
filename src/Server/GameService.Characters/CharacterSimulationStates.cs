using Dapper;
using Mmorpg.GameService.Characters.Contracts;
using Mmorpg.GameService.Platform.Database;

namespace Mmorpg.GameService.Characters;

internal sealed class CharacterSimulationStates : ICharacterSimulationStates
{
    public async Task<CharacterSimulationState?> ReadAsync(
        PostgresTransactionScope scope,
        Guid characterId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var row = await scope.Connection.QuerySingleOrDefaultAsync<StateRow>(new CommandDefinition(
            """
            select world_id as "WorldId",
                   collision_revision as "CollisionRevision",
                   position_x as "PositionX",
                   position_y as "PositionY",
                   position_z as "PositionZ",
                   yaw_degrees as "YawDegrees",
                   health as "Health",
                   mana as "Mana"
            from character_simulation_states
            where character_id = @CharacterId;
            """,
            new { CharacterId = characterId },
            scope.Transaction,
            cancellationToken: cancellationToken));
        return row?.ToState();
    }

    public async Task LockForSaveAsync(
        PostgresTransactionScope scope,
        IReadOnlyCollection<Guid> characterIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(characterIds);
        if (characterIds.Count == 0)
        {
            return;
        }

        // Ascending id order, so two saves of overlapping characters never deadlock.
        await scope.Connection.ExecuteAsync(new CommandDefinition(
            """
            select id
            from characters
            where id = any(@CharacterIds)
            order by id
            for key share;
            """,
            new { CharacterIds = characterIds.Distinct().ToArray() },
            scope.Transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlySet<Guid>> SaveManyAsync(
        PostgresTransactionScope scope,
        IReadOnlyList<CharacterSimulationStateWrite> writes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(writes);
        if (writes.Count == 0)
        {
            return new HashSet<Guid>();
        }

        // One upsert may change a row only once.
        if (writes.Select(write => write.CharacterId).Distinct().Count() != writes.Count)
        {
            throw new ArgumentException("Each character may appear once in a save.", nameof(writes));
        }

        // A state replaces the stored one unless that one comes from the same session with an
        // equal or higher sequence, so a late or repeated state never overwrites a newer one.
        var saved = await scope.Connection.QueryAsync<Guid>(new CommandDefinition(
            """
            insert into character_simulation_states as stored (
                character_id,
                world_id,
                collision_revision,
                position_x,
                position_y,
                position_z,
                yaw_degrees,
                health,
                mana,
                simulation_session_id,
                session_sequence,
                saved_at)
            select incoming.character_id,
                   incoming.world_id,
                   incoming.collision_revision,
                   incoming.position_x,
                   incoming.position_y,
                   incoming.position_z,
                   incoming.yaw_degrees,
                   incoming.health,
                   incoming.mana,
                   incoming.simulation_session_id,
                   incoming.session_sequence,
                   now()
            from unnest(
                @CharacterIds::uuid[],
                @WorldIds::text[],
                @CollisionRevisions::text[],
                @PositionsX::real[],
                @PositionsY::real[],
                @PositionsZ::real[],
                @Yaws::real[],
                @Healths::integer[],
                @Manas::integer[],
                @SimulationSessionIds::uuid[],
                @Sequences::bigint[])
                as incoming(
                    character_id,
                    world_id,
                    collision_revision,
                    position_x,
                    position_y,
                    position_z,
                    yaw_degrees,
                    health,
                    mana,
                    simulation_session_id,
                    session_sequence)
            on conflict (character_id) do update
            set world_id = excluded.world_id,
                collision_revision = excluded.collision_revision,
                position_x = excluded.position_x,
                position_y = excluded.position_y,
                position_z = excluded.position_z,
                yaw_degrees = excluded.yaw_degrees,
                health = excluded.health,
                mana = excluded.mana,
                simulation_session_id = excluded.simulation_session_id,
                session_sequence = excluded.session_sequence,
                saved_at = excluded.saved_at
            where stored.simulation_session_id <> excluded.simulation_session_id
               or stored.session_sequence < excluded.session_sequence
            returning stored.character_id;
            """,
            new
            {
                CharacterIds = writes.Select(write => write.CharacterId).ToArray(),
                WorldIds = writes.Select(write => write.State.WorldId).ToArray(),
                CollisionRevisions = writes.Select(write => write.State.CollisionRevision).ToArray(),
                PositionsX = writes.Select(write => write.State.PositionX).ToArray(),
                PositionsY = writes.Select(write => write.State.PositionY).ToArray(),
                PositionsZ = writes.Select(write => write.State.PositionZ).ToArray(),
                Yaws = writes.Select(write => write.State.YawDegrees).ToArray(),
                Healths = writes.Select(write => write.State.Health).ToArray(),
                Manas = writes.Select(write => write.State.Mana).ToArray(),
                SimulationSessionIds = writes.Select(write => write.SimulationSessionId).ToArray(),
                Sequences = writes.Select(write => write.Sequence).ToArray()
            },
            scope.Transaction,
            cancellationToken: cancellationToken));
        return saved.ToHashSet();
    }

    private sealed record StateRow(
        string WorldId,
        string CollisionRevision,
        float PositionX,
        float PositionY,
        float PositionZ,
        float YawDegrees,
        int Health,
        int Mana)
    {
        public CharacterSimulationState ToState()
        {
            return new CharacterSimulationState(
                WorldId,
                CollisionRevision,
                PositionX,
                PositionY,
                PositionZ,
                YawDegrees,
                Health,
                Mana);
        }
    }
}
