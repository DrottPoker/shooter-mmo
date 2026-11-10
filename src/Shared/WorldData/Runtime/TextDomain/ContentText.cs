using System;
using System.Collections.Generic;
using Mmorpg.WorldData.Actors;
using Mmorpg.WorldData.Items;
using Mmorpg.WorldData.Quests;

namespace Mmorpg.WorldData.Text
{
    // Collects the player-facing text of compiled content into the text table of its domain,
    // in the source locale. A text that content leaves empty has no entry.
    public static class ContentText
    {
        // The names of the authored item catalog. The compiled catalog carries none.
        public static TextTableDocument FromItemCatalog(ItemCatalogAuthoringDocument catalog)
        {
            if (catalog == null)
            {
                throw new ArgumentNullException(nameof(catalog));
            }

            var entries = new List<TextTableEntry>();
            foreach (var definition in catalog.Definitions ?? Array.Empty<ItemDefinitionAuthoringEntry>())
            {
                Add(entries, TextKeys.ItemName(definition.Id), definition.DisplayName);
            }

            foreach (var category in catalog.Categories ?? Array.Empty<ItemCatalogIdentityEntry>())
            {
                Add(entries, TextKeys.ItemCategoryName(category.Id), category.DisplayName);
            }

            foreach (var tag in catalog.Tags ?? Array.Empty<ItemCatalogIdentityEntry>())
            {
                Add(entries, TextKeys.ItemTagName(tag.Id), tag.DisplayName);
            }

            foreach (var toolKind in catalog.ToolKinds ?? Array.Empty<ItemCatalogIdentityEntry>())
            {
                Add(entries, TextKeys.ToolKindName(toolKind.Id), toolKind.DisplayName);
            }

            foreach (var slot in catalog.EquipmentSlots ?? Array.Empty<ItemEquipmentSlotEntry>())
            {
                Add(entries, TextKeys.EquipmentSlotName(slot.Id), slot.DisplayName);
            }

            foreach (var family in catalog.WeaponFamilies ?? Array.Empty<ItemWeaponFamilyEntry>())
            {
                Add(entries, TextKeys.WeaponFamilyName(family.Id), family.DisplayName);
            }

            foreach (var family in catalog.AmmunitionFamilies ?? Array.Empty<ItemCatalogIdentityEntry>())
            {
                Add(entries, TextKeys.AmmunitionFamilyName(family.Id), family.DisplayName);
            }

            foreach (var vendor in catalog.Vendors ?? Array.Empty<VendorProfileAuthoringEntry>())
            {
                Add(entries, TextKeys.VendorName(vendor.Id), vendor.DisplayName);
            }

            if (catalog.Economy != null)
            {
                Add(entries, TextKeys.CurrencyName(), catalog.Economy.CurrencyDisplayName);
            }

            return TextTable.Create(TextTableIds.Items, TextTableFormat.SourceLocale, entries);
        }

        // The texts of the authored actor catalog. The compiled actors of a World carry none.
        public static TextTableDocument FromWorldActors(WorldActorCatalogAuthoringDocument actors)
        {
            if (actors == null)
            {
                throw new ArgumentNullException(nameof(actors));
            }

            var entries = new List<TextTableEntry>();
            foreach (var actor in actors.Actors ?? Array.Empty<WorldActorAuthoringEntry>())
            {
                Add(entries, TextKeys.ActorName(actor.Id), actor.DisplayName);
                Add(entries, TextKeys.ActorExamine(actor.Id), actor.ExamineText);
                foreach (var capability in actor.Capabilities ?? Array.Empty<WorldActorCapabilityAuthoringEntry>())
                {
                    Add(
                        entries,
                        TextKeys.ActorCapabilityName(actor.Id, capability.Id),
                        capability.DisplayName);
                    Add(
                        entries,
                        TextKeys.ActorCapabilityMenuOption(actor.Id, capability.Id),
                        capability.MenuOption);
                }
            }

            foreach (var faction in actors.Factions ?? Array.Empty<WorldActorFactionAuthoringEntry>())
            {
                Add(entries, TextKeys.FactionName(faction.Id), faction.DisplayName);
            }

            return TextTable.Create(TextTableIds.Actors, TextTableFormat.SourceLocale, entries);
        }

        public static TextTableDocument FromQuestCatalog(QuestCatalogRuntimeDocument catalog)
        {
            if (catalog == null)
            {
                throw new ArgumentNullException(nameof(catalog));
            }

            var entries = new List<TextTableEntry>();
            foreach (var quest in catalog.Quests ?? Array.Empty<QuestDefinition>())
            {
                Add(entries, TextKeys.QuestTitle(quest.Id), quest.Title);
                Add(entries, TextKeys.QuestOffer(quest.Id), quest.OfferText);
                Add(entries, TextKeys.QuestProgress(quest.Id), quest.ProgressText);
                Add(entries, TextKeys.QuestCompletion(quest.Id), quest.CompletionText);
                foreach (var step in quest.Steps ?? Array.Empty<QuestStepDefinition>())
                {
                    foreach (var objective in step.Objectives ?? Array.Empty<QuestObjectiveDefinition>())
                    {
                        Add(
                            entries,
                            TextKeys.QuestObjectiveText(quest.Id, step.Id, objective.Id),
                            objective.Text);
                        Add(
                            entries,
                            TextKeys.QuestObjectiveLine(quest.Id, step.Id, objective.Id),
                            objective.Line);
                    }
                }
            }

            return TextTable.Create(TextTableIds.Quests, TextTableFormat.SourceLocale, entries);
        }

        private static void Add(List<TextTableEntry> entries, string key, string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            entries.Add(new TextTableEntry { Key = key, Text = text });
        }
    }
}
