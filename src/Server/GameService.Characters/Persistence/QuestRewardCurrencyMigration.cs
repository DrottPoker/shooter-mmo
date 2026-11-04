namespace Mmorpg.GameService.Characters.Persistence;

internal static class QuestRewardCurrencyMigration
{
    public const string Id = "202611050900_quest_reward_currency";

    // A quest turn-in credits its Gold reward with a quest_reward ledger row.
    public const string Sql = """
        alter table character_currency_ledger
            drop constraint ck_character_currency_ledger_source_kind,
            add constraint ck_character_currency_ledger_source_kind
                check (source_kind in (
                    'opening_balance',
                    'starting_balance',
                    'vendor_purchase',
                    'vendor_sale',
                    'insurance',
                    'development',
                    'quest_reward'));
        """;
}
