using Microsoft.Extensions.DependencyInjection;
using Mmorpg.GameService.Characters.Contracts;
using Mmorpg.GameService.Platform.Database;
using Mmorpg.GameService.Testing;
using Mmorpg.Testing;
using Npgsql;

namespace Mmorpg.GameService.Characters.Tests;

// The saved place and vitals of characters: the floats come back bit for bit, a batch saves in
// one statement, and a stored state of a session yields only to a newer state of that session
// or to any state of another session.
public sealed class CharacterSimulationStateIntegrationTests
{
    [PostgresIntegrationFact]
    public async Task AStateComesBackBitForBit()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        var state = new CharacterSimulationState(
            "development-world-2",
            "collision-abc123",
            0.1f,
            BitConverter.Int32BitsToSingle(0x3F800001),
            -987654.3f,
            -0f,
            37,
            0);

        Assert.Null(await ReadAsync(context, player.Character.Id));
        var saved = await SaveAsync(context, [new(player.Character.Id, Guid.NewGuid(), 1, state)]);
        var read = await ReadAsync(context, player.Character.Id);

        Assert.Equal([player.Character.Id], saved);
        Assert.NotNull(read);
        Assert.Equal((state.WorldId, state.CollisionRevision, state.Health, state.Mana), (read.WorldId, read.CollisionRevision, read.Health, read.Mana));
        Assert.Equal(Bits(state), Bits(read));
    }

    [PostgresIntegrationFact]
    public async Task ABatchSavesEveryCharacterInOneSet()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var first = await context.RegisterPlayerAsync("first@example.com", "first_player", "First Saver");
        var second = await context.RegisterPlayerAsync("second@example.com", "second_player", "Second Saver");
        var third = await context.RegisterPlayerAsync("third@example.com", "third_player", "Third Saver");

        var saved = await SaveAsync(
            context,
            [
                new(first.Character.Id, Guid.NewGuid(), 1, State(1f, health: 10)),
                new(second.Character.Id, Guid.NewGuid(), 1, State(2f, health: 20)),
                new(third.Character.Id, Guid.NewGuid(), 1, State(3f, health: 30))
            ]);

        Assert.Equal(
            new HashSet<Guid> { first.Character.Id, second.Character.Id, third.Character.Id },
            saved.ToHashSet());
        Assert.Equal(10, (await ReadAsync(context, first.Character.Id))!.Health);
        Assert.Equal(2f, (await ReadAsync(context, second.Character.Id))!.PositionX);
        Assert.Equal(30, (await ReadAsync(context, third.Character.Id))!.Health);
    }

    [PostgresIntegrationFact]
    public async Task OnlyANewerStateOfTheSameSessionOrAnotherSessionReplacesTheStoredOne()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        var characterId = player.Character.Id;
        var session = Guid.NewGuid();
        await SaveAsync(context, [new(characterId, session, 5, State(5f))]);

        var equal = await SaveAsync(context, [new(characterId, session, 5, State(6f))]);
        var lower = await SaveAsync(context, [new(characterId, session, 4, State(7f))]);
        Assert.Empty(equal);
        Assert.Empty(lower);
        Assert.Equal(5f, (await ReadAsync(context, characterId))!.PositionX);

        var higher = await SaveAsync(context, [new(characterId, session, 6, State(8f))]);
        Assert.Equal([characterId], higher);
        Assert.Equal(8f, (await ReadAsync(context, characterId))!.PositionX);

        // A later session numbers its states from its own start.
        var anotherSession = await SaveAsync(context, [new(characterId, Guid.NewGuid(), 1, State(9f))]);
        Assert.Equal([characterId], anotherSession);
        Assert.Equal(9f, (await ReadAsync(context, characterId))!.PositionX);
    }

    [PostgresIntegrationFact]
    public async Task ASaveNamesEachCharacterOnce()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        var session = Guid.NewGuid();

        await Assert.ThrowsAsync<ArgumentException>(() => SaveAsync(
            context,
            [
                new(player.Character.Id, session, 1, State(1f)),
                new(player.Character.Id, session, 2, State(2f))
            ]));
    }

    [PostgresIntegrationFact]
    public async Task TheTableRefusesAStateOutsideItsBounds()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();

        await Assert.ThrowsAsync<PostgresException>(() => SaveAsync(
            context,
            [new(player.Character.Id, Guid.NewGuid(), 1, State(float.NaN))]));
        await Assert.ThrowsAsync<PostgresException>(() => SaveAsync(
            context,
            [new(player.Character.Id, Guid.NewGuid(), 1, State(1f, health: 0))]));
        Assert.Null(await ReadAsync(context, player.Character.Id));
    }

    [PostgresIntegrationFact]
    public async Task DeletingACharacterDeletesItsState()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        await SaveAsync(context, [new(player.Character.Id, Guid.NewGuid(), 1, State(1f))]);

        await context.ExecuteAsync($"delete from characters where id = '{player.Character.Id}';");

        Assert.Equal(
            0,
            await context.ExecuteScalarIntAsync(
                $"select count(*) from character_simulation_states where character_id = '{player.Character.Id}';"));
    }

    private static CharacterSimulationState State(float x, int health = 50)
    {
        return new CharacterSimulationState("development-world-2", "collision-abc123", x, 0f, -2f, 90f, health, 40);
    }

    private static (int, int, int, int) Bits(CharacterSimulationState state)
    {
        return (
            BitConverter.SingleToInt32Bits(state.PositionX),
            BitConverter.SingleToInt32Bits(state.PositionY),
            BitConverter.SingleToInt32Bits(state.PositionZ),
            BitConverter.SingleToInt32Bits(state.YawDegrees));
    }

    private static async Task<CharacterSimulationState?> ReadAsync(
        PostgresIntegrationTestContext context,
        Guid characterId)
    {
        var states = context.Services.GetRequiredService<ICharacterSimulationStates>();
        await using var connection = await context.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var state = await states.ReadAsync(
            new PostgresTransactionScope(connection, transaction),
            characterId,
            CancellationToken.None);
        await transaction.CommitAsync();
        return state;
    }

    // Locks and saves in one transaction, as Placement does.
    private static async Task<IReadOnlyList<Guid>> SaveAsync(
        PostgresIntegrationTestContext context,
        IReadOnlyList<CharacterSimulationStateWrite> writes)
    {
        var states = context.Services.GetRequiredService<ICharacterSimulationStates>();
        await using var connection = await context.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var scope = new PostgresTransactionScope(connection, transaction);
        await states.LockForSaveAsync(scope, writes.Select(write => write.CharacterId).ToArray(), CancellationToken.None);
        var saved = await states.SaveManyAsync(scope, writes, CancellationToken.None);
        await transaction.CommitAsync();
        return saved.ToArray();
    }
}
