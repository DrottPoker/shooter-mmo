namespace Mmorpg.GameService.Items.Persistence;

internal static class ItemEconomyMigration
{
    public const string Id = "202610181250_item_economy";

    // The catalog mirror gains item values, tool kinds, the currency settings, and vendor
    // profiles, so purchases, sales, and gathering read prices and tool requirements from
    // the same catalog revision as every other item rule. Vendor profiles that leave the
    // catalog become inactive; nothing durable references them except audit records.
    public const string Sql = """
        alter table item_definitions
            add column value bigint not null default 0,
            add constraint ck_item_definitions_value check (value between 0 and 1000000000);

        create table item_tool_kinds (
            id text primary key,
            catalog_id text not null,
            catalog_revision text not null,
            display_name text not null,
            is_active boolean not null default true,
            updated_at timestamptz not null default now(),
            constraint fk_item_tool_kinds_catalog_revision
                foreign key (catalog_id, catalog_revision)
                references item_catalog_revisions(catalog_id, revision)
                on delete restrict,
            constraint ck_item_tool_kinds_id
                check (id ~ '^[a-z][a-z0-9._-]*[a-z0-9]$' or id ~ '^[a-z]$'),
            constraint ck_item_tool_kinds_display_name
                check (length(btrim(display_name)) between 1 and 128)
        );

        create table item_definition_tool_kinds (
            definition_id text not null references item_definitions(id) on delete cascade,
            tool_kind_id text not null references item_tool_kinds(id) on delete restrict,
            constraint pk_item_definition_tool_kinds primary key (definition_id, tool_kind_id)
        );

        alter table item_system_settings
            add column currency_display_name text not null default 'Currency',
            add column starting_currency_balance bigint not null default 0,
            add constraint ck_item_system_settings_currency_display_name
                check (length(btrim(currency_display_name)) between 1 and 32),
            add constraint ck_item_system_settings_starting_currency_balance
                check (starting_currency_balance between 0 and 1000000000000);

        create table vendor_profiles (
            id text primary key,
            catalog_id text not null,
            catalog_revision text not null,
            display_name text not null,
            purchase_price_basis_points integer not null,
            sale_price_basis_points integer not null,
            is_active boolean not null default true,
            updated_at timestamptz not null default now(),
            constraint fk_vendor_profiles_catalog_revision
                foreign key (catalog_id, catalog_revision)
                references item_catalog_revisions(catalog_id, revision)
                on delete restrict,
            constraint ck_vendor_profiles_id
                check (id ~ '^[a-z][a-z0-9._-]*[a-z0-9]$' or id ~ '^[a-z]$'),
            constraint ck_vendor_profiles_display_name
                check (length(btrim(display_name)) between 1 and 128),
            constraint ck_vendor_profiles_purchase_price
                check (purchase_price_basis_points between 1 and 1000000),
            constraint ck_vendor_profiles_sale_price
                check (sale_price_basis_points between 0 and 1000000)
        );

        create table vendor_offers (
            vendor_id text not null references vendor_profiles(id) on delete cascade,
            offer_index integer not null,
            definition_id text not null references item_definitions(id) on delete restrict,
            constraint pk_vendor_offers primary key (vendor_id, offer_index),
            constraint ux_vendor_offers_definition unique (vendor_id, definition_id),
            constraint ck_vendor_offers_index check (offer_index between 0 and 31)
        );

        create table vendor_sale_categories (
            vendor_id text not null references vendor_profiles(id) on delete cascade,
            category_id text not null references item_categories(id) on delete restrict,
            constraint pk_vendor_sale_categories primary key (vendor_id, category_id)
        );
        """;
}
