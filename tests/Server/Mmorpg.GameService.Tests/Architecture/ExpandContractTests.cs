using System.Text.RegularExpressions;
using Mmorpg.GameService.Config;
using Mmorpg.GameService.Database.Migrations;
using Mmorpg.GameService.Testing;
using Mmorpg.Testing;

namespace Mmorpg.GameService.Tests.Architecture;

// The expand and contract rule of docs/GAME_SERVICE_MODULES.md, as far as a test can read it: the
// SQL of every migration after the legacy baseline is scanned for the statements that the version
// before cannot run against, and a migration that has one must be listed as a contract. The scan
// reads the SQL only. A release that changes a join or the protocol cannot mix versions for
// reasons that are not a schema matter, which the exact match of the protocol enforces.
public sealed partial class ExpandContractTests
{
    // The first migration that shipped together with the rule. A migration before it may have
    // dropped or renamed something in the release that stopped using it, as one step.
    private const string RuleBegins = "202611141400_character_data_steps";

    // Those migrations, with what each did. The history is frozen, so they stay as they are, and
    // no migration from RuleBegins on joins the list.
    private static readonly IReadOnlyDictionary<string, string> ShippedBeforeTheRule =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["202610171200_item_catalog_alignment"] =
                "Renamed equipment_slots.sort_order to display_order in the release that read the new name.",
            ["202611011200_remove_secure_container"] =
                "Dropped the Secure Container tables and columns in the release that removed the feature.",
            ["202611101200_corpse_source_character_name"] =
                "Renamed corpses.source_display_name to source_character_name in the release that read the new name.",
            ["202611101300_item_catalog_names_removal"] =
                "Dropped the display_name columns of the catalog tables in the release that stopped reading them."
        };

    // A contract migration from RuleBegins on, with the id of the expand migration that it
    // completes. Add the contract here in the pull request that writes it, and remove the expand
    // from OpenContracts in the same one.
    private static readonly IReadOnlyDictionary<string, string> Contracts =
        new Dictionary<string, string>(StringComparer.Ordinal);

    // Expands whose contract has not been written. Each names what the contract drops, so the
    // list says what is owed. The contract waits until no running version reads the old column.
    private static readonly IReadOnlyList<OpenContract> OpenContracts =
    [
        new(
            "202611171400_join_ticket_transport_key",
            "simulation_join_tickets",
            "ticket_hash",
            "The column, its unique constraint simulation_join_tickets_ticket_hash_key, and its index ix_simulation_join_tickets_ticket_hash.")
    ];

    [Fact]
    public void EveryDestructiveStatementBelongsToAListedMigration()
    {
        var unlisted = ScannedMigrations()
            .Select(migration => (migration.Id, Kinds: DestructiveKinds(migration.Sql)))
            .Where(found => found.Kinds.Count > 0
                && !ShippedBeforeTheRule.ContainsKey(found.Id)
                && !Contracts.ContainsKey(found.Id))
            .Select(found => $"{found.Id}: {string.Join(", ", found.Kinds)}")
            .ToArray();

        Assert.True(
            unlisted.Length == 0,
            "These migrations drop, rename, or tighten something, so they are contract migrations: add each "
            + "to Contracts with the id of the expand that it completes, or split the change into an expand "
            + "and a later contract:" + Environment.NewLine + string.Join(Environment.NewLine, unlisted));
    }

    [Fact]
    public void EveryListedMigrationStillHasADestructiveStatementAndSitsOnItsSideOfTheRule()
    {
        var scanned = ScannedMigrations().ToDictionary(migration => migration.Id, migration => migration.Sql, StringComparer.Ordinal);

        foreach (var id in ShippedBeforeTheRule.Keys)
        {
            Assert.True(scanned.ContainsKey(id), $"{id} is listed as shipped before the rule but is not a migration.");
            Assert.True(string.CompareOrdinal(id, RuleBegins) < 0, $"{id} is from the rule on, so it cannot be listed as shipped before it.");
            Assert.NotEmpty(DestructiveKinds(scanned[id]));
        }

        foreach (var (contract, expand) in Contracts)
        {
            Assert.True(scanned.ContainsKey(contract), $"{contract} is listed as a contract but is not a migration.");
            Assert.True(string.CompareOrdinal(contract, RuleBegins) >= 0, $"{contract} is before the rule, so it belongs in ShippedBeforeTheRule.");
            Assert.NotEmpty(DestructiveKinds(scanned[contract]));
            Assert.True(
                scanned.ContainsKey(expand) && string.CompareOrdinal(expand, contract) < 0,
                $"{contract} completes {expand}, which must be an earlier migration.");
        }
    }

    [Fact]
    public void TheOpenContractsAreWellFormedAndNoContractCompletesOne()
    {
        var registered = ScannedMigrations().Select(migration => migration.Id).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(OpenContracts.Count, OpenContracts.Select(open => open.Expand).Distinct(StringComparer.Ordinal).Count());
        foreach (var open in OpenContracts)
        {
            Assert.Contains(open.Expand, registered);
            Assert.True(string.CompareOrdinal(open.Expand, RuleBegins) >= 0, $"{open.Expand} is before the rule.");
            Assert.False(string.IsNullOrWhiteSpace(open.Table), $"{open.Expand} names no table.");
            Assert.False(string.IsNullOrWhiteSpace(open.Column), $"{open.Expand} names no column.");
            Assert.False(string.IsNullOrWhiteSpace(open.Owed), $"{open.Expand} says nothing about what the contract drops.");
            Assert.DoesNotContain(open.Expand, Contracts.Values);
        }
    }

    // An entry that stays after its contract dropped the column would claim a debt that is paid.
    [PostgresIntegrationFact]
    public async Task EveryOpenContractStillNamesAColumnThatExists()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync(heartbeatSeedShard: false);

        foreach (var open in OpenContracts)
        {
            var exists = await context.ExecuteScalarIntAsync(
                "select count(*) from information_schema.columns "
                + $"where table_schema = 'public' and table_name = '{open.Table}' and column_name = '{open.Column}';");
            Assert.True(exists == 1, $"{open.Expand} is still open, but {open.Table}.{open.Column} no longer exists.");
        }
    }

    [Fact]
    public void AMigrationThatDropsOrRenamesIsFoundWhateverTheCaseOrLayout()
    {
        Assert.Equal(["drop table"], DestructiveKinds("DROP   TABLE\n  old_things;"));
        Assert.Equal(["drop column"], DestructiveKinds("alter table items\n    drop column  if exists note;"));
        Assert.Equal(["drop column"], DestructiveKinds("alter table items drop note;"));
        Assert.Equal(["rename"], DestructiveKinds("alter table items rename to things;"));
        Assert.Equal(["rename"], DestructiveKinds("alter table items rename column note to remark;"));
        Assert.Equal(["set not null"], DestructiveKinds("alter table items alter column note set not null;"));
        Assert.Equal(["column type"], DestructiveKinds("alter table items alter column note type bigint;"));
        Assert.Equal(["column type"], DestructiveKinds("alter table items alter column note set data type bigint;"));
        Assert.Equal(
            ["drop column", "rename"],
            DestructiveKinds("do $$ begin alter table a drop column b; alter table a rename column c to d; end $$;"));
    }

    // What the expand phase may do: add, relax, and index. Comments say what they like.
    [Fact]
    public void AnExpandMigrationIsNotFlagged()
    {
        const string expand = """
            -- drop table old_things; rename it later
            /* alter table items drop column note; */
            create table new_things (id integer primary key);
            alter table items add column remark text;
            alter table items add column level integer not null default 1;
            alter table items alter column note drop not null;
            alter table items alter column level set default 2;
            alter table items drop constraint ck_items_note;
            alter table items rename constraint ck_a to ck_b;
            alter index ix_a rename to ix_b;
            create index concurrently ix_items_level on items (level);
            drop index ix_items_old;
            drop trigger trg_items on items;
            """;

        Assert.Empty(DestructiveKinds(expand));
    }

    private sealed record OpenContract(string Expand, string Table, string Column, string Owed);

    private static IReadOnlyList<(string Id, string Sql)> ScannedMigrations()
    {
        var baseline = LegacyBaselineMigrations.All.Select(migration => migration.Id).ToHashSet(StringComparer.Ordinal);
        return GameServiceComposition.CreateSchema(
                GameServiceTestComposition.CreateConfiguration(
                    "Host=localhost;Database=expand_contract_test;Username=test;Password=test"))
            .Migrations
            .Where(migration => !baseline.Contains(migration.Id))
            .Select(migration => (migration.Id, migration.Sql))
            .ToArray();
    }

    // The kinds of statement in the SQL that the version before cannot run against: a dropped table
    // or column, a rename of a table or column, a column that becomes not null, and a changed
    // column type. Dropping a constraint, an index, or a trigger, renaming one, and relaxing a
    // column are the expand side. The scan reads text, so a statement inside a DO block or a
    // string that is run as dynamic SQL counts too.
    internal static IReadOnlyList<string> DestructiveKinds(string sql)
    {
        var text = Whitespace().Replace(Comments().Replace(sql, " "), " ").ToLowerInvariant();
        var kinds = new List<string>();
        if (DropTable().IsMatch(text))
        {
            kinds.Add("drop table");
        }

        if (DropColumn().IsMatch(text))
        {
            kinds.Add("drop column");
        }

        if (Rename().IsMatch(text))
        {
            kinds.Add("rename");
        }

        if (SetNotNull().IsMatch(text))
        {
            kinds.Add("set not null");
        }

        if (ColumnType().IsMatch(text))
        {
            kinds.Add("column type");
        }

        return kinds;
    }

    [GeneratedRegex(@"--[^\n]*|/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex Comments();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\bdrop table\b")]
    private static partial Regex DropTable();

    // drop [column] [if exists] name inside an alter table, which is not a drop of a constraint,
    // a default, an identity, a generated expression, or a not null.
    [GeneratedRegex(@"\balter table\b[^;]*?\bdrop (?!constraint\b|not null\b|default\b|identity\b|expression\b)")]
    private static partial Regex DropColumn();

    // rename to, rename column, or rename a column without the keyword; not rename constraint.
    [GeneratedRegex(@"\balter table\b[^;]*?\brename (?!constraint\b)")]
    private static partial Regex Rename();

    [GeneratedRegex(@"\bset not null\b")]
    private static partial Regex SetNotNull();

    [GeneratedRegex(@"\balter column\b[^;]*?\b(?:set data )?type\b")]
    private static partial Regex ColumnType();
}
