using Mmorpg.GameService.Testing;
using Mmorpg.Testing;

namespace Mmorpg.GameService.Tests.Architecture;

// Every foreign key needs an index whose leading columns are the key's columns, or every
// delete or key change of a parent row scans the whole child table. The rule is in
// docs/GAME_SERVICE_MODULES.md under Foreign Keys. It reads the keys and indexes of a fully
// migrated database, so it judges the schema that runs, not the migration text.
public sealed class ForeignKeyIndexTests
{
    private const string CatalogParent =
        "The parent is catalog content, which its seeder upserts by id and retires but never deletes or re-keys.";

    // Keys whose parent rows are never deleted or re-keyed while the game runs, with the reason
    // why the scan never happens.
    private static readonly IReadOnlyDictionary<string, string> AllowedWithoutIndex =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["bag_definition_slot_tags(tag_id)"] = CatalogParent,
            ["character_completed_quests(quest_id)"] = CatalogParent,
            ["corpse_snapshots(definition_id)"] = CatalogParent,
            ["corpse_snapshots(equipment_slot_id)"] = CatalogParent,
            ["equipment_slots(catalog_id, catalog_revision)"] = CatalogParent,
            ["item_categories(catalog_id, catalog_revision)"] = CatalogParent,
            ["item_container_slot_tags(tag_id)"] = CatalogParent,
            ["item_container_slots(equipment_slot_id)"] = CatalogParent,
            ["item_definition_blocked_equipment_slots(equipment_slot_id)"] = CatalogParent,
            ["item_definition_equipment_slots(equipment_slot_id)"] = CatalogParent,
            ["item_definition_tags(tag_id)"] = CatalogParent,
            ["item_definition_tool_kinds(tool_kind_id)"] = CatalogParent,
            ["item_definitions(catalog_id, catalog_revision)"] = CatalogParent,
            ["item_definitions(category_id)"] = CatalogParent,
            ["item_destructions(definition_id)"] = CatalogParent,
            ["item_instances(equipment_slot_id)"] = CatalogParent,
            ["item_tags(catalog_id, catalog_revision)"] = CatalogParent,
            ["item_tool_kinds(catalog_id, catalog_revision)"] = CatalogParent,
            ["vendor_offers(definition_id)"] = CatalogParent,
            ["vendor_profiles(catalog_id, catalog_revision)"] = CatalogParent,
            ["vendor_sale_categories(category_id)"] = CatalogParent,
            ["simulation_workers(node_id)"] =
                "Nodes come from the topology configuration and are never deleted, and a Node has few workers.",
            ["item_operations(actor_account_id)"] =
                "Accounts are never deleted; a flow that deletes them must first index this key, because the table holds every item operation."
        };

    // Columns that name a row of another table without a foreign key on purpose: a log row
    // outlives the row it names, or is trimmed apart from it.
    private static readonly string[] IdsWithoutKey =
    [
        "character_currency_ledger(item_operation_id)",
        "item_destructions(source_operation_id)",
        "item_operation_changes(item_instance_id)"
    ];

    [PostgresIntegrationFact]
    public async Task EveryForeignKeyHasAnIndexThatLeadsWithItsColumns()
    {
        var schema = await LoadSchemaAsync();

        var missing = schema.Keys
            .Where(key => !schema.HasServingIndex(key) && !AllowedWithoutIndex.ContainsKey(key.Name))
            .Select(key =>
                $"{key.Name} references {key.ParentTable} without an index that leads with its columns, "
                + $"so every delete or key change of a {key.ParentTable} row scans {key.Table}. Add an index "
                + $"whose leading columns are the key's columns in a migration of the module that owns {key.Table}, "
                + $"or, if {key.ParentTable} rows are never deleted or re-keyed, add the key with the reason "
                + "to AllowedWithoutIndex in ForeignKeyIndexTests.")
            .ToArray();

        Assert.True(missing.Length == 0, string.Join(Environment.NewLine, missing));
    }

    [PostgresIntegrationFact]
    public async Task EveryAllowedKeyIsAForeignKeyWithoutAnIndex()
    {
        var schema = await LoadSchemaAsync();

        var stale = AllowedWithoutIndex.Keys
            .Where(name => schema.Keys.SingleOrDefault(key => key.Name == name) is not { } key
                || schema.HasServingIndex(key))
            .Select(name =>
                $"{name} is no foreign key without an index any more; remove it from AllowedWithoutIndex "
                + "in ForeignKeyIndexTests.")
            .ToArray();

        Assert.True(stale.Length == 0, string.Join(Environment.NewLine, stale));
    }

    [PostgresIntegrationFact]
    public async Task LogColumnsNameTheirRowsWithoutAForeignKey()
    {
        var schema = await LoadSchemaAsync();

        Assert.All(IdsWithoutKey, name =>
        {
            Assert.True(
                schema.Columns.Contains(name),
                $"{name} is not a column any more; update IdsWithoutKey in ForeignKeyIndexTests.");
            Assert.True(
                schema.Keys.All(key => key.Name != name),
                $"{name} names its row without a foreign key on purpose, as docs/GAME_SERVICE_MODULES.md "
                + "describes under Foreign Keys; a key would erase the id or pin the row it names.");
        });
    }

    private static async Task<DatabaseSchema> LoadSchemaAsync()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync(
            heartbeatSeedShard: false);

        var keys = new List<ForeignKey>();
        await using (var command = context.DataSource.CreateCommand(
            """
            select
                constraint_row.conrelid::regclass::text,
                constraint_row.confrelid::regclass::text,
                array(
                    select attribute.attname::text
                    from unnest(constraint_row.conkey) with ordinality as key_column(attnum, position)
                    join pg_attribute attribute
                      on attribute.attrelid = constraint_row.conrelid
                     and attribute.attnum = key_column.attnum
                    order by key_column.position)
            from pg_constraint constraint_row
            where constraint_row.contype = 'f'
              and constraint_row.connamespace = 'public'::regnamespace;
            """))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                keys.Add(new ForeignKey(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetFieldValue<string[]>(2)));
            }
        }

        // Only valid B-tree indexes serve a key lookup. An expression column has no name.
        var indexes = new List<TableIndex>();
        await using (var command = context.DataSource.CreateCommand(
            """
            select
                index_row.indrelid::regclass::text,
                array(
                    select coalesce(attribute.attname::text, '')
                    from unnest(index_row.indkey::int2[]) with ordinality as index_column(attnum, position)
                    left join pg_attribute attribute
                      on attribute.attrelid = index_row.indrelid
                     and attribute.attnum = index_column.attnum
                    where index_column.position <= index_row.indnkeyatts
                    order by index_column.position),
                pg_get_expr(index_row.indpred, index_row.indrelid)
            from pg_index index_row
            join pg_class table_class on table_class.oid = index_row.indrelid
            join pg_class index_class on index_class.oid = index_row.indexrelid
            join pg_am access_method on access_method.oid = index_class.relam
            where table_class.relnamespace = 'public'::regnamespace
              and index_row.indisvalid
              and access_method.amname = 'btree';
            """))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                indexes.Add(new TableIndex(
                    reader.GetString(0),
                    reader.GetFieldValue<string[]>(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2)));
            }
        }

        var columns = new HashSet<string>(StringComparer.Ordinal);
        await using (var command = context.DataSource.CreateCommand(
            """
            select table_class.relname::text, attribute.attname::text
            from pg_attribute attribute
            join pg_class table_class on table_class.oid = attribute.attrelid
            where table_class.relnamespace = 'public'::regnamespace
              and table_class.relkind = 'r'
              and attribute.attnum > 0
              and not attribute.attisdropped;
            """))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                columns.Add($"{reader.GetString(0)}({reader.GetString(1)})");
            }
        }

        Assert.NotEmpty(keys);
        Assert.NotEmpty(indexes);
        return new DatabaseSchema(keys, indexes, columns);
    }

    private sealed record ForeignKey(string Table, string ParentTable, IReadOnlyList<string> Columns)
    {
        public string Name => $"{Table}({string.Join(", ", Columns)})";
    }

    private sealed record TableIndex(string Table, IReadOnlyList<string> Columns, string? Predicate);

    private sealed record DatabaseSchema(
        IReadOnlyList<ForeignKey> Keys,
        IReadOnlyList<TableIndex> Indexes,
        IReadOnlySet<string> Columns)
    {
        // The index's leading columns are the key's columns in any order. A partial index serves
        // the key only when its one condition is that the key is not null, which every lookup
        // by a value implies.
        public bool HasServingIndex(ForeignKey key)
        {
            return Indexes.Any(index =>
                index.Table == key.Table
                && index.Columns.Count >= key.Columns.Count
                && index.Columns.Take(key.Columns.Count).ToHashSet(StringComparer.Ordinal)
                    .SetEquals(key.Columns)
                && (index.Predicate is null || index.Predicate == NotNullPredicate(key.Columns)));
        }

        private static string NotNullPredicate(IReadOnlyList<string> columns)
        {
            return columns.Count == 1
                ? $"({columns[0]} IS NOT NULL)"
                : $"({string.Join(" AND ", columns.Select(column => $"({column} IS NOT NULL)"))})";
        }
    }
}
