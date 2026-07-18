namespace AuthService.Items;

public sealed record NpcItemLifecycleOptions(
    long InsurancePrice,
    string QuestGrantId,
    string QuestItemDefinitionId,
    int QuestItemQuantity)
{
    public static NpcItemLifecycleOptions Default { get; } = new(
        100,
        "quest.local.signal_transponder",
        "quest_item.signal_transponder",
        1);

    public static NpcItemLifecycleOptions FromConfiguration(
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection("Items:NpcLifecycle");
        var insurancePrice = section.GetValue<long?>("InsurancePrice")
            ?? Default.InsurancePrice;
        var questGrantId = section["QuestGrantId"] ?? Default.QuestGrantId;
        var questItemDefinitionId = section["QuestItemDefinitionId"]
            ?? Default.QuestItemDefinitionId;
        var questItemQuantity = section.GetValue<int?>("QuestItemQuantity")
            ?? Default.QuestItemQuantity;

        if (insurancePrice <= 0
            || !IsValidLineage(questGrantId)
            || !IsValidLineage(questItemDefinitionId)
            || questItemQuantity <= 0)
        {
            throw new InvalidOperationException(
                "Items:NpcLifecycle must define a positive insurance price, valid quest ids, and a positive quest item quantity.");
        }

        return new NpcItemLifecycleOptions(
            insurancePrice,
            questGrantId.Trim(),
            questItemDefinitionId.Trim(),
            questItemQuantity);
    }

    private static bool IsValidLineage(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Length <= 128
            && value.All(character =>
                char.IsAsciiLetterOrDigit(character)
                || character is '-' or '_' or '.');
    }
}
