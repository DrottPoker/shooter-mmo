using System;
using System.Linq;
using Mmorpg.WorldData.Actors;
using Mmorpg.WorldData.Editor.Actors;
using Mmorpg.WorldData.Editor.ContentTools;
using NUnit.Framework;
using UnityEditor;

namespace Mmorpg.Client.Tests.EditMode
{
    public sealed class WorldActorStudioViewTests
    {
        private const string LocalActorId = "npc.test_local_only";

        private ContentUndoState undoState;

        [SetUp]
        public void CreateUndoState()
        {
            undoState = ContentUndoState.Create("Actor Studio View Test");
        }

        [TearDown]
        public void DestroyUndoState()
        {
            Undo.ClearUndo(undoState);
            UnityEngine.Object.DestroyImmediate(undoState);
        }

        [Test]
        public void ActorStudioMapsLiveFindingsToFieldsAndUndoesBulkEdits()
        {
            var view = CreateView();
            try
            {
                view.Load(false);
                Assert.That(view.Issues, Is.Empty, string.Join("\n", view.Issues.Select(issue => issue.Message)));

                var mobs = MobKeys(view);
                view.List.Select(mobs);
                Assert.That(view.List.Selection.Count, Is.EqualTo(mobs.Length));
                var lifetimes = view.List.Selection.Select(actor => actor.corpseLifetimeSeconds).ToArray();

                Assert.That(view.Form.TrySetValue("corpseLifetimeSeconds", -1f), Is.True);

                var findings = WorldActorEditorIssues.Collect(view.Workspace);
                Assert.That(
                    findings.Where(issue => issue.Field == "corpseLifetimeSeconds").Select(issue => issue.EntityKey),
                    Is.EquivalentTo(mobs));
                Assert.That(view.HasUnsavedChanges, Is.True);

                Undo.PerformUndo();
                Assert.That(WorldActorEditorIssues.Collect(view.Workspace), Is.Empty);
                Assert.That(view.HasUnsavedChanges, Is.False);
                Assert.That(
                    view.Workspace.Catalog.actors.Where(actor => actor.kind == WorldActorKindIds.Mob).Select(actor => actor.corpseLifetimeSeconds),
                    Is.EqualTo(lifetimes));
            }
            finally
            {
                view.Dispose();
            }
        }

        [Test]
        public void ChangingKindKeepsTheActorValidAndReportsSpawnsItBreaks()
        {
            var view = CreateView();
            try
            {
                view.Load(false);
                var wolf = view.Workspace.Catalog.actors.Single(actor => actor.id == "mob.feral_wolf");
                view.List.Select(new[] { WorldActorStudioView.Key(wolf) });

                Assert.That(view.Form.TrySetValue("kind", WorldActorKindIds.Npc), Is.True);

                wolf = view.Workspace.Catalog.actors.Single(actor => actor.id == "mob.feral_wolf");
                Assert.That(wolf.damagePolicy, Is.EqualTo(WorldActorDamagePolicyIds.Invulnerable));
                Assert.That(wolf.activityProfileId, Is.Empty);
                Assert.That(wolf.corpseLifetimeSeconds, Is.Zero);
                var presentation = view.Workspace.Catalog.presentationArchetypes.Single(value => value.id == wolf.presentationArchetypeId);
                Assert.That(presentation.actorKind, Is.EqualTo(WorldActorKindIds.Npc));

                var findings = WorldActorEditorIssues.Collect(view.Workspace);
                Assert.That(findings.Where(issue => issue.EntityKey.StartsWith(WorldActorEditorIssues.Actors + "/")), Is.Empty);
                Assert.That(
                    findings.Select(issue => issue.EntityKey),
                    Does.Contain(WorldActorEditorIssues.Key(WorldActorEditorIssues.SpawnAreas, "local.feral_wolves")));
            }
            finally
            {
                view.Dispose();
            }
        }

        [Test]
        public void RenamingUpdatesLocalSpawnsInOneStepAndKeepsActorsOtherWorldsPlace()
        {
            var view = CreateView();
            try
            {
                view.Load(false);

                Assert.That(view.RenameActor(LocalActorId, "npc.test_renamed"), Is.True);
                Assert.That(view.Workspace.Catalog.actors.Select(actor => actor.id), Does.Contain("npc.test_renamed"));
                Assert.That(view.Workspace.Spawns.spawnPoints.Last().actorId, Is.EqualTo("npc.test_renamed"));
                Assert.That(view.List.Selection.Single().id, Is.EqualTo("npc.test_renamed"));

                Undo.PerformUndo();
                Assert.That(view.Workspace.Catalog.actors.Select(actor => actor.id), Does.Contain(LocalActorId));
                Assert.That(view.Workspace.Spawns.spawnPoints.Last().actorId, Is.EqualTo(LocalActorId));

                Assert.That(view.RenameActor("npc.city_guard", "npc.city_watch"), Is.False);
                Assert.That(view.StatusText, Does.Contain("development-world-2"));
                Assert.That(view.Workspace.Catalog.actors.Select(actor => actor.id), Does.Contain("npc.city_guard"));
            }
            finally
            {
                view.Dispose();
            }
        }

        [Test]
        public void DeletingKeepsActorsThatSpawnsPlace()
        {
            var view = CreateView();
            try
            {
                view.Load(false);
                view.AddActor(WorldActorKindIds.Mob);
                var created = view.List.Selection.Single();
                var guard = view.Workspace.Catalog.actors.Single(actor => actor.id == "npc.city_guard");
                view.List.Select(new[] { WorldActorStudioView.Key(created), WorldActorStudioView.Key(guard) });
                string[] approved = null;

                view.DeleteSelected(ids =>
                {
                    approved = ids;
                    return true;
                });

                Assert.That(approved, Is.EqualTo(new[] { created.id }));
                var ids = view.Workspace.Catalog.actors.Select(actor => actor.id).ToArray();
                Assert.That(ids, Does.Contain("npc.city_guard"));
                Assert.That(ids, Does.Not.Contain(created.id));
                Assert.That(view.StatusType, Is.EqualTo(UnityEngine.UIElements.HelpBoxMessageType.Warning));
            }
            finally
            {
                view.Dispose();
            }
        }

        [Test]
        public void TagsAreAddedAndRemovedForEverySelectedActor()
        {
            var view = CreateView();
            try
            {
                view.Load(false);
                var npcs = view.Workspace.Catalog.actors
                    .Where(actor => actor.kind == WorldActorKindIds.Npc && actor.id != LocalActorId)
                    .Select(WorldActorStudioView.Key)
                    .ToArray();
                view.List.Select(npcs);

                view.AddTag("vip");
                Assert.That(view.List.Selection.Select(actor => actor.tags.Contains("vip")), Is.All.True);
                Assert.That(view.List.Selection.Select(actor => actor.tags), Is.All.Ordered);

                Assert.That(view.Form.TrySetFlag("tags", "vip", false), Is.True);
                Assert.That(view.List.Selection.Select(actor => actor.tags.Contains("vip")), Is.All.False);
            }
            finally
            {
                view.Dispose();
            }
        }

        [Test]
        public void NewResourceNodeGetsOneGatheringCapabilityWithAResourceProfile()
        {
            var view = CreateView();
            try
            {
                view.Load(false);
                view.AddActor(WorldActorKindIds.ResourceNode);
                var node = view.List.Selection.Single();

                Assert.That(node.kind, Is.EqualTo(WorldActorKindIds.ResourceNode));
                Assert.That(node.damagePolicy, Is.EqualTo(WorldActorDamagePolicyIds.Invulnerable));
                Assert.That(node.presentationArchetypeId, Is.EqualTo("resource.iron_vein"));
                var capability = node.capabilities.Single();
                Assert.That(capability.kind, Is.EqualTo(WorldActorCapabilityKindIds.Gathering));
                Assert.That(capability.profileId, Is.EqualTo("resource.iron_vein"));
                Assert.That(
                    WorldActorEditorIssues.Collect(view.Workspace).Where(issue => issue.EntityKey == WorldActorStudioView.Key(node)),
                    Is.Empty);

                // A vendor capability names a vendor profile of the compiled item catalog.
                Assert.That(view.Workspace.ItemReferences.vendors.Select(vendor => vendor.id), Does.Contain("vendor.city_general_store"));
                var services = view.Workspace.Catalog.actors.Single(actor => actor.id == "npc.city_services");
                Assert.That(
                    services.capabilities.Single(value => value.kind == WorldActorCapabilityKindIds.Vendor).profileId,
                    Is.EqualTo("vendor.city_general_store"));

                Assert.That(view.Form.TrySetValue("kind", WorldActorKindIds.Npc), Is.True);
                node = view.Workspace.Catalog.actors.Single(actor => actor.id == node.id);
                Assert.That(node.capabilities, Is.Empty);
                Assert.That(WorldActorEditorIssues.Collect(view.Workspace).Where(issue => issue.EntityKey == WorldActorStudioView.Key(node)), Is.Empty);
            }
            finally
            {
                view.Dispose();
            }
        }

        private WorldActorStudioView CreateView()
        {
            var service = new WorldActorEditorService(WorldActorEditorPaths.CreateDefault());
            return new WorldActorStudioView(service, undoState, () => LoadWithLocalOnlyActor(service));
        }

        // The checked-in content plus an actor that only the edited World places.
        private static WorldActorEditorWorkspace LoadWithLocalOnlyActor(WorldActorEditorService service)
        {
            var workspace = service.Load();
            var actor = WorldActorEditorJson.Clone(workspace.Catalog.actors.Single(value => value.id == "npc.city_guard"));
            actor.id = LocalActorId;
            actor.capabilities = Array.Empty<WorldActorEditorCapability>();
            workspace.Catalog.actors = workspace.Catalog.actors.Concat(new[] { actor }).ToArray();
            workspace.Spawns.spawnPoints = workspace.Spawns.spawnPoints
                .Concat(new[]
                {
                    new WorldActorEditorSpawnPoint
                    {
                        id = "local.test_local_only",
                        actorId = LocalActorId,
                        patrolPathId = string.Empty
                    }
                })
                .ToArray();
            return workspace;
        }

        private static string[] MobKeys(WorldActorStudioView view)
        {
            return view.Workspace.Catalog.actors
                .Where(actor => actor.kind == WorldActorKindIds.Mob)
                .Select(WorldActorStudioView.Key)
                .ToArray();
        }
    }
}
