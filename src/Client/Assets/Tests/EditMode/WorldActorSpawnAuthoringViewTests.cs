using System.Linq;
using Mmorpg.WorldData.Editor.Actors;
using Mmorpg.WorldData.Editor.ContentTools;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Mmorpg.Client.Tests.EditMode
{
    public sealed class WorldActorSpawnAuthoringViewTests
    {
        private const string PointKey = "spawnPoints/local.city_services";
        private const string GroupKey = "spawnGroups/local.city_guards";
        private const string AreaKey = "spawnAreas/local.feral_wolves";
        private const string PatrolKey = "patrolPaths/local.wolf_patrol";

        private ContentUndoState undoState;

        [SetUp]
        public void CreateUndoState()
        {
            undoState = ContentUndoState.Create("Spawn Authoring View Test");
        }

        [TearDown]
        public void DestroyUndoState()
        {
            Undo.ClearUndo(undoState);
            Object.DestroyImmediate(undoState);
        }

        [Test]
        public void SpawnAuthoringListsEveryKindAndMovesOneAxisOfASelection()
        {
            var view = CreateView();
            try
            {
                view.Load(false);
                Assert.That(view.Issues, Is.Empty, string.Join("\n", view.Issues.Select(issue => issue.Message)));
                Assert.That(view.List.Shown.Select(entry => entry.Key), Is.EquivalentTo(new[] { PointKey, GroupKey, AreaKey, PatrolKey }));

                view.List.Select(new[] { PointKey, GroupKey, AreaKey });
                var before = view.List.Selection.ToDictionary(entry => entry.Key, entry => entry.Position);
                var areaWidth = Area(view).maximumX - Area(view).minimumX;
                var shown = view.List.Selection[0].Position;

                Assert.That(view.Form.TrySetValue("position", new Vector3(5f, shown.y, shown.z)), Is.True);

                foreach (var entry in view.List.Selection)
                {
                    Assert.That(entry.Position.x, Is.EqualTo(5f), entry.Key);
                    Assert.That(entry.Position.z, Is.EqualTo(before[entry.Key].z), entry.Key);
                }

                Assert.That(Area(view).maximumX - Area(view).minimumX, Is.EqualTo(areaWidth));
                Assert.That(view.HasUnsavedChanges, Is.True);

                Undo.PerformUndo();
                Assert.That(view.HasUnsavedChanges, Is.False);
                Assert.That(view.List.Selection.ToDictionary(entry => entry.Key, entry => entry.Position), Is.EqualTo(before));
            }
            finally
            {
                view.Dispose();
            }
        }

        [Test]
        public void LiveFindingsAreMappedToTheSpawnEntryAndField()
        {
            var view = CreateView();
            try
            {
                view.Load(false);
                view.List.Select(new[] { AreaKey });

                Assert.That(view.Form.TrySetValue("count", 0), Is.True);

                var findings = WorldActorEditorIssues.Collect(view.Workspace);
                Assert.That(findings.Single().EntityKey, Is.EqualTo(AreaKey));
                Assert.That(findings.Single().Field, Is.EqualTo("count"));
                Assert.That(WorldActorEditorIssues.Describe(findings.Single(), view.Workspace), Does.StartWith("Spawn area 'local.feral_wolves': "));
            }
            finally
            {
                view.Dispose();
            }
        }

        [Test]
        public void RenamingAPatrolPathUpdatesItsFollowersInOneStep()
        {
            var view = CreateView();
            try
            {
                view.Load(false);
                view.List.Select(new[] { PatrolKey });

                Assert.That(view.RenameEntry(view.List.Selection.Single(), "local.wolf_route"), Is.True);
                Assert.That(Area(view).patrolPathId, Is.EqualTo("local.wolf_route"));
                Assert.That(view.List.Selection.Single().Key, Is.EqualTo("patrolPaths/local.wolf_route"));
                Assert.That(WorldActorEditorIssues.Collect(view.Workspace), Is.Empty);

                Undo.PerformUndo();
                Assert.That(Area(view).patrolPathId, Is.EqualTo("local.wolf_patrol"));

                view.List.Select(new[] { AreaKey });
                Assert.That(view.RenameEntry(view.List.Selection.Single(), "local.city_services"), Is.False);
                Assert.That(Area(view).id, Is.EqualTo("local.feral_wolves"));
            }
            finally
            {
                view.Dispose();
            }
        }

        [Test]
        public void DeletingKeepsPatrolPathsThatSpawnsStillFollow()
        {
            var view = CreateView();
            try
            {
                view.Load(false);
                view.List.Select(new[] { PointKey, PatrolKey });
                string[] approved = null;

                view.DeleteSelected(ids =>
                {
                    approved = ids;
                    return true;
                });

                Assert.That(approved, Is.EqualTo(new[] { "local.city_services" }));
                Assert.That(view.Workspace.Spawns.spawnPoints, Is.Empty);
                Assert.That(view.Workspace.Spawns.patrolPaths.Single().id, Is.EqualTo("local.wolf_patrol"));
                Assert.That(view.StatusType, Is.EqualTo(UnityEngine.UIElements.HelpBoxMessageType.Warning));
            }
            finally
            {
                view.Dispose();
            }
        }

        [Test]
        public void GroundSnapMovesEverySelectedPositionInOneStep()
        {
            var view = CreateView();
            try
            {
                view.Load(false);
                view.List.Select(new[] { PointKey, PatrolKey });
                var patrolHeights = view.Workspace.Spawns.patrolPaths.Single().points.Select(point => point.y).ToArray();

                var snapped = view.GroundSnapSelected(position => position.x > 0f ? 1.5f : (float?)null);

                Assert.That(snapped, Is.EqualTo(4));
                Assert.That(view.Workspace.Spawns.spawnPoints.Single().y, Is.EqualTo(1.5f));
                Assert.That(view.Workspace.Spawns.patrolPaths.Single().points.Select(point => point.y), Is.All.EqualTo(1.5f));

                Undo.PerformUndo();
                Assert.That(view.Workspace.Spawns.patrolPaths.Single().points.Select(point => point.y), Is.EqualTo(patrolHeights));
            }
            finally
            {
                view.Dispose();
            }
        }

        [Test]
        public void NewAndDuplicatedEntriesGetUniqueIdsAndStayValid()
        {
            var view = CreateView();
            try
            {
                view.Load(false);

                view.AddEntry(WorldActorSpawnEntryKind.Group);
                var created = view.List.Selection.Single();
                Assert.That(created.Id, Is.EqualTo("local.new_group"));

                view.DuplicateSelected();
                var copy = view.List.Selection.Single();
                Assert.That(copy.Id, Is.EqualTo("local.new_group.copy"));
                Assert.That(copy.Position.x, Is.EqualTo(created.Position.x + 1f));

                view.AddEntry(WorldActorSpawnEntryKind.PatrolPath);
                Assert.That(view.List.Selection.Single().PatrolPath.points.Length, Is.EqualTo(2));
                Assert.That(WorldActorEditorIssues.Collect(view.Workspace), Is.Empty);
            }
            finally
            {
                view.Dispose();
            }
        }

        [Test]
        public void MobLifecycleSettingsEditThePlacedMobsRespawnProfile()
        {
            var view = CreateView();
            try
            {
                view.Load(false);
                view.List.Select(new[] { AreaKey, PointKey });

                Assert.That(view.Form.TrySetValue("respawn.populationLimit", 12), Is.True);

                Assert.That(view.Workspace.Catalog.respawnProfiles.Single().populationLimit, Is.EqualTo(12));
                Assert.That(view.Workspace.Catalog.actors.Single(actor => actor.id == "npc.city_services").respawnProfileId, Is.Empty);
            }
            finally
            {
                view.Dispose();
            }
        }

        private WorldActorSpawnAuthoringView CreateView()
        {
            return new WorldActorSpawnAuthoringView(
                new WorldActorEditorService(WorldActorEditorPaths.CreateDefault()),
                undoState);
        }

        private static WorldActorEditorSpawnArea Area(WorldActorSpawnAuthoringView view)
        {
            return view.Workspace.Spawns.spawnAreas.Single();
        }
    }
}
