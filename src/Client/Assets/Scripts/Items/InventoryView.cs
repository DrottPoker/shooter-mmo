namespace Mmorpg.Client.Items
{
    // The inventory that target rules judge: the shown state of InventoryClientState, or one
    // step of a predicted operation queue.
    public interface IInventoryView
    {
        ClientItemCatalog Catalog { get; }

        CharacterInventorySnapshot FullSnapshot { get; }

        InventoryContainer Bank { get; }

        // The character's level, which equipment with a level requirement is judged by.
        int CharacterLevel { get; }
    }

    public sealed class InventorySnapshotView : IInventoryView
    {
        public InventorySnapshotView(ClientItemCatalog catalog, CharacterInventorySnapshot snapshot, int characterLevel)
        {
            Catalog = catalog;
            FullSnapshot = snapshot;
            CharacterLevel = characterLevel;
        }

        public ClientItemCatalog Catalog { get; }

        public CharacterInventorySnapshot FullSnapshot { get; }

        public InventoryContainer Bank
        {
            get { return FullSnapshot?.Bank; }
        }

        public int CharacterLevel { get; }
    }
}
