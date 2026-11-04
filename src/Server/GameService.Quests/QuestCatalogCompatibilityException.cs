namespace Mmorpg.GameService.Quests;

internal sealed class QuestCatalogCompatibilityException(string message)
    : InvalidOperationException(message);
