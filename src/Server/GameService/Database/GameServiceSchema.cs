using Mmorpg.GameService.Platform.Database;
using Mmorpg.GameService.Platform.Modules;

namespace Mmorpg.GameService.Database;

// The complete migration list: the host's migrations and every module's own, merged
// by ordinal id. That reproduces the original application order exactly.
public sealed class GameServiceSchema
{
    // Every migration that has shipped, in application order. A new migration must sort
    // after the last one; append its id here and its SQL hash to MigrationHistoryTests.
    internal static readonly IReadOnlyList<string> FrozenHistory =
    [
        "202607071600_auth_character_world_join",
        "202607101200_character_world_sessions",
        "202607121200_session_ticket_ownership",
        "202607121500_world_session_ownership",
        "202607131000_world_registry_heartbeat",
        "202607131900_single_active_account_session",
        "202607132100_world_runtime_registration",
        "202607141200_simulation_topology",
        "202607141300_single_active_account_simulation_session",
        "202607151200_item_persistence_foundation",
        "202607171200_player_corpse_persistence",
        "202607171210_player_death_carry_overflow",
        "202607181800_item_operations_hardening",
        "202607182100_development_world_identity",
        "202607191200_worker_selected_world_binding",
        "202610141800_release_retryable_operation_claims",
        "202610151000_equipped_item_lookup_index",
        "202610171200_item_catalog_alignment",
        "202610181200_character_level",
        "202610181210_item_level_requirement",
        "202610181220_death_event_killer",
        "202610181230_worker_item_catalog_revision",
        "202610181240_character_currency_ledger",
        "202610181250_item_economy",
        "202610251200_character_appearance"
    ];

    private GameServiceSchema(IReadOnlyList<DatabaseMigration> migrations)
    {
        Migrations = migrations;
    }

    public IReadOnlyList<DatabaseMigration> Migrations { get; }

    public static GameServiceSchema Create(
        IEnumerable<DatabaseMigration> hostMigrations,
        IEnumerable<IGameServiceModule> modules)
    {
        ArgumentNullException.ThrowIfNull(hostMigrations);
        ArgumentNullException.ThrowIfNull(modules);

        var migrations = hostMigrations
            .Concat(modules.SelectMany(module => module.Migrations))
            .OrderBy(migration => migration.Id, StringComparer.Ordinal)
            .ToArray();
        Validate(migrations);
        return new GameServiceSchema(migrations);
    }

    private static void Validate(IReadOnlyList<DatabaseMigration> migrations)
    {
        var errors = migrations
            .GroupBy(migration => migration.Id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => $"- Migration id {group.Key} is defined more than once.")
            .ToList();

        // A sorted list that starts with the frozen history also places every newer id
        // after the last shipped migration.
        for (var index = 0; index < FrozenHistory.Count; index++)
        {
            if (index >= migrations.Count
                || !string.Equals(migrations[index].Id, FrozenHistory[index], StringComparison.Ordinal))
            {
                errors.Add(
                    $"- Migration {index + 1} must be {FrozenHistory[index]}, the shipped history cannot change.");
                break;
            }
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                "GameService migration history is invalid:" + Environment.NewLine
                + string.Join(Environment.NewLine, errors));
        }
    }
}
