using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mmorpg.Client.Core;
using Mmorpg.WorldData.Quests;
using Mmorpg.WorldData.Text;
using UnityEngine;

namespace Mmorpg.Client.Quests
{
    // The compiled quest catalog the client resolved at start: every quest's steps, objectives,
    // and rewards, with the texts of the client's text catalog. It is accepted only when it
    // validates as the compiler wrote it, and the quest log GameService returns must name the
    // same revision.
    public sealed class ClientQuestCatalog
    {
        private readonly Dictionary<string, QuestDefinition> quests;
        private readonly Dictionary<string, QuestDefinition[]> questsByGiver;

        private readonly TextCatalog texts;

        // The texts that filled the quests, which also hold the client's own quest sentences.
        public TextCatalog Texts
        {
            get { return texts; }
        }

        private ClientQuestCatalog(QuestCatalogRuntimeDocument runtime, TextCatalog texts)
        {
            this.texts = texts;
            Runtime = runtime;
            quests = runtime.Quests.ToDictionary(quest => quest.Id, quest => quest, StringComparer.Ordinal);
            questsByGiver = runtime.Quests
                .Where(quest => !string.IsNullOrEmpty(quest.GiverActorId))
                .GroupBy(quest => quest.GiverActorId, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.OrderBy(quest => quest.Title, StringComparer.Ordinal).ToArray(),
                    StringComparer.Ordinal);
        }

        public QuestCatalogRuntimeDocument Runtime { get; }

        public string Revision
        {
            get { return Runtime.Revision; }
        }

        public int MaximumActiveQuests
        {
            get { return Runtime.MaximumActiveQuests; }
        }

        public IReadOnlyList<QuestDefinition> Quests
        {
            get { return Runtime.Quests; }
        }

        public static bool TryParse(string json, TextCatalog texts, out ClientQuestCatalog catalog, out string error)
        {
            catalog = null;
            return TryReadRuntime(json, out var runtime, out error)
                && TryCreate(runtime, texts, out catalog, out error);
        }

        // The quest catalog of the content set that the client resolves at start.
        public static ClientContentRequest ContentRequest(string bundledJson)
        {
            return new ClientContentRequest(
                ClientContentKinds.QuestCatalog,
                ClientContentKinds.CoreCatalogId,
                null,
                QuestCatalogFormat.Version,
                bundledJson,
                TryValidateContent);
        }

        public static bool TryValidateContent(string json, out ClientContentHeader header, out string error)
        {
            header = null;
            if (!TryReadRuntime(json, out var runtime, out error))
            {
                return false;
            }

            try
            {
                QuestCatalogCompiler.ValidateRuntime(runtime);
            }
            catch (QuestValidationException exception)
            {
                error = "The quest catalog is invalid: " + string.Join(" ", exception.Errors);
                return false;
            }

            header = new ClientContentHeader(runtime.CatalogId, null, runtime.FormatVersion, runtime.Revision);
            return true;
        }

        private static bool TryReadRuntime(string json, out QuestCatalogRuntimeDocument runtime, out string error)
        {
            runtime = null;
            error = string.Empty;
            try
            {
                if (string.IsNullOrWhiteSpace(json))
                {
                    throw new InvalidDataException("The quest catalog JSON is empty.");
                }

                var document = JsonUtility.FromJson<QuestCatalogJsonDocument>(json)
                    ?? throw new InvalidDataException("The quest catalog JSON has no root object.");
                runtime = document.ToRuntime();
                return true;
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
            {
                error = "The quest catalog JSON is malformed: " + exception.Message;
                return false;
            }
        }

        // Accepts a runtime catalog, such as a test catalog, that validates as compiled. The
        // catalog is the client's own copy: once it is accepted, every text in it is replaced by
        // the text catalog's, so the quest windows show nothing but what the text catalog holds.
        public static bool TryCreate(
            QuestCatalogRuntimeDocument runtime,
            TextCatalog texts,
            out ClientQuestCatalog catalog,
            out string error)
        {
            catalog = null;
            error = string.Empty;
            if (texts == null)
            {
                error = "The quest catalog needs the text catalog.";
                return false;
            }

            try
            {
                QuestCatalogCompiler.ValidateRuntime(runtime);
            }
            catch (QuestValidationException exception)
            {
                error = "The quest catalog is invalid: " + string.Join(" ", exception.Errors);
                return false;
            }

            ApplyTexts(runtime, texts);
            catalog = new ClientQuestCatalog(runtime, texts);
            return true;
        }

        private static void ApplyTexts(QuestCatalogRuntimeDocument runtime, TextCatalog texts)
        {
            foreach (var quest in runtime.Quests)
            {
                quest.Title = texts.Get(TextKeys.QuestTitle(quest.Id));
                quest.OfferText = texts.Get(TextKeys.QuestOffer(quest.Id));
                quest.ProgressText = texts.Get(TextKeys.QuestProgress(quest.Id));
                quest.CompletionText = texts.Get(TextKeys.QuestCompletion(quest.Id));
                foreach (var step in quest.Steps)
                {
                    foreach (var objective in step.Objectives)
                    {
                        objective.Text = texts.Get(TextKeys.QuestObjectiveText(quest.Id, step.Id, objective.Id));

                        // Only a talk-to objective has a line.
                        objective.Line = texts.GetOrEmpty(TextKeys.QuestObjectiveLine(quest.Id, step.Id, objective.Id));
                    }
                }
            }
        }

        // The name of an actor that a quest message names by its definition, such as the Mob
        // whose corpse holds a quest drop.
        public string ActorName(string actorDefinitionId)
        {
            return TextKeys.IsValidId(actorDefinitionId)
                ? texts.Get(TextKeys.ActorName(actorDefinitionId))
                : actorDefinitionId ?? string.Empty;
        }

        public bool TryGet(string questId, out QuestDefinition quest)
        {
            quest = null;
            return !string.IsNullOrEmpty(questId) && quests.TryGetValue(questId, out quest);
        }

        // The quests an NPC gives, by title, so the dialog and the marker of one NPC never walk
        // the whole catalog.
        public IReadOnlyList<QuestDefinition> QuestsGivenBy(string npcActorId)
        {
            return !string.IsNullOrEmpty(npcActorId) && questsByGiver.TryGetValue(npcActorId, out var given)
                ? given
                : Array.Empty<QuestDefinition>();
        }

        public bool MatchesServerRevision(string serverRevision)
        {
            return !string.IsNullOrWhiteSpace(serverRevision)
                && string.Equals(Revision, serverRevision, StringComparison.Ordinal);
        }
    }
}
