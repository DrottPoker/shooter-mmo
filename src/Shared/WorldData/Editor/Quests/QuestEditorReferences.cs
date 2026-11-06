using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mmorpg.WorldData.Actors;
using Mmorpg.WorldData.Content;
using Mmorpg.WorldData.Items;
using UnityEngine;

namespace Mmorpg.WorldData.Editor.Quests
{
    // An actor that quest content may name, with what the dropdowns need to know about it.
    public sealed class QuestEditorActorChoice
    {
        public QuestEditorActorChoice(string id, string displayName, string kind, IReadOnlyList<string> tags, bool hasQuests, bool placed)
        {
            Id = id;
            DisplayName = displayName ?? string.Empty;
            Kind = kind ?? string.Empty;
            Tags = tags ?? Array.Empty<string>();
            HasQuests = hasQuests;
            Placed = placed;
        }

        public string Id { get; }
        public string DisplayName { get; }
        public string Kind { get; }
        public IReadOnlyList<string> Tags { get; }

        // An NPC with the quests capability, which gives, receives, or talks about quests.
        public bool HasQuests { get; }

        // Some World places it, as every actor a quest names must be.
        public bool Placed { get; }

        public bool IsQuestNpc => HasQuests && Placed && Kind == WorldActorKindIds.Npc;
        public bool IsMob => Kind == WorldActorKindIds.Mob;
        public bool IsResourceNode => Kind == WorldActorKindIds.ResourceNode;
    }

    public sealed class QuestEditorItemChoice
    {
        public QuestEditorItemChoice(string id, string displayName, string category, bool protectedQuestItem, bool rewardable)
        {
            Id = id;
            DisplayName = displayName ?? string.Empty;
            Category = category ?? string.Empty;
            ProtectedQuestItem = protectedQuestItem;
            Rewardable = rewardable;
        }

        public string Id { get; }
        public string DisplayName { get; }
        public string Category { get; }

        // A protected quest item, which start items and quest drops must be.
        public bool ProtectedQuestItem { get; }

        // An ordinary item without default policies, which rewards must be.
        public bool Rewardable { get; }
    }

    // What quest content may name, read from the compiled item catalog and every World's
    // compiled actors, which the item and actor compilers write before the quest compiler runs.
    // The same documents feed the shared quest compiler for live validation.
    public sealed class QuestEditorReferences
    {
        public QuestEditorReferences(ItemCatalogRuntimeDocument items, IReadOnlyList<WorldActorRuntimeDocument> worlds)
        {
            Items = items ?? throw new ArgumentNullException(nameof(items));
            Worlds = worlds ?? throw new ArgumentNullException(nameof(worlds));
            var placed = new HashSet<string>(
                worlds.SelectMany(world => world.SpawnInstances ?? Array.Empty<WorldActorSpawnInstanceDefinition>())
                    .Select(spawn => spawn.ActorId),
                StringComparer.Ordinal);
            Actors = worlds.SelectMany(world => world.Actors ?? Array.Empty<WorldActorDefinition>())
                .GroupBy(actor => actor.Id, StringComparer.Ordinal)
                .Select(group => group.First())
                .Select(actor => new QuestEditorActorChoice(
                    actor.Id,
                    actor.DisplayName,
                    actor.Kind,
                    actor.Tags,
                    (actor.Capabilities ?? Array.Empty<WorldActorCapabilityDefinition>())
                        .Any(capability => capability.Kind == WorldActorCapabilityKindIds.Quests),
                    placed.Contains(actor.Id)))
                .OrderBy(actor => actor.Id, StringComparer.Ordinal)
                .ToArray();
            ItemChoices = (items.Definitions ?? Array.Empty<ItemDefinition>())
                .Select(item => new QuestEditorItemChoice(
                    item.Id,
                    item.DisplayName,
                    item.Category,
                    item.Category == ItemCategoryIds.QuestItem
                        && (item.DefaultPolicies ?? Array.Empty<string>()).Contains(ItemPolicyIds.ProtectedOnDeath),
                    item.Category != ItemCategoryIds.QuestItem && (item.DefaultPolicies ?? Array.Empty<string>()).Length == 0))
                .OrderBy(item => item.Id, StringComparer.Ordinal)
                .ToArray();
            ActorTags = Mobs
                .SelectMany(actor => actor.Tags)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(tag => tag, StringComparer.Ordinal)
                .ToArray();
        }

        public ItemCatalogRuntimeDocument Items { get; }
        public IReadOnlyList<WorldActorRuntimeDocument> Worlds { get; }
        public IReadOnlyList<QuestEditorActorChoice> Actors { get; }
        public IReadOnlyList<QuestEditorItemChoice> ItemChoices { get; }

        // The tags of the placed Mobs, which a kill objective may name instead of actors.
        public IReadOnlyList<string> ActorTags { get; }

        // What quest content may name: placed quest NPCs, Mobs, and resource nodes.
        public IEnumerable<QuestEditorActorChoice> QuestNpcs => Actors.Where(actor => actor.IsQuestNpc);
        public IEnumerable<QuestEditorActorChoice> Mobs => Actors.Where(actor => actor.IsMob && actor.Placed);
        public IEnumerable<QuestEditorActorChoice> ResourceNodes => Actors.Where(actor => actor.IsResourceNode && actor.Placed);

        public static QuestEditorReferences Load(string contentRoot, string generatedRoot)
        {
            var itemCatalogPath = ContentLayout.GeneratedItemCatalogPath(generatedRoot);
            if (!File.Exists(itemCatalogPath))
            {
                throw new FileNotFoundException("The compiled item catalog is missing; run the item compiler first.", itemCatalogPath);
            }

            var worlds = ContentLayout.ListWorldIds(ContentLayout.WorldsDirectory(contentRoot))
                .Select(worldId => LoadWorld(ContentLayout.GeneratedWorldActorsPath(generatedRoot, worldId)))
                .ToArray();
            return new QuestEditorReferences(LoadItems(itemCatalogPath), worlds);
        }

        public string ActorLabel(string actorId)
        {
            var actor = Actors.FirstOrDefault(candidate => candidate.Id == actorId);
            return actor == null ? actorId + " (unknown)" : actor.DisplayName + " (" + actor.Id + ")";
        }

        public string ItemLabel(string itemId)
        {
            var item = ItemChoices.FirstOrDefault(candidate => candidate.Id == itemId);
            return item == null ? itemId + " (unknown)" : item.DisplayName + " (" + item.Id + ")";
        }

        // The compiled catalog read like the client reads it and recompiled from its gameplay
        // content, as the quest compiler does.
        private static ItemCatalogRuntimeDocument LoadItems(string path)
        {
            var document = JsonUtility.FromJson<ItemCatalogJsonDocument>(File.ReadAllText(path))
                ?? throw new InvalidDataException("The compiled item catalog has no root object.");
            foreach (var definition in document.definitions ?? Array.Empty<ItemCatalogJsonDefinition>())
            {
                definition.ClearMissingPlaceholders();
            }

            return ItemCatalogCompiler.Compile(document.ToAuthoringDocument());
        }

        // Only the fields that quest content refers to: each actor's kind, tags, and capability
        // kinds, and which actors the World places.
        private static WorldActorRuntimeDocument LoadWorld(string path)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("A compiled World is missing; run the actor compiler first.", path);
            }

            var world = JsonUtility.FromJson<WorldJson>(File.ReadAllText(path))
                ?? throw new InvalidDataException("The compiled World at '" + path + "' has no root object.");
            return new WorldActorRuntimeDocument
            {
                Actors = (world.actors ?? Array.Empty<ActorJson>())
                    .Select(actor => new WorldActorDefinition
                    {
                        Id = actor.id,
                        DisplayName = actor.displayName,
                        Kind = actor.kind,
                        Tags = actor.tags ?? Array.Empty<string>(),
                        Capabilities = (actor.capabilities ?? Array.Empty<CapabilityJson>())
                            .Select(capability => new WorldActorCapabilityDefinition { Kind = capability.kind })
                            .ToArray()
                    })
                    .ToArray(),
                SpawnInstances = (world.spawnInstances ?? Array.Empty<SpawnJson>())
                    .Select(spawn => new WorldActorSpawnInstanceDefinition { ActorId = spawn.actorId })
                    .ToArray()
            };
        }

        [Serializable]
        private sealed class WorldJson
        {
            public ActorJson[] actors;
            public SpawnJson[] spawnInstances;
        }

        [Serializable]
        private sealed class ActorJson
        {
            public string id;
            public string displayName;
            public string kind;
            public string[] tags;
            public CapabilityJson[] capabilities;
        }

        [Serializable]
        private sealed class CapabilityJson
        {
            public string kind;
        }

        [Serializable]
        private sealed class SpawnJson
        {
            public string actorId;
        }
    }
}
