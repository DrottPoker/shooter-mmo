namespace Mmorpg.Client.Items
{
    // The inventory that target rules judge: the shown state of InventoryClientState, or one
    // step of a predicted operation queue.
    public interface IInventoryView
    {
        ClientItemCatalog Catalog { get; }

        CharacterInventorySnapshot FullSnapshot { get; }

        InventoryContainer Bank { get; }
    }

    public sealed class InventorySnapshotView : IInventoryView
    {
        public InventorySnapshotView(ClientItemCatalog catalog, CharacterInventorySnapshot snapshot)
        {
            Catalog = catalog;
            FullSnapshot = snapshot;
        }

        public ClientItemCatalog Catalog { get; }

        public CharacterInventorySnapshot FullSnapshot { get; }

        public InventoryContainer Bank
        {
            get { return FullSnapshot?.Bank; }
        }
    }
}
