namespace Mmorpg.GameService.Quests.Contracts;

// A worker counts quest progress by the quest catalog it runs, so every quest request names that
// catalog's revision, and GameService refuses one from a worker that runs another catalog before
// anything happens. Placement already keeps characters off such a worker; this keeps the ones it
// hosts from building on other rules after GameService started with another catalog.
public static class QuestCatalogFence
{
    public const string MismatchCode = "simulation_worker_quest_catalog_mismatch";

    public const string MismatchMessage =
        "The simulation worker runs another quest catalog than GameService. Restart it with the same content.";

    public static bool Admits(IQuestDefinitions definitions, string? workerCatalogRevision)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        return string.Equals(workerCatalogRevision?.Trim(), definitions.Revision, StringComparison.Ordinal);
    }
}
