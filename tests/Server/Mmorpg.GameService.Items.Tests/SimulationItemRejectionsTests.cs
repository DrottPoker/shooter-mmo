using System.Reflection;
using Mmorpg.GameService.Items.Contracts;
using Mmorpg.GameService.Platform.Http;

namespace Mmorpg.GameService.Items.Tests;

// Pins the HTTP status and body of every rejected worker item operation by its error code. The
// codes are client and worker protocol, so the table names them by value. The quest and economy
// codes and their mappings are pinned in Mmorpg.GameService.Quests.Tests and
// Mmorpg.GameService.Economy.Tests.
public sealed class SimulationItemRejectionsTests
{
    private const string Message = "The pinned rejection message.";

    private static readonly IReadOnlyDictionary<string, int> StatusByCode = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["simulation_session_invalid"] = 401,
        ["item_authority_required"] = 403,
        ["wrong_simulation_worker"] = 403,
        ["bank_access_required"] = 403,
        ["recovery_access_required"] = 403,
        ["item_not_found"] = 404,
        ["item_not_owned"] = 404,
        ["recovery_delivery_not_found"] = 404,
        ["item_policy_not_found"] = 404,
        ["item_state_conflict"] = 409,
        ["item_operation_conflict"] = 409,
        ["item_transaction_timeout"] = 409,
        ["item_slot_occupied"] = 409,
        ["equipment_slot_occupied"] = 409,
        ["item_quantity_changed"] = 409,
        ["bag_state_changed"] = 409,
        ["worker_runtime_changed"] = 409,
        ["inventory_full"] = 409,
        ["quest_item_not_needed"] = 409,
        ["item_operation_id_invalid"] = 422,
        ["item_command_payload_too_large"] = 422,
        ["item_offline_access_required"] = 422,
        ["item_slot_incompatible"] = 422,
        ["item_stack_incompatible"] = 422,
        ["item_stack_limit_exceeded"] = 422,
        ["item_policy_restricted"] = 422,
        ["quest_grant_invalid"] = 422,
        ["currency_insufficient"] = 422,
        ["currency_limit_exceeded"] = 422,
        ["vendor_unavailable"] = 422,
        ["item_destroy_forbidden"] = 422,
        ["bag_not_empty"] = 422,
        ["carry_weight_limit_exceeded"] = 422,
        ["equipment_slot_incompatible"] = 422,
        ["equipment_slot_blocked"] = 422,
        ["equipment_level_requirement_not_met"] = 422,
        ["ammunition_not_equipped"] = 422,
        ["recovery_delivery_not_expired"] = 422,
        ["death_event_conflict"] = 422,
        ["mob_corpse_invalid"] = 422,
        ["mob_corpse_empty"] = 422,
        ["mob_loot_grant_invalid"] = 422,
        ["corpse_not_found"] = 422,
        ["corpse_not_expired"] = 422,
        ["corpse_state_changed"] = 422,
        ["corpse_expired"] = 422,
        ["corpse_invalidated"] = 422,
        ["corpse_not_lootable"] = 422,
        ["item_already_looted"] = 422,
        ["item_simulation_operation_invalid"] = 422
    };

    public static TheoryData<string> Codes()
    {
        return new TheoryData<string>(StatusByCode.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TheTableNamesEveryErrorCode()
    {
        var codes = typeof(ItemTransactionErrorCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(StatusByCode.Keys.Order(StringComparer.Ordinal), codes);
    }

    [Theory]
    [MemberData(nameof(Codes))]
    public void ARejectionKeepsItsStatusCodeAndMessage(string code)
    {
        var mapped = SimulationItemRejections.Map<SimulationItemOperationResponse>(Rejected(code));

        Assert.Equal(
            new ServiceResult<SimulationItemOperationResponse>(null, new ErrorResponse(code, Message), StatusByCode[code]),
            mapped);
    }

    private static ItemTransactionResult Rejected(string code)
    {
        return new ItemTransactionResult(
            Guid.NewGuid(),
            ItemOperationKinds.Grant,
            false,
            new ItemTransactionError(code, Message),
            [],
            [],
            [],
            []);
    }
}
