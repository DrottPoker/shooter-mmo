using System;
using System.Collections.Generic;
using System.Linq;
using Mmorpg.Client.Gameplay;
using Mmorpg.Client.Items;
using Mmorpg.Client.Ui;
using Mmorpg.Client.WorldActors;
using Mmorpg.GameProtocol;
using NUnit.Framework;
using UnityEngine;

namespace Mmorpg.Client.Tests.EditMode
{
    // The RuneScape-style world menu: option order, the options of NPCs, resource nodes, and
    // corpses, Walk here and Examine, capability activation, game messages, and presentation.
    public sealed class WorldMenuTests
    {
        private const ulong NpcId = 31;
        private const ulong NodeId = 32;
        private const ulong MobId = 33;

        private readonly List<GameObject> objects = new List<GameObject>();

        [TearDown]
        public void DestroyObjects()
        {
            foreach (var value in objects)
            {
                UnityEngine.Object.DestroyImmediate(value);
            }

            objects.Clear();
        }

        [Test]
        public void OptionsSortTargetsNearestFirstThenWalkThenExamine()
        {
            var first = new FakeProvider();
            var second = new FakeProvider();
            var near = new WorldPointerCandidate(new FakeTarget("Near"), 1f);
            var far = new WorldPointerCandidate(new FakeTarget("Far"), 2f);
            var menu = new WorldMenuBuilder();
            menu.Begin(default(Ray), new[] { near, far });

            menu.Add(WorldMenuOption.ForExamine(first, 9, 0, near, "Near", WorldMenuTargetStyle.Npc, "Near text."));
            menu.Add(WorldMenuOption.ForWalk(first, 8, default(Ray)));
            menu.Add(WorldMenuOption.ForTarget(first, 1, 1, far, "Attack", "Far", WorldMenuTargetStyle.Mob));
            menu.Add(WorldMenuOption.ForTarget(second, 2, 0, near, "Trade", "Near", WorldMenuTargetStyle.Npc));
            menu.Add(WorldMenuOption.ForTarget(second, 3, 0, near, "Bank", "Near", WorldMenuTargetStyle.Npc));
            menu.Add(WorldMenuOption.ForExamine(first, 9, 1, far, "Far", WorldMenuTargetStyle.Mob, "Far text."));
            menu.Finish();

            Assert.That(
                menu.Options.Select(option => option.Label),
                Is.EqualTo(new[]
                {
                    "Trade Near",
                    "Bank Near",
                    "Attack Far",
                    "Walk here",
                    "Examine Near",
                    "Examine Far"
                }));
            Assert.That(menu.Default.Label, Is.EqualTo("Trade Near"));
            Assert.That(menu.Options[3].IsPassive, Is.False);
            Assert.That(menu.Options[4].IsPassive, Is.True);
        }

        [Test]
        public void BlockedMenuHasNoOptionsAtAll()
        {
            var provider = new FakeProvider();
            var menu = new WorldMenuBuilder();
            menu.Begin(default(Ray), null);
            menu.Add(WorldMenuOption.ForWalk(provider, 1, default(Ray)));

            menu.Block();
            menu.Add(WorldMenuOption.ForWalk(provider, 1, default(Ray)));
            menu.Finish();

            Assert.That(menu.IsBlocked, Is.True);
            Assert.That(menu.Options, Is.Empty);
            Assert.That(menu.Default.IsEmpty, Is.True);

            menu.Begin(default(Ray), null);
            Assert.That(menu.IsBlocked, Is.False);
        }

        [Test]
        public void NpcsAndResourceNodesOfferTheirCapabilitiesInOrderAndCorpsesOfferLoot()
        {
            var actors = Create<WorldActorClientController>("Actors");
            Apply(actors.State, Spawn(NpcId, RealtimeWorldActorKind.Npc, "Mira", MiraOptions()));
            Apply(actors.State, Spawn(NodeId, RealtimeWorldActorKind.ResourceNode, "Iron Vein", new[]
            {
                new RealtimeWorldActorMenuOption("resource.mine", RealtimeWorldActorCapabilityKind.Gathering, "Mine")
            }));
            Apply(actors.State, Spawn(MobId, RealtimeWorldActorKind.Mob, "Feral Wolf", Array.Empty<RealtimeWorldActorMenuOption>()));
            var targeting = Create<WorldInteractionTargetingController>("Targeting");
            targeting.Initialize(null, actors, null, null);
            var menu = new WorldMenuBuilder();

            menu.Begin(default(Ray), new[]
            {
                new WorldPointerCandidate(View<NpcView>(actors.State, NpcId), 1f),
                new WorldPointerCandidate(View<MobView>(actors.State, MobId), 2f),
                new WorldPointerCandidate(View<ResourceNodeView>(actors.State, NodeId), 3f),
                new WorldPointerCandidate(Corpse("Feral Wolf"), 4f)
            });
            targeting.AddWorldMenuOptions(menu);
            menu.Finish();

            Assert.That(
                menu.Options.Select(option => option.Label),
                Is.EqualTo(new[]
                {
                    "Talk-to Mira",
                    "Trade Mira",
                    "Bank Mira",
                    "Mine Iron Vein",
                    "Loot Corpse of Feral Wolf"
                }));
            Assert.That(menu.Options[0].Style, Is.EqualTo(WorldMenuTargetStyle.Npc));
            Assert.That(menu.Options[1].Argument, Is.EqualTo("services.vendor"));
            Assert.That(menu.Options[3].Style, Is.EqualTo(WorldMenuTargetStyle.Object));
            Assert.That(menu.Options[3].TargetIndex, Is.EqualTo(2));
            Assert.That(menu.Options[4].Style, Is.EqualTo(WorldMenuTargetStyle.Corpse));
        }

        [Test]
        public void WalkHereIsAlwaysOfferedAndExamineShowsTheContentText()
        {
            var actors = Create<WorldActorClientController>("Actors");
            Apply(actors.State, Spawn(NpcId, RealtimeWorldActorKind.Npc, "Mira", MiraOptions()));
            Apply(actors.State, Spawn(MobId, RealtimeWorldActorKind.Mob, "Feral Wolf", Array.Empty<RealtimeWorldActorMenuOption>()));
            var messages = new GameMessageLog();
            var worldMenu = Create<WorldMenuController>("WorldMenu");
            worldMenu.Initialize(actors, messages);

            // Nothing under the pointer leaves Walk here alone.
            var empty = new WorldMenuBuilder();
            worldMenu.BuildMenu(new Ray(new Vector3(0f, 1000f, 0f), Vector3.up), empty);
            Assert.That(empty.Options.Select(option => option.Label), Is.EqualTo(new[] { "Walk here" }));

            var menu = new WorldMenuBuilder();
            menu.Begin(default(Ray), new[]
            {
                new WorldPointerCandidate(View<MobView>(actors.State, MobId), 1f),
                new WorldPointerCandidate(View<NpcView>(actors.State, NpcId), 2f),
                new WorldPointerCandidate(Corpse("Ada"), 3f)
            });
            worldMenu.AddWorldMenuOptions(menu);
            menu.Finish();

            Assert.That(
                menu.Options.Select(option => option.Label),
                Is.EqualTo(new[]
                {
                    "Walk here",
                    "Examine Feral Wolf",
                    "Examine Mira",
                    "Examine Corpse of Ada"
                }));
            Assert.That(menu.Options[1].Style, Is.EqualTo(WorldMenuTargetStyle.Mob));

            worldMenu.ExecuteWorldMenuOption(menu.Options[1]);
            worldMenu.ExecuteWorldMenuOption(menu.Options[3]);
            Assert.That(
                messages.Messages.Select(message => message.Text),
                Is.EqualTo(new[] { "Feral Wolf examine text.", "The remains of Ada." }));
        }

        // A route ends within its range plus the arrival tolerance, so the walk must aim
        // inside the client's open check, which sits inside the worker's start range.
        [Test]
        public void InteractionWalkStopsSafelyInsideTheOpenCheck()
        {
            Assert.That(
                WorldInteractionTargetingController.ApproachRange
                    + (2f * Mmorpg.GameSimulation.PlayerMovementSimulation.WaypointArrivalDistance),
                Is.LessThan(WorldInteractionTargetingController.OpenRange));
            Assert.That(
                WorldInteractionTargetingController.OpenRange,
                Is.LessThan(Mmorpg.WorldData.Actors.WorldInteractionRules.AuthoritativeStartRange));
        }

        [Test]
        public void ChosenCapabilitiesActivateAsTheirPanelButtonsWould()
        {
            Assert.That(
                WorldInteractionClientController.ActivationFor(RealtimeWorldActorCapabilityKind.Gathering),
                Is.EqualTo(WorldCapabilityActivation.StartGathering));
            foreach (var kind in new[]
            {
                RealtimeWorldActorCapabilityKind.Dialogue,
                RealtimeWorldActorCapabilityKind.Insurance,
                RealtimeWorldActorCapabilityKind.QuestOffer
            })
            {
                Assert.That(
                    WorldInteractionClientController.ActivationFor(kind),
                    Is.EqualTo(WorldCapabilityActivation.OpenOnly),
                    kind.ToString());
            }

            foreach (var kind in new[]
            {
                RealtimeWorldActorCapabilityKind.Vendor,
                RealtimeWorldActorCapabilityKind.Bank,
                RealtimeWorldActorCapabilityKind.RecoveryStorage,
                RealtimeWorldActorCapabilityKind.QuestTurnIn,
                RealtimeWorldActorCapabilityKind.Crafting,
                RealtimeWorldActorCapabilityKind.Trainer
            })
            {
                Assert.That(
                    WorldInteractionClientController.ActivationFor(kind),
                    Is.EqualTo(WorldCapabilityActivation.Execute),
                    kind.ToString());
            }
        }

        [Test]
        public void GameMessagesKeepTheNewestAndIgnoreBlankText()
        {
            var messages = new GameMessageLog();
            var changes = 0;
            messages.Changed += () => changes++;

            messages.Add(" ", 1f);
            Assert.That(messages.Messages, Is.Empty);
            Assert.That(changes, Is.Zero);

            for (var index = 0; index < GameMessageLog.Capacity + 2; index++)
            {
                messages.Add("Message " + index + " ", index);
            }

            Assert.That(messages.Messages.Count, Is.EqualTo(GameMessageLog.Capacity));
            Assert.That(messages.Messages[0].Text, Is.EqualTo("Message 2"));
            Assert.That(messages.Messages[GameMessageLog.Capacity - 1].Time, Is.EqualTo(GameMessageLog.Capacity + 1));
            Assert.That(changes, Is.EqualTo(GameMessageLog.Capacity + 2));

            messages.Clear();
            Assert.That(messages.Messages, Is.Empty);
        }

        [Test]
        public void PresentationColorsTargetNamesAndFadesOldMessages()
        {
            var provider = new FakeProvider();
            var npc = WorldMenuOption.ForTarget(provider, 1, 0, default(WorldPointerCandidate), "Talk-to", "Mira", WorldMenuTargetStyle.Npc);
            var node = WorldMenuOption.ForTarget(provider, 1, 0, default(WorldPointerCandidate), "Mine", "Iron Vein", WorldMenuTargetStyle.Object);

            Assert.That(TemporaryWorldContextMenu.RichLabel(npc), Is.EqualTo("Talk-to <color=#FFFF00>Mira</color>"));
            Assert.That(TemporaryWorldContextMenu.RichLabel(node), Is.EqualTo("Mine <color=#00FFFF>Iron Vein</color>"));
            Assert.That(TemporaryWorldContextMenu.RichLabel(WorldMenuOption.ForWalk(provider, 1, default(Ray))), Is.EqualTo("Walk here"));
            Assert.That(
                TemporaryWorldContextMenu.NameColor(WorldMenuTargetStyle.Mob),
                Is.EqualTo(TemporaryWorldContextMenu.NpcNameColor));
            Assert.That(
                TemporaryWorldContextMenu.NameColor(WorldMenuTargetStyle.Corpse),
                Is.EqualTo(TemporaryWorldContextMenu.CorpseNameColor));

            // The hint names the left-click option and counts the others; bare ground has none.
            var walk = WorldMenuOption.ForWalk(provider, 1, default(Ray));
            var examine = WorldMenuOption.ForExamine(provider, 2, 0, default(WorldPointerCandidate), "Mira", WorldMenuTargetStyle.Npc, "Text.");
            Assert.That(TemporaryWorldContextMenu.HoverLabel(new[] { walk }), Is.Empty);
            Assert.That(TemporaryWorldContextMenu.HoverLabel(Array.Empty<WorldMenuOption>()), Is.Empty);
            Assert.That(TemporaryWorldContextMenu.HoverLabel(null), Is.Empty);
            Assert.That(
                TemporaryWorldContextMenu.HoverLabel(new[] { npc, walk }),
                Is.EqualTo(
                    "Talk-to <color=#FFFF00>Mira</color> <color="
                    + TemporaryUiKit.ColorTag(TemporaryUiKit.TextSecondary)
                    + ">/ 1 more option</color>"));
            Assert.That(
                TemporaryWorldContextMenu.HoverLabel(new[] { walk, examine, examine }),
                Does.StartWith("Walk here <color=").And.EndWith(">/ 2 more options</color>"));

            Assert.That(TemporaryGameMessagePanel.Opacity(0f), Is.EqualTo(1f));
            Assert.That(TemporaryGameMessagePanel.Opacity(TemporaryGameMessagePanel.VisibleSeconds), Is.EqualTo(1f));
            Assert.That(
                TemporaryGameMessagePanel.Opacity(TemporaryGameMessagePanel.VisibleSeconds + (TemporaryGameMessagePanel.FadeSeconds * 0.5f)),
                Is.EqualTo(0.5f).Within(0.0001f));
            Assert.That(
                TemporaryGameMessagePanel.Opacity(TemporaryGameMessagePanel.VisibleSeconds + TemporaryGameMessagePanel.FadeSeconds),
                Is.Zero);
        }

        private T Create<T>(string name) where T : Component
        {
            var value = new GameObject(name);
            objects.Add(value);
            return value.AddComponent<T>();
        }

        private T View<T>(WorldActorClientState actors, ulong entityId) where T : WorldActorView
        {
            Assert.That(actors.TryGet(entityId, out var actor), Is.True);
            var view = Create<T>(typeof(T).Name);
            view.Initialize(actor);
            return view;
        }

        private CorpseInteractionTarget Corpse(string sourceName)
        {
            var corpse = Create<CorpseInteractionTarget>("Corpse");
            corpse.Apply(new CorpsePresenceEntry(new RealtimeCorpsePresence(
                Guid.NewGuid(),
                1f,
                0f,
                2f,
                2_000_000_000_000L,
                false,
                sourceName,
                "corpse.remains")));
            return corpse;
        }

        private static void Apply(WorldActorClientState actors, RealtimeWorldActorSpawn spawn)
        {
            Assert.That(actors.TryApplySpawn(spawn, out var error), Is.True, error);
        }

        private static RealtimeWorldActorMenuOption[] MiraOptions()
        {
            return new[]
            {
                new RealtimeWorldActorMenuOption("services.dialogue", RealtimeWorldActorCapabilityKind.Dialogue, "Talk-to"),
                new RealtimeWorldActorMenuOption("services.vendor", RealtimeWorldActorCapabilityKind.Vendor, "Trade"),
                new RealtimeWorldActorMenuOption("services.bank", RealtimeWorldActorCapabilityKind.Bank, "Bank")
            };
        }

        private static RealtimeWorldActorSpawn Spawn(
            ulong entityId,
            RealtimeWorldActorKind kind,
            string displayName,
            RealtimeWorldActorMenuOption[] menuOptions)
        {
            return new RealtimeWorldActorSpawn(
                entityId,
                Guid.NewGuid(),
                "actor." + entityId,
                "spawn." + entityId,
                displayName,
                kind,
                "faction",
                RealtimeWorldActorDisposition.Neutral,
                "presentation." + entityId,
                1,
                1,
                true,
                entityId,
                0f,
                0f,
                0f,
                RealtimeWorldActorActivityTier.EventDriven,
                0f,
                1f,
                0f,
                1f,
                2f,
                1f,
                displayName + " examine text.",
                menuOptions);
        }

        private sealed class FakeProvider : IWorldMenuProvider
        {
            public void AddWorldMenuOptions(WorldMenuBuilder menu)
            {
            }

            public void ExecuteWorldMenuOption(WorldMenuOption option)
            {
            }
        }

        private sealed class FakeTarget : IWorldInteractionTarget
        {
            public FakeTarget(string name)
            {
                TargetDisplayName = name;
                TargetRuntimeId = Guid.NewGuid();
            }

            public RealtimeWorldInteractionTargetKind TargetKind =>
                RealtimeWorldInteractionTargetKind.WorldActor;
            public ulong TargetEntityId => 0;
            public Guid TargetRuntimeId { get; }
            public long TargetRevision => 1;
            public bool IsTargetActive => true;
            public string TargetDisplayName { get; }
            public Vector3 TargetPosition => Vector3.zero;
        }
    }
}
