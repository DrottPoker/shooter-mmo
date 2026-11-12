using Dapper;
using Mmorpg.WorldData.Items;

namespace Mmorpg.GameService.Items.Transactions.Domain;

// The corpse loot rules of quest drops: protected quest items that a Mob drops for one character,
// with the grant lineage of the quest that asks for them. A drop counts only up to the quantity
// that the character still lacks: what the quest asks for, which IQuestDropDemand reads, less
// the items with the lineage that the character owns anywhere.
internal sealed class CorpseQuestDrops(IQuestDropDemand demand)
{
    private readonly IQuestDropDemand demand = demand;

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
        if (!IsQuestDropItem(definition))
        {
            return 0;
        }

        var definitionId = definition.RuntimeDefinition.Id;
        return await LineageItems.LackingAsync(
            context.Scope,
            characterId,
            Lineage(questId),
            definitionId,
            await demand.WantedAsync(
                context.Scope,
                characterId,
                questId,
                sourceActorDefinitionId,
                definitionId,
                cancellationToken),
            quantity,
            cancellationToken);
    }

    // The drops of one transaction share what their quests still need.
    public QuestDropPlan Plan()
    {
        return new QuestDropPlan(demand);
    }

    // Plans a Loot all in which quest drops share what their quests still need. Only the drops
    // that the plan places take from it; a drop beyond what is left leaves the items, and the
    // plan is made again without it, so a drop that does not fit never holds back the drops
    // behind it.
    public async Task<LootAllPlan> PlanLootAllAsync(
        ItemTransactionContext context,
        Guid characterId,
        List<LootAllCorpseItem> items,
        IReadOnlyDictionary<Guid, PlannedQuestDrop> questDropItems,
        Func<IReadOnlyList<LootAllCorpseItem>, LootAllPlan> plan,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var planned = plan(items);
            var shared = Plan();
            var excess = new HashSet<Guid>();
            foreach (var step in planned.Steps)
            {
                if (questDropItems.TryGetValue(step.Item.ItemInstanceId, out var drop)
                    && !await shared.TakeAllAsync(
                        context,
                        characterId,
                        drop.QuestId,
                        drop.SourceActorDefinitionId,
                        drop.Definition,
                        step.Quantity,
                        cancellationToken))
                {
                    excess.Add(step.Item.ItemInstanceId);
                }
            }

            if (excess.Count == 0)
            {
                return planned;
            }

            items.RemoveAll(item => excess.Contains(item.ItemInstanceId));
        }
    }

    // The lineage that a quest drop of the quest carries.
    public static ItemLineage Lineage(string questId)
    {
        return new ItemLineage(ItemPolicySourceKinds.QuestGrant, questId);
    }

    // Only a protected quest item carries a quest's lineage. It is never equippable, because
    // equipment counts for no collect objective and a Loot all would equip it.
    public static bool IsQuestDropItem(ResolvedItemDefinition definition)
    {
        var runtime = definition.RuntimeDefinition;
        return string.Equals(runtime.Category, ItemCategoryIds.QuestItem, StringComparison.Ordinal)
            && runtime.DefaultPolicies.Contains(ItemPolicyIds.ProtectedOnDeath, StringComparer.Ordinal)
            && runtime.EquipmentSlots.Length == 0
            && !definition.IsBag;
    }

    // A quest drop lies in the durable corpse of the Mob that dropped it, which belongs to the
    // character it dropped for. Returns the Mob's actor definition, or null for any other corpse.
    public static Task<string?> OwnMobCorpseSourceAsync(
        ItemTransactionContext context,
        Guid corpseId,
        Guid characterId,
        CancellationToken cancellationToken)
    {
        return context.Connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            """
            select source_npc_definition_id
            from corpses
            where id = @CorpseId
              and source_type = 'persistent_npc'
              and owner_character_id = @CharacterId;
            """,
            new { CorpseId = corpseId, CharacterId = characterId },
            context.Transaction,
            cancellationToken: cancellationToken));
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

// A quest drop that a Loot all may move, with what its quest needs to know about it.
internal sealed record PlannedQuestDrop(string QuestId, string SourceActorDefinitionId, ResolvedItemDefinition Definition);

// What the quest drops of one transaction still need: drops of one quest item share it, so two
// letters in one corpse never take more than the quest asks for.
internal sealed class QuestDropPlan(IQuestDropDemand demand)
{
    private readonly IQuestDropDemand demand = demand;
    private readonly LineageNeedPlan plan = new();

    // The part of a drop that its quest still needs after the drops planned before it.
    public Task<int> TakeAsync(
        ItemTransactionContext context,
        Guid characterId,
        string questId,
        string sourceActorDefinitionId,
        ResolvedItemDefinition definition,
        int quantity,
        CancellationToken cancellationToken)
    {
        if (!CorpseQuestDrops.IsQuestDropItem(definition))
        {
            return Task.FromResult(0);
        }

        var definitionId = definition.RuntimeDefinition.Id;
        return plan.TakeAsync(
            context.Scope,
            characterId,
            CorpseQuestDrops.Lineage(questId),
            definitionId,
            quantity,
            token => demand.WantedAsync(
                context.Scope,
                characterId,
                questId,
                sourceActorDefinitionId,
                definitionId,
                token),
            cancellationToken);
    }

    // Whether the quest still needs all of a drop after the drops planned before it; only then
    // does the plan count it.
    public Task<bool> TakeAllAsync(
        ItemTransactionContext context,
        Guid characterId,
        string questId,
        string sourceActorDefinitionId,
        ResolvedItemDefinition definition,
        int quantity,
        CancellationToken cancellationToken)
    {
        var definitionId = definition.RuntimeDefinition.Id;
        return plan.TakeAllAsync(
            context.Scope,
            characterId,
            CorpseQuestDrops.Lineage(questId),
            definitionId,
            quantity,
            token => CorpseQuestDrops.IsQuestDropItem(definition)
                ? demand.WantedAsync(
                    context.Scope,
                    characterId,
                    questId,
                    sourceActorDefinitionId,
                    definitionId,
                    token)
                : Task.FromResult(0),
            cancellationToken);
    }
}
