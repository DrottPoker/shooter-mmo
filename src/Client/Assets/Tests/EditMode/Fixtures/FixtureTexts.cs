using System.Collections.Generic;
using System.Linq;
using Mmorpg.Client.Core;
using Mmorpg.Client.WorldActors;
using Mmorpg.WorldData.Items;
using Mmorpg.WorldData.Quests;
using Mmorpg.WorldData.Text;
using NUnit.Framework;

namespace Mmorpg.Client.Tests.EditMode
{
    // Text catalogs for tests: the bundled one that the client configuration lists, and ones
    // built from a test catalog's own texts, as the content compilers build the real tables.
    internal static class FixtureTexts
    {
        public static TextCatalog Bundled(ClientConfig config)
        {
            Assert.That(ClientTexts.TryLoad(config.TextTables, out var texts, out var error), Is.True, error);
            return texts;
        }

        public static TextCatalog For(ItemCatalogRuntimeDocument items)
        {
            return TextCatalog.Create(new[] { ContentText.FromItemCatalog(items) });
        }

        // The quest texts with the development actors, which name the Mob of a quest drop.
        public static TextCatalog For(QuestCatalogRuntimeDocument quests)
        {
            return TextCatalog.Create(new[]
            {
                ContentText.FromQuestCatalog(quests),
                DevelopmentActorTexts().BuildTable()
            });
        }

        // The actors that the client tests spawn, named as the tests expect: Mira of the city
        // services, the Feral Wolf, and the Iron Vein.
        public static TextCatalog DevelopmentActors()
        {
            return DevelopmentActorTexts().Build();
        }

        private static ActorTexts DevelopmentActorTexts()
        {
            return new ActorTexts()
                .Actor("npc.city_services", "Mira", "She keeps the city's stores.")
                .Capability("npc.city_services", "services.dialogue", "Talk", "Talk-to")
                .Capability("npc.city_services", "services.vendor", "Trade", "Trade")
                .Capability("npc.city_services", "services.bank", "Character Bank", "Bank")
                .Actor("mob.feral_wolf", "Feral Wolf", "A lean, hungry wolf.")
                .Actor("resource.iron_vein", "Iron Vein", "A seam of iron ore runs through the rock.")
                .Capability("resource.iron_vein", "resource.mine", "Mine", "Mine");
        }

        // An actor state that names its actors from DevelopmentActors.
        public static WorldActorClientState ActorState()
        {
            var state = new WorldActorClientState();
            state.UseTexts(DevelopmentActors());
            return state;
        }

        // Builds the actor text table of a test, as the actor compiler builds the real one.
        public sealed class ActorTexts
        {
            private readonly List<(string Key, string Text)> entries = new List<(string Key, string Text)>();

            public ActorTexts Actor(string actorId, string name, string examine)
            {
                entries.Add((TextKeys.ActorName(actorId), name));
                entries.Add((TextKeys.ActorExamine(actorId), examine));
                return this;
            }

            public ActorTexts Capability(string actorId, string capabilityId, string name, string menuOption)
            {
                entries.Add((TextKeys.ActorCapabilityName(actorId, capabilityId), name));
                entries.Add((TextKeys.ActorCapabilityMenuOption(actorId, capabilityId), menuOption));
                return this;
            }

            public TextCatalog Build()
            {
                return TextCatalog.Create(new[] { BuildTable() });
            }

            public TextTableDocument BuildTable()
            {
                return TextTable.Create(
                    TextTableIds.Actors,
                    TextTableFormat.SourceLocale,
                    entries.Select(entry => new TextTableEntry { Key = entry.Key, Text = entry.Text }));
            }
        }

        // A catalog of one table whose texts the test chooses, to prove where a text comes from.
        public static TextCatalog Of(string tableId, params (string Key, string Text)[] entries)
        {
            return TextCatalog.Create(new[]
            {
                TextTable.Create(
                    tableId,
                    TextTableFormat.SourceLocale,
                    entries.Select(entry => new TextTableEntry { Key = entry.Key, Text = entry.Text }))
            });
        }
    }
}
