using System.Reflection;
using Mmorpg.GameService.Items.Audit;
using Mmorpg.GameService.Items.Transactions;

namespace Mmorpg.GameService.Items.Tests;

// Pins the Items checks of the economy audit by id, which the metrics, the alert, and the
// documents name, and the quantity effect of every Items operation kind, so a new kind cannot
// escape the journal's conservation check by accident.
public sealed class ItemAuditTests
{
    [Fact]
    public void TheItemsChecksKeepTheirIds()
    {
        Assert.Equal(
            [
                "item_custody_missing",
                "item_custody_duplicated",
                "item_slot_invalid",
                "item_slot_shared",
                "item_quantity_out_of_range",
                "item_definition_unknown",
                "item_destroyed_but_present",
                "item_journal_removed_but_present",
                "item_journal_quantity_changed",
                "item_operation_pending_too_long",
                "currency_change_operation_committed"
            ],
            ItemsAudit.Checks.Select(check => check.Id).ToArray());
        Assert.All(ItemsAudit.Checks, check => Assert.False(string.IsNullOrWhiteSpace(check.Description)));
    }

    [Fact]
    public void EveryItemsOperationKindHasExactlyOneQuantityEffect()
    {
        var kinds = typeof(ItemOperationKinds)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var classified = ItemOperationQuantityEffects.Moving
            .Concat(ItemOperationQuantityEffects.Creating)
            .Concat(ItemOperationQuantityEffects.Removing)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(kinds, classified);
    }
}
