namespace Mmorpg.GameService.Items.Transactions;

// The one definition of the character that owns an item instance: the equipping character,
// the owner of its container, or the character that equips the Bag bound to its container.
// The item row must use the alias "item".
internal static class ItemOwnershipSql
{
    public const string Joins =
        """
        left join item_containers container on container.id = item.container_id
        left join item_instances source_bag
          on source_bag.id = container.bound_bag_item_instance_id
        """;

    public const string OwningCharacterId =
        """
        coalesce(
            item.equipped_character_id,
            container.owner_character_id,
            source_bag.equipped_character_id)
        """;
}
