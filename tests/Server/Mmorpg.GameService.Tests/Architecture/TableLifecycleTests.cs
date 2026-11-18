using Microsoft.Extensions.DependencyInjection;
using Mmorpg.GameService.Platform.Retention;
using Mmorpg.GameService.Testing;
using Mmorpg.Testing;
using Npgsql;

namespace Mmorpg.GameService.Tests.Architecture;

// The database holds a bounded history at any population, which docs/GAME_SERVICE_MODULES.md
// states under Retention. This is where that is checked: every table is classified by how its
// rows leave, so a new table cannot grow without limit unnoticed. The list of tables is the one
// of TableOwnershipTests.
public sealed class TableLifecycleTests
{
    private enum TableClass
    {
        // Rows that the content, the configuration, or the code sets, bounded by them and never by
        // players.
        Reference,

        // One row per account, or per thing that an account has and that is bounded by its limits.
        AccountState,

        // Rows per character, or per thing that a character owns and that is bounded by game
        // rules such as slots, quests, and carried items.
        CharacterState,

        // History that a retention task trims after its window.
        Trimmed,

        // History that grows without a limit on purpose, with the reason.
        KeptOnPurpose
    }

    // How a table's rows leave. Task is the retention task that deletes its rows, which a trimmed
    // table needs and a table of state may name for the part of its rows that history makes. Reason
    // says why a table is kept on purpose.
    private sealed record Lifecycle(TableClass Class, string? Task = null, string? Reason = null);

    private static readonly Lifecycle ReferenceTable = new(TableClass.Reference);
    private static readonly Lifecycle AccountTable = new(TableClass.AccountState);
    private static readonly Lifecycle CharacterTable = new(TableClass.CharacterState);

    private static Lifecycle TrimmedBy(string task) => new(TableClass.Trimmed, task);

    // A table of character state whose rows for a closed corpse go with the corpse.
    private static readonly Lifecycle CharacterTableWithClosedCorpses = new(
        TableClass.CharacterState,
        "closed_corpses",
        "Containers of closed corpses are deleted with their corpse, empty.");

    private static readonly IReadOnlyDictionary<string, Lifecycle> Lifecycles =
        new Dictionary<string, Lifecycle>(StringComparer.Ordinal)
        {
            ["schema_migrations"] = new(TableClass.Reference, Reason: "One row per migration of the code."),
            ["accounts"] = AccountTable,
            ["account_sessions"] = TrimmedBy("account_sessions"),
            ["characters"] = CharacterTable,
            ["character_currency_ledger"] = new(
                TableClass.KeptOnPurpose,
                Reason: "The ledger proves every character's balance over all time, so no row is trimmed."),
            ["character_appearances"] = CharacterTable,
            ["character_simulation_states"] = CharacterTable,
            ["character_data_steps"] = ReferenceTable,
            ["world_definitions"] = ReferenceTable,
            ["fleets"] = ReferenceTable,
            ["simulation_nodes"] = ReferenceTable,
            ["shards"] = ReferenceTable,
            ["simulation_workers"] = ReferenceTable,
            ["simulation_assignments"] = TrimmedBy("simulation_assignments"),
            ["simulation_join_tickets"] = TrimmedBy("join_tickets"),
            ["character_simulation_sessions"] = TrimmedBy("simulation_sessions"),
            ["item_catalog_revisions"] = ReferenceTable,
            ["item_categories"] = ReferenceTable,
            ["item_tags"] = ReferenceTable,
            ["equipment_slots"] = ReferenceTable,
            ["item_definitions"] = ReferenceTable,
            ["item_definition_tags"] = ReferenceTable,
            ["item_definition_equipment_slots"] = ReferenceTable,
            ["item_definition_blocked_equipment_slots"] = ReferenceTable,
            ["item_definition_default_policies"] = ReferenceTable,
            ["bag_definitions"] = ReferenceTable,
            ["bag_definition_slots"] = ReferenceTable,
            ["bag_definition_slot_tags"] = ReferenceTable,
            ["item_system_settings"] = ReferenceTable,
            ["item_tool_kinds"] = ReferenceTable,
            ["item_definition_tool_kinds"] = ReferenceTable,
            ["vendor_profiles"] = ReferenceTable,
            ["vendor_offers"] = ReferenceTable,
            ["vendor_sale_categories"] = ReferenceTable,
            ["item_data_steps"] = ReferenceTable,
            ["item_containers"] = CharacterTableWithClosedCorpses,
            ["item_container_slots"] = CharacterTableWithClosedCorpses,
            ["item_container_slot_tags"] = CharacterTableWithClosedCorpses,
            ["character_item_states"] = CharacterTable,
            ["item_instances"] = CharacterTable,
            ["item_instance_policies"] = CharacterTable,
            ["recovery_deliveries"] = TrimmedBy("claimed_recovery_deliveries"),
            ["recovery_delivery_items"] = TrimmedBy("claimed_recovery_deliveries"),
            ["item_operations"] = TrimmedBy("item_operations"),
            ["item_operation_changes"] = TrimmedBy("item_operations"),
            ["item_destructions"] = TrimmedBy("item_destructions"),
            ["corpses"] = TrimmedBy("closed_corpses"),
            ["corpse_sections"] = TrimmedBy("closed_corpses"),
            ["corpse_snapshots"] = TrimmedBy("closed_corpses"),
            ["death_events"] = TrimmedBy("closed_corpses"),
            ["live_mob_loot_grants"] = TrimmedBy("live_mob_loot_grants"),
            ["quest_catalogs"] = ReferenceTable,
            ["quest_definitions"] = ReferenceTable,
            ["character_quest_logs"] = CharacterTable,
            ["character_active_quests"] = CharacterTable,
            ["character_quest_progress"] = CharacterTable,
            ["character_completed_quests"] = CharacterTable
        };

    // The tables of the ownership map and the tables here are one list.
    [Fact]
    public void EveryTableOfTheOwnershipMapHasAClassAndEveryClassIsATable()
    {
        var owned = TableOwnershipTests.TableOwners.Keys.ToHashSet(StringComparer.Ordinal);

        var unclassified = owned.Except(Lifecycles.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var unknown = Lifecycles.Keys.Except(owned, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

        Assert.True(
            unclassified.Length == 0,
            "Classify these tables in TableLifecycleTests as reference data, state of an account or a "
            + "character, trimmed by a retention task, or kept on purpose with the reason: "
            + string.Join(", ", unclassified));
        Assert.True(
            unknown.Length == 0,
            "These tables are classified but are not in the ownership map: " + string.Join(", ", unknown));
    }

    // The tables that a migrated database holds, so a table that no test of the source sees, such
    // as one that a migration creates through dynamic SQL, is classified too.
    [PostgresIntegrationFact]
    public async Task EveryMigratedTableHasAClass()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync(heartbeatSeedShard: false);
        await using var command = context.DataSource.CreateCommand(
            "select table_name from information_schema.tables "
            + "where table_schema = 'public' and table_type = 'BASE TABLE' order by table_name;");
        var migrated = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                migrated.Add(reader.GetString(0));
            }
        }

        Assert.Equal(migrated, Lifecycles.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ATrimmedTableNamesItsTaskAndAKeptTableItsReason()
    {
        foreach (var (table, lifecycle) in Lifecycles)
        {
            if (lifecycle.Class == TableClass.Trimmed)
            {
                Assert.False(string.IsNullOrWhiteSpace(lifecycle.Task), $"{table} is trimmed but names no retention task.");
            }

            if (lifecycle.Class == TableClass.KeptOnPurpose)
            {
                Assert.False(string.IsNullOrWhiteSpace(lifecycle.Reason), $"{table} is kept on purpose without a reason.");
            }

            Assert.True(
                lifecycle.Task is null
                    || lifecycle.Class is TableClass.Trimmed or TableClass.CharacterState or TableClass.AccountState,
                $"{table} names a retention task but is neither history nor state that history is part of.");
        }
    }

    // A class that a retention task trims needs the task to exist, and a task that trims nothing
    // here is a table that nobody classified.
    [Fact]
    public void EveryTaskThatATableNamesIsRegisteredAndEveryRegisteredTaskTrimsATable()
    {
        var registered = RegisteredTasks();

        var named = Lifecycles.Values
            .Select(lifecycle => lifecycle.Task)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var missing = named.Except(registered, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var untied = registered.Except(named, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

        Assert.True(
            missing.Length == 0,
            "These retention tasks are named by a table but no module registers them: " + string.Join(", ", missing));
        Assert.True(
            untied.Length == 0,
            "These registered retention tasks trim no classified table: " + string.Join(", ", untied));
    }

    private static string[] RegisteredTasks()
    {
        using var dataSource = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused");
        using var services = GameServiceTestComposition.BuildServiceProvider(dataSource);
        return services.GetServices<IRetentionTask>().Select(task => task.Name).Order(StringComparer.Ordinal).ToArray();
    }
}
