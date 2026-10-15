using System.Text.RegularExpressions;

namespace Mmorpg.Backend.Tests.Architecture;

// Enforces the data access rules in docs/GAME_SERVICE_MODULES.md. A module reads,
// writes, or locks another module's table only through the owner's contract or a
// published SQL fragment. The compiler cannot check SQL strings, so this scans them.
public sealed partial class TableOwnershipTests
{
    private static readonly IReadOnlyDictionary<string, string> TableOwners =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["schema_migrations"] = GameServiceSourceMap.Platform,
            ["accounts"] = GameServiceSourceMap.Accounts,
            ["account_sessions"] = GameServiceSourceMap.Accounts,
            ["characters"] = GameServiceSourceMap.Characters,
            ["world_definitions"] = GameServiceSourceMap.Placement,
            ["fleets"] = GameServiceSourceMap.Placement,
            ["simulation_nodes"] = GameServiceSourceMap.Placement,
            ["shards"] = GameServiceSourceMap.Placement,
            ["simulation_workers"] = GameServiceSourceMap.Placement,
            ["simulation_assignments"] = GameServiceSourceMap.Placement,
            ["simulation_join_tickets"] = GameServiceSourceMap.Placement,
            ["character_simulation_sessions"] = GameServiceSourceMap.Placement,
            ["item_catalog_revisions"] = GameServiceSourceMap.Items,
            ["item_categories"] = GameServiceSourceMap.Items,
            ["item_tags"] = GameServiceSourceMap.Items,
            ["equipment_slots"] = GameServiceSourceMap.Items,
            ["item_definitions"] = GameServiceSourceMap.Items,
            ["item_definition_tags"] = GameServiceSourceMap.Items,
            ["item_definition_equipment_slots"] = GameServiceSourceMap.Items,
            ["item_definition_location_rules"] = GameServiceSourceMap.Items,
            ["item_definition_default_policies"] = GameServiceSourceMap.Items,
            ["bag_definitions"] = GameServiceSourceMap.Items,
            ["bag_definition_slots"] = GameServiceSourceMap.Items,
            ["bag_definition_slot_tags"] = GameServiceSourceMap.Items,
            ["secure_container_tiers"] = GameServiceSourceMap.Items,
            ["item_system_settings"] = GameServiceSourceMap.Items,
            ["account_secure_container_entitlements"] = GameServiceSourceMap.Items,
            ["item_containers"] = GameServiceSourceMap.Items,
            ["item_container_slots"] = GameServiceSourceMap.Items,
            ["item_container_slot_tags"] = GameServiceSourceMap.Items,
            ["character_item_states"] = GameServiceSourceMap.Items,
            ["item_instances"] = GameServiceSourceMap.Items,
            ["item_instance_policies"] = GameServiceSourceMap.Items,
            ["recovery_deliveries"] = GameServiceSourceMap.Items,
            ["recovery_delivery_items"] = GameServiceSourceMap.Items,
            ["item_operations"] = GameServiceSourceMap.Items,
            ["item_operation_changes"] = GameServiceSourceMap.Items,
            ["item_destructions"] = GameServiceSourceMap.Items,
            ["corpses"] = GameServiceSourceMap.Items,
            ["corpse_sections"] = GameServiceSourceMap.Items,
            ["corpse_snapshots"] = GameServiceSourceMap.Items,
            ["death_events"] = GameServiceSourceMap.Items
        };

    // R4: frozen migration history. Its SQL may touch any table and is pinned by
    // MigrationHistoryTests instead.
    private static readonly IReadOnlySet<string> FrozenFiles = new HashSet<string>(StringComparer.Ordinal)
    {
        "GameService/Database/Migrations/LegacyBaselineMigrations.cs",
        "GameService/Items/Persistence/ItemPersistenceFoundationMigration.cs",
        "GameService/Items/Persistence/PlayerCorpsePersistenceMigration.cs",
        "GameService/Items/Persistence/PlayerDeathCarryOverflowMigration.cs",
        "GameService/Items/Persistence/ItemOperationsHardeningMigration.cs",
        "GameService.Placement/Persistence/DevelopmentWorldIdentityMigration.cs",
        "GameService.Placement/Persistence/WorkerSelectedWorldBindingMigration.cs",
        "GameService/Items/Persistence/ReleaseRetryableOperationClaimsMigration.cs",
        "GameService/Items/Persistence/EquippedItemLookupIndexMigration.cs"
    };

    // Foreign table access that still has to move behind an owner contract. The ids
    // refer to the cross-module statement list in docs/GAME_SERVICE_MODULES.md. Every
    // entry must still exist, so the list can only shrink.
    private static readonly IReadOnlySet<string> PendingForeignAccess = new HashSet<string>(StringComparer.Ordinal)
    {
    };

    [Fact]
    public void ModulesOnlyTouchForeignTablesThroughOwnerContracts()
    {
        var found = FindForeignAccess();
        var pending = PendingForeignAccess.Select(StripStatementIds).ToHashSet(StringComparer.Ordinal);

        var unexpected = found.Where(access => !pending.Contains(access)).ToArray();
        var resolved = pending.Where(access => !found.Contains(access)).ToArray();

        Assert.True(
            unexpected.Length == 0,
            "Foreign table access must go through the owner module's contract:"
            + Environment.NewLine + string.Join(Environment.NewLine, unexpected));
        Assert.True(
            resolved.Length == 0,
            "Remove resolved entries from the pending foreign access list:"
            + Environment.NewLine + string.Join(Environment.NewLine, resolved));
    }

    [Fact]
    public void EveryScannedTableHasAnOwner()
    {
        var unknown = GameServiceSourceMap.LoadSourceFiles()
            .Where(file => !FrozenFiles.Contains(file.RelativePath))
            .SelectMany(file => TableReferencePattern().Matches(file.Text)
                .Select(match => match.Groups["table"].Value))
            .Where(table => table.Contains('_', StringComparison.Ordinal)
                && !TableOwners.ContainsKey(table)
                && !NonTableIdentifiers.Contains(table))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            unknown.Length == 0,
            "Add these tables to the ownership map: " + string.Join(", ", unknown));
    }

    [Fact]
    public void FrozenFilesExist()
    {
        var files = GameServiceSourceMap.LoadSourceFiles()
            .Select(file => file.RelativePath)
            .ToHashSet(StringComparer.Ordinal);

        Assert.All(FrozenFiles, file => Assert.Contains(file, files));
    }

    private static HashSet<string> FindForeignAccess()
    {
        return GameServiceSourceMap.LoadSourceFiles()
            .Where(file => !FrozenFiles.Contains(file.RelativePath))
            .SelectMany(file => TableReferencePattern().Matches(file.Text)
                .Select(match => match.Groups["table"].Value)
                .Where(table => TableOwners.TryGetValue(table, out var owner)
                    && !string.Equals(owner, file.Module, StringComparison.Ordinal))
                .Select(table => $"{file.RelativePath} -> {table}"))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string StripStatementIds(string entry)
    {
        var index = entry.IndexOf(" (", StringComparison.Ordinal);
        return index < 0 ? entry : entry[..index];
    }

    // Common table expressions and set-returning functions that follow from, join,
    // into, or update but are not tables.
    private static readonly IReadOnlySet<string> NonTableIdentifiers = new HashSet<string>(StringComparer.Ordinal)
    {
        "carried_containers",
        "generate_series",
        "recalculated",
        "refreshed_session",
        "released_session"
    };

    [GeneratedRegex(@"\b(?:from|join|into|update)\s+(?<table>[a-z][a-z0-9_]*)\b")]
    private static partial Regex TableReferencePattern();
}
