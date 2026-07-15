namespace AuthService.Items;

public sealed class ItemCatalogCompatibilityException(string message)
    : InvalidOperationException(message);
