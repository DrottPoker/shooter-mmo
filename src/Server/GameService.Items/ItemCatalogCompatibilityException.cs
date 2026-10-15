namespace Mmorpg.GameService.Items;

public sealed class ItemCatalogCompatibilityException(string message)
    : InvalidOperationException(message);
