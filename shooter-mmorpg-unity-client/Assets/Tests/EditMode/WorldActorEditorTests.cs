using System;
using NUnit.Framework;
using ShooterMmo.WorldData.Actors;
using ShooterMmo.WorldData.Editor.Actors;

namespace ShooterMmo.Tests.EditMode
{
    public sealed class WorldActorEditorTests
    {
        [Test]
        public void CheckedInContentRoundTripsThroughNeutralEditorModels()
        {
            var service = new WorldActorEditorService(
                WorldActorEditorPaths.CreateDefault());
            var workspace = service.Load();
            var catalog = WorldActorEditorJson.DeserializeCatalog(
                WorldActorEditorJson.SerializeCatalog(workspace.Catalog));
            var spawns = WorldActorEditorJson.DeserializeSpawns(
                WorldActorEditorJson.SerializeSpawns(workspace.Spawns));

            var compiled = WorldActorCompiler.Compile(
                catalog.ToDomain(),
                spawns.ToDomain());

            Assert.That(compiled.Revision, Is.EqualTo(workspace.CurrentRuntime.revision));
            Assert.That(compiled.Actors, Has.Length.EqualTo(3));
            Assert.That(compiled.SpawnInstances, Has.Length.EqualTo(5));
            Assert.That(
                Array.Find(compiled.Actors, actor => actor.Id == "npc.city_services")
                    .Capabilities,
                Has.Length.EqualTo(8));
        }

        [Test]
        public void PreviewCompileAndVerifyUseCanonicalCompiler()
        {
            var service = new WorldActorEditorService(
                WorldActorEditorPaths.CreateDefault());
            var workspace = service.Load();

            var preview = service.Validate(workspace);
            var verify = service.VerifyCanonical();

            Assert.That(preview.Success, Is.True, preview.Message);
            Assert.That(preview.Changes, Is.Empty);
            Assert.That(verify.Success, Is.True, verify.Output);
        }

        [Test]
        public void ContentToolsRemainUnderShooterMmoToolsMenu()
        {
            Assert.That(
                WorldActorStudioWindow.MenuPath,
                Is.EqualTo("Shooter MMO/Tools/Content/Actor Studio"));
            Assert.That(
                WorldActorSpawnAuthoringWindow.MenuPath,
                Is.EqualTo("Shooter MMO/Tools/Content/Spawn Authoring"));
        }

        [Test]
        public void ActorTagsAndPopulationLimitSurviveEditorRoundTrip()
        {
            var service = new WorldActorEditorService(
                WorldActorEditorPaths.CreateDefault());
            var workspace = service.Load();
            var catalog = WorldActorEditorJson.DeserializeCatalog(
                WorldActorEditorJson.SerializeCatalog(workspace.Catalog));

            Assert.That(
                Array.Find(catalog.actors, actor => actor.id == "mob.feral_wolf").tags,
                Is.EquivalentTo(new[] { "creature", "wild" }));
            Assert.That(
                Array.Find(catalog.respawnProfiles, profile => profile.id == "mob.normal")
                    .populationLimit,
                Is.EqualTo(32));
        }
    }
}
