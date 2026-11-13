using Mmorpg.GameService.Platform.Http;

namespace Mmorpg.GameService.Items.Contracts;

// Maps a rejected worker item operation to its HTTP status by the class of its code. The
// modules above Items map their own codes first and fall back to this mapping.
public static class SimulationItemRejections
{
    public static ServiceResult<T> Map<T>(ItemTransactionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var error = result.Error
            ?? throw new InvalidOperationException("A rejected item operation has no error.");
        return error.Code switch
        {
            ItemTransactionErrorCodes.SimulationSessionInvalid or
            ItemTransactionErrorCodes.AccountSessionReplaced or
            ItemTransactionErrorCodes.AccountSessionEnded =>
                ServiceResult<T>.Unauthorized(error.Code, error.Message),
            ItemTransactionErrorCodes.AuthorityRequired or
            ItemTransactionErrorCodes.WrongSimulationWorker or
            ItemTransactionErrorCodes.BankAccessRequired or
            ItemTransactionErrorCodes.RecoveryAccessRequired =>
                ServiceResult<T>.Forbidden(error.Code, error.Message),
            ItemTransactionErrorCodes.ItemNotFound or
            ItemTransactionErrorCodes.ItemNotOwned or
            ItemTransactionErrorCodes.RecoveryDeliveryNotFound or
            ItemTransactionErrorCodes.ItemPolicyNotFound =>
                ServiceResult<T>.NotFound(error.Code, error.Message),
            ItemTransactionErrorCodes.ItemStateConflict or
            ItemTransactionErrorCodes.ItemOperationConflict or
            ItemTransactionErrorCodes.ItemTransactionTimeout or
            ItemTransactionErrorCodes.ItemSlotOccupied or
            ItemTransactionErrorCodes.EquipmentSlotOccupied or
            ItemTransactionErrorCodes.ItemQuantityChanged or
            ItemTransactionErrorCodes.BagStateChanged or
            ItemTransactionErrorCodes.WorkerRuntimeChanged or
            ItemTransactionErrorCodes.InventoryFull or
            ItemTransactionErrorCodes.QuestItemNotNeeded =>
                ServiceResult<T>.Conflict(error.Code, error.Message),
            _ => ServiceResult<T>.UnprocessableEntity(error.Code, error.Message)
        };
    }
}
