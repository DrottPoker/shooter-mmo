using Dapper;
using Mmorpg.GameService.Items.Persistence;
using Mmorpg.GameService.Testing;
using Mmorpg.Testing;
using Npgsql;

namespace Mmorpg.GameService.Items.Tests;

// A corpse stores what it came from, never a display text: a player's corpse keeps the name
// its character had, and a Mob's corpse only its actor definition.
public sealed class CorpseSourceCharacterNameMigrationTests
{
    private static readonly Guid PlayerCorpseId = Guid.Parse("10000000-0000-0000-0000-0000000000a1");
    private static readonly Guid MobCorpseId = Guid.Parse("10000000-0000-0000-0000-0000000000a2");

    [PostgresIntegrationFact]
    public async Task AMobCorpseLosesItsNameAndAPlayerCorpseKeepsIt()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);

        // The table as it was before the migration, with one corpse of each kind.
        await world.ExecuteSqlAsync(
            """
            alter table corpses drop constraint ck_corpses_source_character_name;
            alter table corpses rename column source_character_name to source_display_name;
            alter table corpses add constraint ck_corpses_source_display_name
                check (length(btrim(source_display_name)) between 1 and 128);
            """);
        await InsertAsync(world, PlayerCorpseId, "player", null, "source_display_name", "Fallen Hero");
        await InsertAsync(world, MobCorpseId, "persistent_npc", "mob.feral_alpha", "source_display_name", "Feral Alpha");
        await world.ExecuteSqlAsync("alter table corpses alter column source_display_name set not null;");

        await world.ExecuteSqlAsync(CorpseSourceCharacterNameMigration.Sql);

        Assert.Equal("Fallen Hero", await NameAsync(world, PlayerCorpseId));
        Assert.Null(await NameAsync(world, MobCorpseId));
        Assert.Equal("mob.feral_alpha", await DefinitionAsync(world, MobCorpseId));

        // The new rule holds for every later corpse.
        await Assert.ThrowsAsync<PostgresException>(() => InsertAsync(
            world,
            Guid.NewGuid(),
            "persistent_npc",
            "mob.feral_alpha",
            "source_character_name",
            "Feral Alpha"));
        await Assert.ThrowsAsync<PostgresException>(() => InsertAsync(
            world,
            Guid.NewGuid(),
            "player",
            null,
            "source_character_name",
            null));
        await Assert.ThrowsAsync<PostgresException>(() => InsertAsync(
            world,
            Guid.NewGuid(),
            "persistent_npc",
            null,
            "source_character_name",
            null));
    }

    private static Task InsertAsync(
        ItemScenarioWorld world,
        Guid corpseId,
        string sourceType,
        string? actorDefinitionId,
        string nameColumn,
        string? name)
    {
        return world.ExecuteSqlAsync(
            $"""
            insert into corpses (
                id,
                source_type,
                source_npc_definition_id,
                {nameColumn},
                shard_id,
                position_x,
                position_y,
                position_z,
                rotation_x,
                rotation_y,
                rotation_z,
                rotation_w,
                persistence_mode,
                presentation_key,
                expires_at,
                expiry_operation_id)
            values (
                @CorpseId,
                @SourceType,
                @ActorDefinitionId,
                @Name,
                @ShardId,
                0,
                0,
                0,
                0,
                0,
                0,
                1,
                'durable',
                'corpse.remains',
                now() + interval '5 minutes',
                @ExpiryOperationId);
            """,
            new
            {
                CorpseId = corpseId,
                SourceType = sourceType,
                ActorDefinitionId = actorDefinitionId,
                Name = name,
                ItemScenarioWorld.ShardId,
                ExpiryOperationId = Guid.NewGuid()
            });
    }

    private static Task<string?> NameAsync(ItemScenarioWorld world, Guid corpseId)
    {
        return ColumnAsync(world, "source_character_name", corpseId);
    }

    private static Task<string?> DefinitionAsync(ItemScenarioWorld world, Guid corpseId)
    {
        return ColumnAsync(world, "source_npc_definition_id", corpseId);
    }

    private static async Task<string?> ColumnAsync(ItemScenarioWorld world, string column, Guid corpseId)
    {
        await using var connection = await world.Context.DataSource.OpenConnectionAsync();
        return await connection.QuerySingleAsync<string?>(
            $"select {column} from corpses where id = @CorpseId;",
            new { CorpseId = corpseId });
    }
}
