namespace AuthService.Items;

public static class ItemTransactionErrorCodes
{
    public const string AuthorityRequired = "item_authority_required";
    public const string ItemNotFound = "item_not_found";
    public const string ItemNotOwned = "item_not_owned";
    public const string ItemStateConflict = "item_state_conflict";
    public const string ItemOperationConflict = "item_operation_conflict";
    public const string ItemSlotOccupied = "item_slot_occupied";
    public const string ItemSlotIncompatible = "item_slot_incompatible";
    public const string ItemStackIncompatible = "item_stack_incompatible";
    public const string ItemStackLimitExceeded = "item_stack_limit_exceeded";
    public const string ItemPolicyRestricted = "item_policy_restricted";
    public const string ItemDestroyForbidden = "item_destroy_forbidden";
    public const string BagNotEmpty = "bag_not_empty";
    public const string BagStateChanged = "bag_state_changed";
    public const string CarryWeightLimitExceeded = "carry_weight_limit_exceeded";
    public const string EquipmentSlotOccupied = "equipment_slot_occupied";
    public const string EquipmentSlotIncompatible = "equipment_slot_incompatible";
    public const string SecureContainerItemForbidden = "secure_container_item_forbidden";
    public const string RecoveryAccessRequired = "recovery_access_required";
    public const string RecoveryDeliveryNotFound = "recovery_delivery_not_found";
    public const string ItemQuantityChanged = "item_quantity_changed";
}

internal sealed class ItemTransactionRejectedException(
    string code,
    string message) : Exception(message)
{
    public string Code { get; } = code;
}
