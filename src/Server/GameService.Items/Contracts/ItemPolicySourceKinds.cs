namespace Mmorpg.GameService.Items.Contracts;

public static class ItemPolicySourceKinds
{
    public const string CatalogDefault = "catalog_default";
    public const string InsuranceService = "insurance_service";
    public const string QuestGrant = "quest_grant";

    // The protection source that inventory snapshots and item state changes show players.
    internal static string ToProtectionSource(string sourceKind)
    {
        return sourceKind switch
        {
            InsuranceService => "insurance_npc",
            QuestGrant => "quest_grant",
            CatalogDefault => "catalog_default",
            _ => "system"
        };
    }
}
