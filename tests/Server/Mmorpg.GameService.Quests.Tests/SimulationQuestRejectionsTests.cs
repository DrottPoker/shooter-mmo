using System.Reflection;
using Mmorpg.GameService.Items.Contracts;
using Mmorpg.GameService.Platform.Http;

namespace Mmorpg.GameService.Quests.Tests;

// Pins the HTTP status and body of every rejected worker quest operation by its error code. The
// quest codes are client and worker protocol, so the table names them by value; every other code
// keeps the status that Items gives it, which SimulationItemRejectionsTests pins.
public sealed class SimulationQuestRejectionsTests
{
    private const string Message = "The pinned rejection message.";

    private static readonly IReadOnlyDictionary<string, int> StatusByQuestCode = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["quest_npc_access_required"] = 403,
        ["quest_not_found"] = 404,
        ["quest_not_active"] = 409,
        ["quest_already_active"] = 409,
        ["quest_objective_complete"] = 409,
        ["quest_already_completed"] = 422,
        ["quest_requirements_not_met"] = 422,
        ["quest_level_too_low"] = 422,
        ["quest_log_full"] = 422,
        ["quest_objective_invalid"] = 422,
        ["quest_items_missing"] = 422,
        ["quest_not_ready"] = 422,
        ["quest_reward_choice_required"] = 422,
        ["quest_reward_choice_invalid"] = 422,
        ["development_tools_disabled"] = 422
    };

    public static TheoryData<string> QuestCodes()
    {
        return new TheoryData<string>(StatusByQuestCode.Keys.Order(StringComparer.Ordinal));
    }

    public static TheoryData<string> ItemCodes()
    {
        return new TheoryData<string>(Constants(typeof(ItemTransactionErrorCodes)));
    }

    [Fact]
    public void TheTableNamesEveryQuestCodeAndNoItemCode()
    {
        Assert.Equal(StatusByQuestCode.Keys.Order(StringComparer.Ordinal), Constants(typeof(QuestTransactionErrorCodes)));
        Assert.Empty(Constants(typeof(QuestTransactionErrorCodes)).Intersect(Constants(typeof(ItemTransactionErrorCodes))));
    }

    [Theory]
    [MemberData(nameof(QuestCodes))]
    public void AQuestRejectionKeepsItsStatusCodeAndMessage(string code)
    {
        var mapped = SimulationQuestRejections.Map<SimulationItemOperationResponse>(Rejected(code));

        Assert.Equal(
            new ServiceResult<SimulationItemOperationResponse>(null, new ErrorResponse(code, Message), StatusByQuestCode[code]),
            mapped);
    }

    [Theory]
    [MemberData(nameof(ItemCodes))]
    public void AnItemRejectionOnTheQuestRouteKeepsItsItemStatus(string code)
    {
        var result = Rejected(code);

        Assert.Equal(
            SimulationItemRejections.Map<SimulationItemOperationResponse>(result),
            SimulationQuestRejections.Map<SimulationItemOperationResponse>(result));
    }

    private static string[] Constants(Type codes)
    {
        return codes
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static ItemTransactionResult Rejected(string code)
    {
        return new ItemTransactionResult(
            Guid.NewGuid(),
            QuestOperationKinds.AcceptQuest,
            false,
            new ItemTransactionError(code, Message),
            [],
            [],
            [],
            []);
    }
}
