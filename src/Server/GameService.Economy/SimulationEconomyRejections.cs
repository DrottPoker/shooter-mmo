using Mmorpg.GameService.Items.Contracts;
using Mmorpg.GameService.Platform.Http;

namespace Mmorpg.GameService.Economy;

// Maps a rejected vendor or gathering operation to its HTTP status by the class of its code: the
// economy codes here, every other code as Items maps its own.
internal static class SimulationEconomyRejections
{
    public static ServiceResult<T> Map<T>(ItemTransactionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var error = result.Error
            ?? throw new InvalidOperationException("A rejected economy operation has no error.");
        return error.Code switch
        {
            EconomyTransactionErrorCodes.VendorAccessRequired =>
                ServiceResult<T>.Forbidden(error.Code, error.Message),
            EconomyTransactionErrorCodes.VendorPriceChanged =>
                ServiceResult<T>.Conflict(error.Code, error.Message),
            EconomyTransactionErrorCodes.VendorOfferUnavailable or
            EconomyTransactionErrorCodes.VendorWillNotBuy or
            EconomyTransactionErrorCodes.GatheringToolRequired or
            EconomyTransactionErrorCodes.GatheringYieldInvalid =>
                ServiceResult<T>.UnprocessableEntity(error.Code, error.Message),
            _ => SimulationItemRejections.Map<T>(result)
        };
    }
}
