using Mmorpg.GameService.Quests.Contracts;
using Mmorpg.WorldData.Items;
using Mmorpg.WorldData.Quests;

namespace Mmorpg.GameService.Items.Transactions.Domain;

// Quest drops: protected quest items that a Mob drops for one character, with the grant lineage
// of the quest that asks for them. A drop counts while the quest is active and its current step
// holds the collect objective for the item with the Mob as a drop source, and only up to the
// quantity that the character still needs, counting the items with the lineage that it owns
// anywhere.
internal sealed class QuestDrops(IQuestDefinitions questDefinitions, ICharacterQuestLog questLog)
{
    // The source type of a durable Mob corpse.
    private const string MobCorpseSourceType = "persistent_npc";

    // The part of a dropped quantity that the character still needs, or 0 when its quest does
    // not ask for the drop.
    public async Task<int> NeededAsync(
        ItemTransactionContext context,
        Guid characterId,
        string questId,
        string sourceActorDefinitionId,
        ResolvedItemDefinition definition,
        int quantity,
        CancellationToken cancellationToken)
    {
        if (!IsQuestDropItem(definition) || questDefinitions.Find(questId) is not { } quest)
        {
            return 0;
        }

        var log = await questLog.ReadAsync(context.Scope, characterId, cancellationToken);
        var active = log.ActiveQuests.FirstOrDefault(value =>
            string.Equals(value.QuestId, quest.Id, StringComparison.Ordinal));
        var objective = active is null
            ? null
            : QuestProgression.CurrentStep(quest, active).Objectives.FirstOrDefault(value =>
                string.Equals(value.ItemId, definition.RuntimeDefinition.Id, StringComparison.Ordinal)
                && QuestRules.FindDropSource(value, sourceActorDefinitionId) is not null);
        if (objective is null)
        {
            return 0;
        }

        var owned = await QuestLineageItems.CountOwnedAsync(
            context,
            characterId,
            quest.Id,
            definition.RuntimeDefinition.Id,
            cancellationToken);
        return Math.Clamp(objective.Count - owned, 0, quantity);
    }

    // Only a protected quest item carries a quest's lineage.
    public static bool IsQuestDropItem(ResolvedItemDefinition definition)
    {
        var runtime = definition.RuntimeDefinition;
        return string.Equals(runtime.Category, ItemCategoryIds.QuestItem, StringComparison.Ordinal)
            && runtime.DefaultPolicies.Contains(ItemPolicyIds.ProtectedOnDeath, StringComparer.Ordinal)
            && !definition.IsBag;
    }

    // A quest drop lies in the durable corpse of the Mob that dropped it, which belongs to the
    // character it dropped for.
    public static bool IsOwnMobCorpse(LockedCorpseRow corpse, Guid characterId)
    {
        return string.Equals(corpse.SourceType, MobCorpseSourceType, StringComparison.Ordinal)
            && corpse.OwnerCharacterId == characterId
            && !string.IsNullOrEmpty(corpse.SourceNpcDefinitionId);
    }

    // The quest whose lineage an item carries: its active protected policy that a quest granted.
    public static string? LineageQuestId(IReadOnlyList<LockedItemPolicy> policies)
    {
        return policies.FirstOrDefault(policy =>
                string.Equals(policy.Status, ItemPolicyRules.ActiveStatus, StringComparison.Ordinal)
                && string.Equals(policy.PolicyKind, ItemPolicyIds.ProtectedOnDeath, StringComparison.Ordinal)
                && string.Equals(policy.SourceKind, ItemPolicySourceKinds.QuestGrant, StringComparison.Ordinal))
            ?.SourceId;
    }

    // The policies a quest drop gets when it is created, so its stack state can be compared with a
    // stack that already carries the lineage.
    public static IReadOnlyList<LockedItemPolicy> LineagePolicies(ResolvedItemDefinition definition, string questId)
    {
        return definition.RuntimeDefinition.DefaultPolicies
            .Select(policyKind => new LockedItemPolicy
            {
                PolicyKind = policyKind,
                Status = ItemPolicyRules.ActiveStatus,
                SourceKind = ItemPolicySourceKinds.QuestGrant,
                SourceId = questId
            })
            .ToArray();
    }
}
