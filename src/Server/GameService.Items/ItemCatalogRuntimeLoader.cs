using Mmorpg.Server.Common.Items;
using Mmorpg.WorldData.Items;

namespace Mmorpg.GameService.Items;

internal static class ItemCatalogRuntimeLoader
{
    public static Task<ItemCatalogRuntimeDocument> LoadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        return ItemCatalogFileLoader.LoadAsync(path, cancellationToken);
    }

    public static ItemCatalogRuntimeDocument ValidateAndCanonicalize(
        ItemCatalogRuntimeDocument runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        return ItemCatalogRuntimeVerifier.VerifyAndCanonicalize(runtime);
    }
}
