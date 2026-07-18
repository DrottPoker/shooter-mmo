namespace AuthService.Items;

public static class ItemTransactionErrorCodes
{
    public const string AuthorityRequired = "item_authority_required";
    public const string ItemNotFound = "item_not_found";
    public const string ItemNotOwned = "item_not_owned";
    public const string ItemStateConflict = "item_state_conflict";
    public const string ItemOperationConflict = "item_operation_conflict";
    public const string OfflineAccessRequired = "item_offline_access_required";
    public const string ItemSlotOccupied = "item_slot_occupied";
    public const string ItemSlotIncompatible = "item_slot_incompatible";
    public const string ItemStackIncompatible = "item_stack_incompatible";
    public const string ItemStackLimitExceeded = "item_stack_limit_exceeded";
    public const string ItemPolicyRestricted = "item_policy_restricted";
    public const string ItemPolicyNotFound = "item_policy_not_found";
    public const string QuestGrantInvalid = "quest_grant_invalid";
    public const string CurrencyInsufficient = "currency_insufficient";
    public const string ItemDestroyForbidden = "item_destroy_forbidden";
    public const string BagNotEmpty = "bag_not_empty";
    public const string BagStateChanged = "bag_state_changed";
    public const string CarryWeightLimitExceeded = "carry_weight_limit_exceeded";
    public const string EquipmentSlotOccupied = "equipment_slot_occupied";
    public const string EquipmentSlotIncompatible = "equipment_slot_incompatible";
    public const string SecureContainerItemForbidden = "secure_container_item_forbidden";
    public const string BankAccessRequired = "bank_access_required";
    public const string RecoveryAccessRequired = "recovery_access_required";
    public const string RecoveryDeliveryNotFound = "recovery_delivery_not_found";
    public const string DeathEventConflict = "death_event_conflict";
    public const string CorpseNotFound = "corpse_not_found";
    public const string CorpseNotExpired = "corpse_not_expired";
    public const string CorpseStateChanged = "corpse_state_changed";
    public const string CorpseExpired = "corpse_expired";
    public const string CorpseInvalidated = "corpse_invalidated";
    public const string ItemAlreadyLooted = "item_already_looted";
    public const string ItemQuantityChanged = "item_quantity_changed";
    public const string WrongSimulationWorker = "wrong_simulation_worker";
    public const string WorkerRuntimeChanged = "worker_runtime_changed";
    public const string SimulationSessionInvalid = "simulation_session_invalid";
}

internal sealed class ItemTransactionRejectedException(
    string code,
    string message) : Exception(message)
{
    public string Code { get; } = code;
}
