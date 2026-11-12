using System.Reflection;
using Mmorpg.GameService.Items.Contracts;
using Mmorpg.GameService.Platform.Http;

namespace Mmorpg.GameService.Economy.Tests;

// Pins the HTTP status and body of every rejected vendor or gathering operation by its error code.
// The economy codes are client and worker protocol, so the table names them by value; every other
// code keeps the status that Items gives it, which SimulationItemRejectionsTests pins.
public sealed class SimulationEconomyRejectionsTests
{
    private const string Message = "The pinned rejection message.";

    private static readonly IReadOnlyDictionary<string, int> StatusByEconomyCode = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["vendor_access_required"] = 403,
        ["vendor_price_changed"] = 409,
        ["vendor_offer_unavailable"] = 422,
        ["vendor_will_not_buy"] = 422,
        ["gathering_tool_required"] = 422,
        ["gathering_yield_invalid"] = 422
    };

    public static TheoryData<string> EconomyCodes()
    {
        return new TheoryData<string>(StatusByEconomyCode.Keys.Order(StringComparer.Ordinal));
    }

    public static TheoryData<string> ItemCodes()
    {
        return new TheoryData<string>(Constants(typeof(ItemTransactionErrorCodes)));
    }

    [Fact]
    public void TheTableNamesEveryEconomyCodeAndNoItemCode()
    {
        Assert.Equal(StatusByEconomyCode.Keys.Order(StringComparer.Ordinal), Constants(typeof(EconomyTransactionErrorCodes)));
        Assert.Empty(Constants(typeof(EconomyTransactionErrorCodes)).Intersect(Constants(typeof(ItemTransactionErrorCodes))));
    }

    [Theory]
    [MemberData(nameof(EconomyCodes))]
    public void AnEconomyRejectionKeepsItsStatusCodeAndMessage(string code)
    {
        var mapped = SimulationEconomyRejections.Map<SimulationItemOperationResponse>(Rejected(code));

        Assert.Equal(
            new ServiceResult<SimulationItemOperationResponse>(null, new ErrorResponse(code, Message), StatusByEconomyCode[code]),
            mapped);
    }

    [Theory]
    [MemberData(nameof(ItemCodes))]
    public void AnItemRejectionOfAnEconomyOperationKeepsItsItemStatus(string code)
    {
        var result = Rejected(code);

        Assert.Equal(
            SimulationItemRejections.Map<SimulationItemOperationResponse>(result),
            SimulationEconomyRejections.Map<SimulationItemOperationResponse>(result));
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
            EconomyOperationKinds.VendorPurchase,
            false,
            new ItemTransactionError(code, Message),
            [],
            [],
            [],
            []);
    }
}
