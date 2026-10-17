using Mmorpg.Server.Common.Identity;

namespace Mmorpg.GameService.Items;

// The insurance price and the one quest whose item the NPC lifecycle grants, until quest
// and pricing content exist. Every value comes from configuration; nothing is defaulted
// in code, so a content rename can never leave a stale id behind.
public sealed record NpcItemLifecycleOptions(
    long InsurancePrice,
    string QuestGrantId,
    string QuestItemDefinitionId,
    int QuestItemQuantity)
{
    public static NpcItemLifecycleOptions FromConfiguration(
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection("Items:NpcLifecycle");
        var insurancePrice = section.GetValue<long?>("InsurancePrice");
        var questGrantId = section["QuestGrantId"];
        var questItemDefinitionId = section["QuestItemDefinitionId"];
        var questItemQuantity = section.GetValue<int?>("QuestItemQuantity");

        if (insurancePrice is not > 0
            || !Identifiers.IsContentIdentifier(questGrantId)
            || !Identifiers.IsContentIdentifier(questItemDefinitionId)
            || questItemQuantity is not > 0)
        {
            throw new InvalidOperationException(
                "Items:NpcLifecycle must define a positive insurance price, valid quest ids, and a positive quest item quantity.");
        }

        return new NpcItemLifecycleOptions(
            insurancePrice.Value,
            questGrantId!.Trim(),
            questItemDefinitionId!.Trim(),
            questItemQuantity.Value);
    }

}
