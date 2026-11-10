namespace Mmorpg.GameService.Items.Persistence;

internal static class ItemCatalogNamesRemovalMigration
{
    public const string Id = "202611101300_item_catalog_names_removal";

    // GameService keeps the rules of the item catalog, never its text: the names of items,
    // categories, tags, tool kinds, equipment slots, vendors, and the currency are in the item
    // text table, which only the client reads. Dropping a column drops its check with it.
    public const string Sql = """
        alter table item_categories drop column display_name;
        alter table item_tags drop column display_name;
        alter table item_tool_kinds drop column display_name;
        alter table equipment_slots drop column display_name;
        alter table item_definitions drop column display_name;
        alter table vendor_profiles drop column display_name;
        alter table item_system_settings drop column currency_display_name;
        """;
}
