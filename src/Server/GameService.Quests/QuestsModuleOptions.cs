namespace Mmorpg.GameService.Quests;

public sealed record QuestsModuleOptions(QuestCatalogSource Catalog)
{
    // Whether the worker quest route runs the Admin menu's quest tools, which only the
    // Development environment allows.
    public bool DevelopmentQuestTools { get; init; }
}
