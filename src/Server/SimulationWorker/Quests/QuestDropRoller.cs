using Mmorpg.SimulationWorker.Combat;
using Mmorpg.SimulationWorker.Corpses;
using Mmorpg.SimulationWorker.Sessions;
using Mmorpg.WorldData.Quests;

namespace Mmorpg.SimulationWorker.Quests;

// Rolls a dying Mob's quest drops for a character it credits: once for every collect objective
// of a current step that names the Mob as a drop source, by the quest log the worker keeps, in
// catalog order so a seeded roll repeats. While that log may miss a change, it rolls for every
// quest that the Mob drops for. GameService lets through only what the quests still need, so a
// roll never has to know the character's items. Loop-confined; the quest logs synchronize
// themselves.
public sealed class QuestDropRoller
{
    private readonly CharacterQuestLogStore questLogs;
    private readonly Dictionary<string, DropObjective[]> objectivesByMob;

    public QuestDropRoller(CharacterQuestLogStore questLogs, QuestCatalogRuntimeDocument catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        this.questLogs = questLogs ?? throw new ArgumentNullException(nameof(questLogs));
        var objectives = new Dictionary<string, List<DropObjective>>(StringComparer.Ordinal);
        foreach (var quest in catalog.Quests)
        {
            foreach (var step in quest.Steps)
            {
                foreach (var objective in step.Objectives)
                {
                    foreach (var mobActorId in (objective.DropSources ?? [])
                                 .Select(source => source.ActorId)
                                 .Distinct(StringComparer.Ordinal))
                    {
                        if (QuestRules.FindDropSource(objective, mobActorId) is { } source)
                        {
                            if (!objectives.TryGetValue(mobActorId, out var dropping))
                            {
                                objectives.Add(mobActorId, dropping = []);
                            }

                            dropping.Add(new DropObjective(quest.Id, step.Id, objective.ItemId, source));
                        }
                    }
                }
            }
        }

        objectivesByMob = objectives.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToArray(),
            StringComparer.Ordinal);
    }

    public IReadOnlyList<MobCorpseQuestDropSeed> Roll(
        ActiveSimulationSession session,
        string mobActorId,
        ICombatRandom random)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(random);
        if (session.IsSyntheticBot
            || !objectivesByMob.TryGetValue(mobActorId, out var objectives)
            || !questLogs.TryGet(session.CharacterId, session.SimulationSessionId, out var log))
        {
            return [];
        }

        var stale = questLogs.IsStale(session.CharacterId, session.SimulationSessionId);
        List<MobCorpseQuestDropSeed>? drops = null;
        foreach (var objective in objectives)
        {
            if (!stale
                && !(log!.ActiveQuests.TryGetValue(objective.QuestId, out var active)
                    && string.Equals(active.StepId, objective.StepId, StringComparison.Ordinal)))
            {
                continue;
            }

            if (objective.Source.ChanceBasisPoints < MobLootRoller.BasisPointsPerWhole
                && random.NextInclusive(0, MobLootRoller.BasisPointsPerWhole - 1) >= objective.Source.ChanceBasisPoints)
            {
                continue;
            }

            (drops ??= []).Add(new MobCorpseQuestDropSeed(objective.QuestId, objective.ItemId, objective.Source.Quantity));
        }

        return drops ?? (IReadOnlyList<MobCorpseQuestDropSeed>)[];
    }

    private sealed record DropObjective(
        string QuestId,
        string StepId,
        string ItemId,
        QuestDropSourceDefinition Source);
}
