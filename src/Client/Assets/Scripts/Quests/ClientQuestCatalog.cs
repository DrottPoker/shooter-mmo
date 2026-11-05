using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mmorpg.WorldData.Quests;
using UnityEngine;

namespace Mmorpg.Client.Quests
{
    // The bundled compiled quest catalog: every quest's texts, steps, objectives, and rewards. It
    // is accepted only when it validates as the compiler wrote it, and the quest log GameService
    // returns must name the same revision.
    public sealed class ClientQuestCatalog
    {
        private readonly Dictionary<string, QuestDefinition> quests;

        private ClientQuestCatalog(QuestCatalogRuntimeDocument runtime)
        {
            Runtime = runtime;
            quests = runtime.Quests.ToDictionary(quest => quest.Id, quest => quest, StringComparer.Ordinal);
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

        public static bool TryLoad(TextAsset asset, out ClientQuestCatalog catalog, out string error)
        {
            catalog = null;
            if (asset == null)
            {
                error = "The bundled quest catalog is missing.";
                return false;
            }

            return TryParse(asset.text, out catalog, out error);
        }

        public static bool TryParse(string json, out ClientQuestCatalog catalog, out string error)
        {
            catalog = null;
            error = string.Empty;
            try
            {
                if (string.IsNullOrWhiteSpace(json))
                {
                    throw new InvalidDataException("The quest catalog JSON is empty.");
                }

                var document = JsonUtility.FromJson<QuestCatalogJsonDocument>(json)
                    ?? throw new InvalidDataException("The quest catalog JSON has no root object.");
                return TryCreate(document.ToRuntime(), out catalog, out error);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
            {
                error = "The quest catalog JSON is malformed: " + exception.Message;
                return false;
            }
        }

        // Accepts a runtime catalog, such as a test catalog, that validates as compiled.
        public static bool TryCreate(QuestCatalogRuntimeDocument runtime, out ClientQuestCatalog catalog, out string error)
        {
            catalog = null;
            error = string.Empty;
            try
            {
                QuestCatalogCompiler.ValidateRuntime(runtime);
            }
            catch (QuestValidationException exception)
            {
                error = "The quest catalog is invalid: " + string.Join(" ", exception.Errors);
                return false;
            }

            catalog = new ClientQuestCatalog(runtime);
            return true;
        }

        public bool TryGet(string questId, out QuestDefinition quest)
        {
            quest = null;
            return !string.IsNullOrEmpty(questId) && quests.TryGetValue(questId, out quest);
        }

        public bool MatchesServerRevision(string serverRevision)
        {
            return !string.IsNullOrWhiteSpace(serverRevision)
                && string.Equals(Revision, serverRevision, StringComparison.Ordinal);
        }
    }
}
