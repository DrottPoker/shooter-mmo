using Mmorpg.GameService.Characters.Contracts;
using Mmorpg.GameService.Platform.Audit;

namespace Mmorpg.GameService.Characters.Audit;

// The economy audit's checks of gold: the balance on characters and the ledger beside it. The
// ledger is never trimmed, so these checks see a character's whole history.
internal static class CurrencyLedgerAuditChecks
{
    // The kind of the rows that opened the ledger for balances that existed before it; only the
    // ledger migration wrote it.
    public const string OpeningBalanceKind = "opening_balance";

    // Every source kind that a ledger row may have.
    public static IReadOnlyList<string> KnownKinds { get; } =
    [
        OpeningBalanceKind,
        CurrencySourceKinds.StartingBalance,
        CurrencySourceKinds.VendorPurchase,
        CurrencySourceKinds.VendorSale,
        CurrencySourceKinds.Insurance,
        CurrencySourceKinds.Development,
        CurrencySourceKinds.QuestReward
    ];

    // The kinds that change gold inside an item transaction, whose rows name the item operation
    // that committed them. The starting balance, Development grants, and the opening rows have
    // no operation.
    public static IReadOnlyList<string> ItemOperationKinds { get; } =
    [
        CurrencySourceKinds.VendorPurchase,
        CurrencySourceKinds.VendorSale,
        CurrencySourceKinds.Insurance,
        CurrencySourceKinds.QuestReward
    ];

    public static SqlAuditCheck BalanceMatchesLedger { get; } = new(
        "currency_balance_matches_ledger",
        "Every character's gold balance equals the sum of the deltas in its ledger.",
        AuditSql.Counted(
            "select count(*) from characters",
            """
            select
                character.id as character_id,
                character.currency as balance,
                coalesce(ledger.total, 0) as ledger_sum
            from characters character
            left join (
                select character_id, sum(delta) as total
                from character_currency_ledger
                group by character_id
            ) ledger on ledger.character_id = character.id
            where character.currency <> coalesce(ledger.total, 0)
            """,
            "character_id"));

    // Rows are numbered in the order they were written: the wallet locks the character before it
    // appends, so a character's rows never interleave.
    public static SqlAuditCheck LedgerRunningBalance { get; } = new(
        "currency_ledger_running_balance",
        "Every ledger row's balance after equals the sum of its character's deltas up to and including it.",
        AuditSql.Counted(
            "select count(*) from character_currency_ledger",
            """
            select
                ledger.id as ledger_row_id,
                ledger.character_id,
                ledger.balance_after,
                ledger.running_balance
            from (
                select
                    id,
                    character_id,
                    balance_after,
                    sum(delta) over (partition by character_id order by id) as running_balance
                from character_currency_ledger
            ) ledger
            where ledger.balance_after <> ledger.running_balance
            """,
            "ledger_row_id"));

    public static SqlAuditCheck LedgerReason { get; } = new(
        "currency_ledger_reason",
        "Every ledger row names a known source kind and a source id, and an item operation exactly when its kind changes gold inside an item transaction.",
        AuditSql.Counted(
            "select count(*) from character_currency_ledger",
            """
            select
                id as ledger_row_id,
                character_id,
                source_kind,
                item_operation_id
            from character_currency_ledger
            where source_kind <> all(@KnownKinds)
               or length(btrim(source_id)) = 0
               or (source_kind = any(@ItemOperationKinds)) <> (item_operation_id is not null)
            """,
            "ledger_row_id"),
        _ => new
        {
            KnownKinds = KnownKinds.ToArray(),
            ItemOperationKinds = ItemOperationKinds.ToArray()
        });

    public static IReadOnlyList<IAuditCheck> All { get; } =
    [
        BalanceMatchesLedger,
        LedgerRunningBalance,
        LedgerReason
    ];
}
