using Mmorpg.GameService.Items.Contracts;
using Mmorpg.GameService.Platform.Http;

namespace Mmorpg.GameService.Quests;

// Maps a rejected worker quest operation to its HTTP status by the class of its code: the quest
// codes here, every other code as Items maps its own.
internal static class SimulationQuestRejections
{
    public static ServiceResult<T> Map<T>(ItemTransactionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var error = result.Error
            ?? throw new InvalidOperationException("A rejected quest operation has no error.");
        return error.Code switch
        {
            QuestTransactionErrorCodes.QuestNpcAccessRequired =>
                ServiceResult<T>.Forbidden(error.Code, error.Message),
            QuestTransactionErrorCodes.QuestNotFound =>
                ServiceResult<T>.NotFound(error.Code, error.Message),
            QuestTransactionErrorCodes.QuestNotActive or
            QuestTransactionErrorCodes.QuestAlreadyActive or
            QuestTransactionErrorCodes.QuestObjectiveComplete =>
                ServiceResult<T>.Conflict(error.Code, error.Message),
            QuestTransactionErrorCodes.QuestAlreadyCompleted or
            QuestTransactionErrorCodes.QuestRequirementsNotMet or
            QuestTransactionErrorCodes.QuestLevelTooLow or
            QuestTransactionErrorCodes.QuestLogFull or
            QuestTransactionErrorCodes.QuestObjectiveInvalid or
            QuestTransactionErrorCodes.QuestItemsMissing or
            QuestTransactionErrorCodes.QuestNotReady or
            QuestTransactionErrorCodes.QuestRewardChoiceRequired or
            QuestTransactionErrorCodes.QuestRewardChoiceInvalid or
            QuestTransactionErrorCodes.DevelopmentToolsDisabled =>
                ServiceResult<T>.UnprocessableEntity(error.Code, error.Message),
            _ => SimulationItemRejections.Map<T>(result)
        };
    }
}
