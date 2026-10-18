namespace Mmorpg.GameService.Characters.Persistence;

internal static class CharacterCurrencyLedgerMigration
{
    public const string Id = "202610181240_character_currency_ledger";

    // The balance stays within the wallet's range, and every change appends one ledger row,
    // so a character's ledger deltas always sum to its balance. Balances that existed
    // before the ledger open it with one opening_balance row. The item operation id is not
    // a foreign key, because Items is above Characters in the module order.
    public const string Sql = """
        alter table characters
            add constraint ck_characters_currency_range
                check (currency between 0 and 1000000000000);

        create table character_currency_ledger (
            id bigint generated always as identity primary key,
            character_id uuid not null references characters(id) on delete cascade,
            source_kind text not null,
            source_id text not null,
            item_operation_id uuid null,
            delta bigint not null,
            balance_after bigint not null,
            created_at timestamptz not null default now(),
            constraint ck_character_currency_ledger_source_kind
                check (source_kind in (
                    'opening_balance',
                    'starting_balance',
                    'vendor_purchase',
                    'vendor_sale',
                    'insurance',
                    'development')),
            constraint ck_character_currency_ledger_source_id
                check (length(source_id) between 1 and 256),
            constraint ck_character_currency_ledger_delta check (delta <> 0),
            constraint ck_character_currency_ledger_balance_after
                check (balance_after between 0 and 1000000000000)
        );

        create index ix_character_currency_ledger_character
            on character_currency_ledger(character_id, id);

        insert into character_currency_ledger (
            character_id,
            source_kind,
            source_id,
            delta,
            balance_after)
        select id, 'opening_balance', id::text, currency, currency
        from characters
        where currency > 0;
        """;
}
