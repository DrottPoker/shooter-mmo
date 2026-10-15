namespace Mmorpg.GameService.Items;

internal sealed class ItemCatalogCompatibilityException(string message)
    : InvalidOperationException(message);
