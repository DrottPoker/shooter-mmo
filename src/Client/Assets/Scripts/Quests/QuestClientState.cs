using System;
using System.Collections.Generic;
using System.Linq;
using Mmorpg.Client.Api;
using Mmorpg.GameProtocol;
using Mmorpg.WorldData.Quests;

namespace Mmorpg.Client.Quests
{
    public enum QuestClientStatus
    {
        // No quest log of a character is loaded.
        Unavailable,
        Loading,
        Ready,

        // The quest log could not be read, or the bundled catalog is invalid.
        Failed,

        // GameService runs another quest catalog than this client build bundles.
        UpdateRequired
    }

    public enum QuestLogApplyResult
    {
        Applied,

        // The change is not newer than the log, such as one a reload already holds.
        Stale,

        // The log missed a change or the worker asks for a reload, so GameService's log must
        // replace it.
        ReloadRequired
    }

    // An active quest at its current step, with the stored progress of the step's counted
    // objectives. Collect objectives count the carried items instead.
    public sealed class QuestClientActiveQuest
    {
        public QuestClientActiveQuest(string questId, string stepId, IReadOnlyDictionary<string, int> progress)
        {
            QuestId = questId;
            StepId = stepId;
            Progress = progress ?? new Dictionary<string, int>(StringComparer.Ordinal);
        }

        public string QuestId { get; }

        public string StepId { get; }

        public IReadOnlyDictionary<string, int> Progress { get; }

        public int ProgressOf(string objectiveId)
        {
            return objectiveId != null && Progress.TryGetValue(objectiveId, out var progress) ? progress : 0;
        }
    }

    // One quest that a realtime change touched, as the log held it before and after: an active
    // quest or none, and its completion count.
    public sealed class QuestClientChange
    {
        public QuestClientChange(
            string questId,
            QuestClientActiveQuest before,
            QuestClientActiveQuest after,
            int completionsBefore,
            int completionsAfter)
        {
            QuestId = questId;
            Before = before;
            After = after;
            CompletionsBefore = completionsBefore;
            CompletionsAfter = completionsAfter;
        }

        public string QuestId { get; }

        public QuestClientActiveQuest Before { get; }

        public QuestClientActiveQuest After { get; }

        public int CompletionsBefore { get; }

        public int CompletionsAfter { get; }
    }

    // The quest log of the joined character as GameService last committed it, with the bundled
    // catalog that names every quest and the character's level. It is read once per join and
    // patched from the realtime changes in revision order; a change that does not follow the
    // log's revision asks for a reload instead. It decides no quest outcome.
    public sealed class QuestClientState
    {
        private readonly Dictionary<string, QuestClientActiveQuest> activeQuests =
            new Dictionary<string, QuestClientActiveQuest>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> completionCounts =
            new Dictionary<string, int>(StringComparer.Ordinal);

        public event Action Changed;

        // The quests that one realtime change touched, after Changed; a load raises none.
        public event Action<IReadOnlyList<QuestClientChange>> QuestsChanged;

        public ClientQuestCatalog Catalog { get; private set; }

        public QuestClientStatus Status { get; private set; } = QuestClientStatus.Unavailable;

        public string ErrorCode { get; private set; } = string.Empty;

        public string ErrorMessage { get; private set; } = string.Empty;

        public Guid CharacterId { get; private set; }

        public int CharacterLevel { get; private set; } = 1;

        // The quest log revision the state holds, or -1 before a load.
        public long Revision { get; private set; } = -1;

        public IReadOnlyDictionary<string, QuestClientActiveQuest> ActiveQuests
        {
            get { return activeQuests; }
        }

        public IReadOnlyDictionary<string, int> CompletionCounts
        {
            get { return completionCounts; }
        }

        public bool IsReady
        {
            get { return Status == QuestClientStatus.Ready && Catalog != null; }
        }

        public void SetCatalog(ClientQuestCatalog catalog)
        {
            Catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            Changed?.Invoke();
        }

        // Forgets the previous character's log; the next load reads this one's.
        public void PrepareCharacter(Guid characterId, int level)
        {
            activeQuests.Clear();
            completionCounts.Clear();
            CharacterId = characterId;
            CharacterLevel = Math.Max(1, level);
            Revision = -1;
            ErrorCode = string.Empty;
            ErrorMessage = string.Empty;
            if (Status != QuestClientStatus.UpdateRequired)
            {
                Status = QuestClientStatus.Unavailable;
            }

            Changed?.Invoke();
        }

        public void BeginLoading()
        {
            if (Status == QuestClientStatus.UpdateRequired)
            {
                return;
            }

            Status = QuestClientStatus.Loading;
            Changed?.Invoke();
        }

        public void SetFailed(string code, string message, bool updateRequired = false)
        {
            if (Status == QuestClientStatus.UpdateRequired)
            {
                return;
            }

            Status = updateRequired ? QuestClientStatus.UpdateRequired : QuestClientStatus.Failed;
            ErrorCode = code ?? string.Empty;
            ErrorMessage = message ?? string.Empty;
            Changed?.Invoke();
        }

        // Replaces the log with GameService's when it belongs to this character and to the
        // bundled catalog.
        public bool TryApplyLog(CharacterQuestLogResponse response, out string error)
        {
            error = string.Empty;
            if (Catalog == null)
            {
                error = "The bundled quest catalog is unavailable.";
                return false;
            }

            if (response == null
                || !Guid.TryParse(response.characterId, out var characterId)
                || characterId != CharacterId
                || response.questLogRevision < 0)
            {
                error = "The quest log belongs to another character or is invalid.";
                return false;
            }

            if (!Catalog.MatchesServerRevision(response.questCatalogRevision))
            {
                error = "This client build requires a quest catalog update.";
                SetFailed("quest_catalog_update_required", error, true);
                return false;
            }

            var active = new Dictionary<string, QuestClientActiveQuest>(StringComparer.Ordinal);
            foreach (var quest in response.activeQuests ?? Array.Empty<ActiveQuestResponse>())
            {
                if (quest == null
                    || !Catalog.TryGet(quest.questId, out var definition)
                    || QuestProgress.StepIndexOf(definition, quest.stepId) < 0
                    || active.ContainsKey(quest.questId))
                {
                    error = "The quest log names a quest or step that the bundled catalog does not hold.";
                    return false;
                }

                active.Add(quest.questId, new QuestClientActiveQuest(
                    quest.questId,
                    quest.stepId,
                    (quest.objectives ?? Array.Empty<QuestObjectiveProgressResponse>())
                        .Where(objective => objective != null && !string.IsNullOrEmpty(objective.objectiveId))
                        .GroupBy(objective => objective.objectiveId, StringComparer.Ordinal)
                        .ToDictionary(group => group.Key, group => group.First().progress, StringComparer.Ordinal)));
            }

            activeQuests.Clear();
            foreach (var pair in active)
            {
                activeQuests.Add(pair.Key, pair.Value);
            }

            completionCounts.Clear();
            foreach (var completed in response.completedQuests ?? Array.Empty<CompletedQuestResponse>())
            {
                if (completed != null && !string.IsNullOrEmpty(completed.questId) && completed.completionCount > 0)
                {
                    completionCounts[completed.questId] = completed.completionCount;
                }
            }

            Revision = response.questLogRevision;
            Status = QuestClientStatus.Ready;
            ErrorCode = string.Empty;
            ErrorMessage = string.Empty;
            Changed?.Invoke();
            return true;
        }

        // Applies a committed change that follows the log's revision. A quest without a step is
        // no longer active.
        public QuestLogApplyResult ApplyChange(RealtimeQuestLogChanged change)
        {
            if (change == null || !IsReady)
            {
                return QuestLogApplyResult.Stale;
            }

            if (change.ReloadRequired)
            {
                return QuestLogApplyResult.ReloadRequired;
            }

            if (change.QuestLogRevision <= Revision)
            {
                return QuestLogApplyResult.Stale;
            }

            if (change.PreviousQuestLogRevision != Revision
                || change.Quests.Any(quest => !Catalog.TryGet(quest.QuestId, out var definition)
                    || (!string.IsNullOrEmpty(quest.StepId) && QuestProgress.StepIndexOf(definition, quest.StepId) < 0)))
            {
                return QuestLogApplyResult.ReloadRequired;
            }

            var changes = new List<QuestClientChange>(change.Quests.Length);
            foreach (var quest in change.Quests)
            {
                activeQuests.TryGetValue(quest.QuestId, out var before);
                var completionsBefore = CompletionCountOf(quest.QuestId);
                QuestClientActiveQuest after = null;
                if (string.IsNullOrEmpty(quest.StepId))
                {
                    activeQuests.Remove(quest.QuestId);
                }
                else
                {
                    after = new QuestClientActiveQuest(
                        quest.QuestId,
                        quest.StepId,
                        quest.Objectives.ToDictionary(
                            objective => objective.ObjectiveId,
                            objective => objective.Progress,
                            StringComparer.Ordinal));
                    activeQuests[quest.QuestId] = after;
                }

                if (quest.CompletionCount > 0)
                {
                    completionCounts[quest.QuestId] = quest.CompletionCount;
                }
                else
                {
                    completionCounts.Remove(quest.QuestId);
                }

                changes.Add(new QuestClientChange(
                    quest.QuestId,
                    before,
                    after,
                    completionsBefore,
                    quest.CompletionCount));
            }

            Revision = change.QuestLogRevision;
            Changed?.Invoke();
            QuestsChanged?.Invoke(changes);
            return QuestLogApplyResult.Applied;
        }

        public bool IsActive(string questId)
        {
            return questId != null && activeQuests.ContainsKey(questId);
        }

        public int CompletionCountOf(string questId)
        {
            return questId != null && completionCounts.TryGetValue(questId, out var count) ? count : 0;
        }

        // Whether the character may accept the quest now, by the shared rules.
        public QuestAvailability AvailabilityOf(QuestDefinition quest)
        {
            return QuestRules.Evaluate(
                quest,
                CharacterLevel,
                activeQuests.Count,
                Catalog.MaximumActiveQuests,
                IsActive,
                CompletionCountOf);
        }
    }
}
