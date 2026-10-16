namespace Mmorpg.Client.Core
{
    // Addressables addresses of client content. Each content folder is one Addressables
    // folder entry whose address is its path below the content root, so an asset address is
    // its path below that root including the file extension. Item icons use
    // ItemPresentationCatalogFormat.IconAddressPrefix. A World scene's address is its scene name.
    public static class ClientAddresses
    {
        public const string ContentRoot = "Assets/Content/";
        public const string ClientConfig = "Config/ClientConfig.asset";
        public const string WorldSceneCatalog = "Worlds/world-scene-catalog.json";
        public const string ItemPresentationCatalog = "Items/Presentation/item-presentation-catalog.json";
        public const string WorldActorPresentationRegistry =
            "WorldActors/WorldActorPresentationRegistry.asset";

        // Generated collision lives in the com.mmorpg.world-data package, below
        // Generated/WorldCollision, with the same relative layout.
        public const string WorldCollisionPrefix = "WorldCollision/";
    }
}
