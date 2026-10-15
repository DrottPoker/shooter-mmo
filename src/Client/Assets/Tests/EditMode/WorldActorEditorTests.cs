using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Mmorpg.WorldData.Actors;
using Mmorpg.WorldData.Content;
using Mmorpg.WorldData.Editor;
using Mmorpg.WorldData.Editor.Actors;
using NUnit.Framework;

namespace Mmorpg.Client.Tests.EditMode
{
    public sealed class WorldActorEditorTests
    {
        [Test]
        public void CheckedInContentRoundTripsThroughNeutralEditorModels()
        {
            var service = new WorldActorEditorService(
                WorldActorEditorPaths.CreateDefault());
            var workspace = service.Load();
            var catalog = RoundTrip(workspace.Catalog);
            var spawns = WorldActorEditorJson.DeserializeSpawns(
                WorldActorEditorJson.SerializeSpawns(workspace.Spawns));

            var compiled = WorldActorCompiler.Compile(
                catalog.ToDomain(),
                spawns.ToDomain());

            Assert.That(compiled.Revision, Is.EqualTo(workspace.CurrentRuntime.revision));
            Assert.That(compiled.Actors, Has.Length.EqualTo(4));
            Assert.That(compiled.SpawnInstances, Has.Length.EqualTo(5));
            Assert.That(
                Array.Find(compiled.Actors, actor => actor.Id == "npc.city_services")
                    .Capabilities,
                Has.Length.EqualTo(8));
        }

        [Test]
        public void CheckedInActorContentIsInCanonicalEditorFormat()
        {
            var paths = WorldActorEditorPaths.CreateDefault();
            var expected = WorldActorEditorContent.CreateFiles(
                WorldActorEditorContent.Load(paths.ActorsDirectory));
            var actual = Directory.EnumerateFiles(
                    paths.ActorsDirectory,
                    "*.json",
                    SearchOption.AllDirectories)
                .ToDictionary(
                    path => Path.GetRelativePath(paths.ActorsDirectory, path),
                    ReadNormalized);

            Assert.That(actual.Keys, Is.EquivalentTo(expected.Keys));
            foreach (var file in expected)
            {
                Assert.That(actual[file.Key], Is.EqualTo(file.Value), file.Key);
            }

            foreach (var worldId in WorldIds(paths.ContentRoot))
            {
                var spawnPath = ContentLayout.ActorSpawnsPath(paths.ContentRoot, worldId);
                var text = ReadNormalized(spawnPath);
                Assert.That(
                    WorldActorEditorJson.SerializeSpawns(
                        WorldActorEditorJson.DeserializeSpawns(text)),
                    Is.EqualTo(text),
                    spawnPath);
            }
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
            Assert.That(preview.RemovedFiles, Is.Empty);
            Assert.That(preview.Files.Where(file => file.Changed), Is.Empty);
            Assert.That(
                preview.Files.Select(file => file.DisplayPath),
                Does.Contain("Generated/Worlds/development-world-1/world-actors.json")
                    .And.Contain("Generated/Worlds/development-world-2/world-actors.json"));
            Assert.That(verify.Success, Is.True, verify.Output);
        }

        [Test]
        public void CatalogChangesRecompileEveryWorldAndRemovedActorsDeleteTheirFiles()
        {
            var repositoryPaths = WorldActorEditorPaths.CreateDefault();
            var temporaryDirectory = Path.Combine(
                Path.GetTempPath(),
                "MmorpgWorldActorEditorTests",
                Guid.NewGuid().ToString("N"));
            try
            {
                var paths = new WorldActorEditorPaths(
                    repositoryPaths.RepositoryRoot,
                    Path.Combine(temporaryDirectory, "content"),
                    Path.Combine(temporaryDirectory, "generated"),
                    repositoryPaths.WorldId);
                CopyActorContent(repositoryPaths, paths);
                var service = new WorldActorEditorService(paths);
                var workspace = service.Load();
                var added = WorldActorEditorJson.Clone(
                    Array.Find(workspace.Catalog.actors, actor => actor.id == "mob.feral_wolf"));
                added.id = "mob.editor_test_wolf";
                workspace.Catalog.actors = workspace.Catalog.actors
                    .Concat(new[] { added })
                    .ToArray();

                var addition = service.Validate(workspace);
                Assert.That(addition.Success, Is.True, addition.Message);
                Assert.That(addition.Changes, Does.Contain("Added actor: mob.editor_test_wolf"));
                Assert.That(
                    addition.Changes,
                    Does.Contain("Recompiled world actors: development-world-2"));
                service.SaveAndCompile(workspace, addition);

                var addedPath = ContentLayout.DefinitionPath(
                    paths.ActorsDirectory,
                    "mob.editor_test_wolf");
                Assert.That(File.Exists(addedPath), Is.True);
                var verifyAfterAddition = service.VerifyCanonical();
                Assert.That(verifyAfterAddition.Success, Is.True, verifyAfterAddition.Output);

                workspace.Catalog.actors = workspace.Catalog.actors
                    .Where(actor => actor.id != "mob.editor_test_wolf")
                    .ToArray();
                var removal = service.Validate(workspace);
                Assert.That(removal.Success, Is.True, removal.Message);
                Assert.That(removal.RemovedFiles, Is.EqualTo(new[] { Path.GetFullPath(addedPath) }));
                service.SaveAndCompile(workspace, removal);

                Assert.That(File.Exists(addedPath), Is.False);
                var verifyAfterRemoval = service.VerifyCanonical();
                Assert.That(verifyAfterRemoval.Success, Is.True, verifyAfterRemoval.Output);
                foreach (var worldId in WorldIds(repositoryPaths.ContentRoot))
                {
                    Assert.That(
                        ReadNormalized(ContentLayout.GeneratedWorldActorsPath(
                            paths.GeneratedRoot,
                            worldId)),
                        Is.EqualTo(ReadNormalized(ContentLayout.GeneratedWorldActorsPath(
                            repositoryPaths.GeneratedRoot,
                            worldId))),
                        worldId);
                }
            }
            finally
            {
                if (Directory.Exists(temporaryDirectory))
                {
                    Directory.Delete(temporaryDirectory, true);
                }
            }
        }

        [Test]
        public void FloatsAreWrittenWithTheirShortestRoundTripText()
        {
            const string json = "{\"a\": 0.550000011920929, \"b\": 120.0, "
                + "\"c\": \"0.550000011920929\", \"d\": 3, \"e\": 0.1, \"f\": -1.7999999523162842}";

            Assert.That(
                SinglePrecisionJson.ShortenFloats(json),
                Is.EqualTo("{\"a\": 0.55, \"b\": 120.0, "
                    + "\"c\": \"0.550000011920929\", \"d\": 3, \"e\": 0.1, \"f\": -1.8}"));
        }

        [Test]
        public void ActorEditorModelsHoldNoDoubleFields()
        {
            var modelNamespace = typeof(WorldActorEditorCatalog).Namespace;
            var doubleFields = typeof(WorldActorEditorCatalog).Assembly.GetTypes()
                .Where(type => type.Namespace == modelNamespace && type.IsSerializable)
                .SelectMany(type => type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                .Where(field => field.FieldType == typeof(double)
                    || field.FieldType == typeof(double[]))
                .Select(field => field.DeclaringType.Name + "." + field.Name);

            Assert.That(doubleFields, Is.Empty);
        }

        [Test]
        public void ContentToolsRemainUnderTheSharedToolsMenu()
        {
            Assert.That(
                WorldActorStudioWindow.MenuPath,
                Is.EqualTo("MMORPG/Tools/Content/Actor Studio"));
            Assert.That(
                WorldActorSpawnAuthoringWindow.MenuPath,
                Is.EqualTo("MMORPG/Tools/Content/Spawn Authoring"));
        }

        [Test]
        public void ActorTagsAndPopulationLimitSurviveEditorRoundTrip()
        {
            var service = new WorldActorEditorService(
                WorldActorEditorPaths.CreateDefault());
            var workspace = service.Load();
            var catalog = RoundTrip(workspace.Catalog);

            Assert.That(
                Array.Find(catalog.actors, actor => actor.id == "mob.feral_wolf").tags,
                Is.EquivalentTo(new[] { "creature", "wild" }));
            Assert.That(
                Array.Find(catalog.respawnProfiles, profile => profile.id == "mob.normal")
                    .populationLimit,
                Is.EqualTo(32));
        }

        [Test]
        public void MobCorpseSettingsSurviveEditorRoundTripPerDefinition()
        {
            var service = new WorldActorEditorService(
                WorldActorEditorPaths.CreateDefault());
            var workspace = service.Load();
            var catalog = RoundTrip(workspace.Catalog);
            var normalMob = Array.Find(
                catalog.actors,
                actor => actor.id == "mob.feral_wolf");
            var bossMob = Array.Find(
                catalog.actors,
                actor => actor.id == "mob.feral_alpha");

            Assert.That(
                normalMob.corpsePersistenceMode,
                Is.EqualTo(WorldActorCorpsePersistenceModeIds.Live));
            Assert.That(normalMob.corpseLifetimeSeconds, Is.EqualTo(120f));
            Assert.That(
                bossMob.corpsePersistenceMode,
                Is.EqualTo(WorldActorCorpsePersistenceModeIds.Durable));
            Assert.That(bossMob.corpseLifetimeSeconds, Is.EqualTo(600f));
        }

        private static WorldActorEditorCatalog RoundTrip(WorldActorEditorCatalog catalog)
        {
            var temporaryDirectory = Path.Combine(
                Path.GetTempPath(),
                "MmorpgWorldActorEditorTests",
                Guid.NewGuid().ToString("N"));
            try
            {
                foreach (var file in WorldActorEditorContent.CreateFiles(catalog))
                {
                    var path = Path.Combine(temporaryDirectory, file.Key);
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllText(path, file.Value);
                }

                return WorldActorEditorContent.Load(temporaryDirectory);
            }
            finally
            {
                if (Directory.Exists(temporaryDirectory))
                {
                    Directory.Delete(temporaryDirectory, true);
                }
            }
        }

        private static void CopyActorContent(
            WorldActorEditorPaths source,
            WorldActorEditorPaths target)
        {
            var files = new List<KeyValuePair<string, string>>();
            foreach (var path in Directory.EnumerateFiles(
                         source.ActorsDirectory,
                         "*.json",
                         SearchOption.AllDirectories))
            {
                files.Add(new KeyValuePair<string, string>(
                    path,
                    Path.Combine(
                        target.ActorsDirectory,
                        Path.GetRelativePath(source.ActorsDirectory, path))));
            }

            foreach (var worldId in WorldIds(source.ContentRoot))
            {
                // The compiler only accepts complete Worlds.
                foreach (var fileName in ContentLayout.RequiredWorldFileNames)
                {
                    files.Add(new KeyValuePair<string, string>(
                        Path.Combine(ContentLayout.WorldDirectory(source.ContentRoot, worldId), fileName),
                        Path.Combine(ContentLayout.WorldDirectory(target.ContentRoot, worldId), fileName)));
                }

                files.Add(new KeyValuePair<string, string>(
                    ContentLayout.GeneratedWorldActorsPath(source.GeneratedRoot, worldId),
                    ContentLayout.GeneratedWorldActorsPath(target.GeneratedRoot, worldId)));
            }

            foreach (var file in files)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file.Value));
                File.Copy(file.Key, file.Value);
            }
        }

        private static string[] WorldIds(string contentRoot)
        {
            return ContentLayout.ListWorldIds(ContentLayout.WorldsDirectory(contentRoot));
        }

        private static string ReadNormalized(string path)
        {
            return ContentFileTransaction.NormalizeLineEndings(File.ReadAllText(path));
        }
    }
}
