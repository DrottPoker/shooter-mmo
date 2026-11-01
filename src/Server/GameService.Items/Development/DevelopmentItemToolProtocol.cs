using System.Text.Json;

namespace Mmorpg.GameService.Items.Development;

internal sealed record DevelopmentItemToolResponse(
    bool Success,
    string Message,
    DevelopmentItemToolData? Data);

internal sealed record DevelopmentItemToolData(
    IReadOnlyList<DevelopmentItemToolCharacter> Characters,
    IReadOnlyList<DevelopmentItemToolDefinition> Definitions,
    IReadOnlyList<DevelopmentItemToolPackage> Packages);

internal sealed record DevelopmentItemToolCharacter(
    Guid CharacterId,
    string CharacterName,
    string AccountUsername,
    bool IsOnline,
    int ItemCount,
    int RecoveryDeliveryCount,
    long ItemStateRevision,
    long CarriedWeight,
    long CarryCapacity,
    long Currency);

internal sealed record DevelopmentItemToolDefinition(
    string DefinitionId,
    string DisplayName,
    string CategoryId,
    int MaximumStackSize,
    long UnitWeight);

internal sealed record DevelopmentItemToolPackage(
    string PackageId,
    string DisplayName,
    string Description,
    bool RequiresEmptyCharacter);

internal static class DevelopmentItemToolProtocol
{
    public const string ResponsePrefix = "MMORPG_DEV_ITEMS_JSON:";

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public static string Serialize(DevelopmentItemToolResponse response)
    {
        return ResponsePrefix + JsonSerializer.Serialize(response, JsonOptions);
    }
}
