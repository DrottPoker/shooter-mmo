using Mmorpg.GameService.Platform.Http;

namespace Mmorpg.GameService.Items;

// Maps a rejected worker item or quest operation to its HTTP status by the class of its code.
internal static class SimulationItemRejections
{
    public static ServiceResult<T> Map<T>(ItemTransactionResult result)
    {
        var error = result.Error
            ?? throw new InvalidOperationException("A rejected item operation has no error.");
        return error.Code switch
        {
            ItemTransactionErrorCodes.SimulationSessionInvalid =>
                ServiceResult<T>.Unauthorized(error.Code, error.Message),
            ItemTransactionErrorCodes.AuthorityRequired or
            ItemTransactionErrorCodes.WrongSimulationWorker or
            ItemTransactionErrorCodes.BankAccessRequired or
            ItemTransactionErrorCodes.RecoveryAccessRequired or
            ItemTransactionErrorCodes.VendorAccessRequired or
            ItemTransactionErrorCodes.QuestNpcAccessRequired =>
                ServiceResult<T>.Forbidden(error.Code, error.Message),
            ItemTransactionErrorCodes.ItemNotFound or
            ItemTransactionErrorCodes.ItemNotOwned or
            ItemTransactionErrorCodes.RecoveryDeliveryNotFound or
            ItemTransactionErrorCodes.ItemPolicyNotFound or
            ItemTransactionErrorCodes.QuestNotFound =>
                ServiceResult<T>.NotFound(error.Code, error.Message),
            ItemTransactionErrorCodes.ItemStateConflict or
            ItemTransactionErrorCodes.ItemOperationConflict or
            ItemTransactionErrorCodes.ItemTransactionTimeout or
            ItemTransactionErrorCodes.ItemSlotOccupied or
            ItemTransactionErrorCodes.EquipmentSlotOccupied or
            ItemTransactionErrorCodes.ItemQuantityChanged or
            ItemTransactionErrorCodes.BagStateChanged or
            ItemTransactionErrorCodes.WorkerRuntimeChanged or
            ItemTransactionErrorCodes.VendorPriceChanged or
            ItemTransactionErrorCodes.InventoryFull or
            ItemTransactionErrorCodes.QuestNotActive or
            ItemTransactionErrorCodes.QuestAlreadyActive =>
                ServiceResult<T>.Conflict(error.Code, error.Message),
            _ => ServiceResult<T>.UnprocessableEntity(error.Code, error.Message)
        };
    }
}
