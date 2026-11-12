using System;
using System.Collections.Generic;
using System.IO;
using Mmorpg.WorldData.Content;
using Mmorpg.WorldData.Editor.Actors;
using Mmorpg.WorldData.Text;
using UnityEngine;

namespace Mmorpg.Client.Tests.EditMode
{
    // A temporary copy of the actor content, every World, and the generated data the editor
    // reads. Development World 1 uses the test-owned spawn layout in tests/Fixtures/WorldActors,
    // shared with the .NET tests, so editor view tests never depend on a World's layout.
    internal sealed class FixtureWorldActorContent : IDisposable
    {
        public const string WorldId = "development-world-1";

        private readonly string temporaryDirectory;

        public FixtureWorldActorContent()
        {
            var repositoryPaths = WorldActorEditorPaths.CreateDefault();
            temporaryDirectory = Path.Combine(
                Path.GetTempPath(),
                "MmorpgWorldActorFixture",
                Guid.NewGuid().ToString("N"));
            Paths = new WorldActorEditorPaths(
                repositoryPaths.RepositoryRoot,
                Path.Combine(temporaryDirectory, "content"),
                Path.Combine(temporaryDirectory, "generated"),
                WorldId);
            CopyActorContent(repositoryPaths, Paths);
            File.Copy(
                SpawnsPath,
                ContentLayout.ActorSpawnsPath(Paths.ContentRoot, WorldId),
                true);
        }

        public static string SpawnsPath
        {
            get
            {
                return Path.GetFullPath(Path.Combine(
                    Application.dataPath,
                    "..",
                    "..",
                    "..",
                    "tests",
                    "Fixtures",
                    "WorldActors",
                    "actor-spawns.json"));
            }
        }

        public WorldActorEditorPaths Paths { get; }

        public void Dispose()
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, true);
            }
        }

        public static void CopyActorContent(
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

            foreach (var worldId in ContentLayout.ListWorldIds(ContentLayout.WorldsDirectory(source.ContentRoot)))
            {
                // The compiler only accepts complete Worlds.
                foreach (var fileName in ContentLayout.RequiredWorldFileNames)
                {
                    files.Add(new KeyValuePair<string, string>(
                        Path.Combine(ContentLayout.WorldDirectory(source.ContentRoot, worldId), fileName),
                        Path.Combine(ContentLayout.WorldDirectory(target.ContentRoot, worldId), fileName)));
                }

                files.Add(new KeyValuePair<string, string>(
                    ContentLayout.GeneratedWorldActorSpawnsPath(source.GeneratedRoot, worldId),
                    ContentLayout.GeneratedWorldActorSpawnsPath(target.GeneratedRoot, worldId)));
            }

            files.Add(new KeyValuePair<string, string>(
                ContentLayout.GeneratedActorCatalogPath(source.GeneratedRoot),
                ContentLayout.GeneratedActorCatalogPath(target.GeneratedRoot)));

            // Mob loot is checked against the compiled item catalog.
            files.Add(new KeyValuePair<string, string>(
                ContentLayout.GeneratedItemCatalogPath(source.GeneratedRoot),
                ContentLayout.GeneratedItemCatalogPath(target.GeneratedRoot)));

            // The actor compiler verifies the actor text table with the runtimes.
            files.Add(new KeyValuePair<string, string>(
                ContentLayout.GeneratedTextTablePath(
                    source.GeneratedRoot,
                    TextTableFormat.SourceLocale,
                    TextTableIds.Actors),
                ContentLayout.GeneratedTextTablePath(
                    target.GeneratedRoot,
                    TextTableFormat.SourceLocale,
                    TextTableIds.Actors)));

            foreach (var file in files)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file.Value));
                File.Copy(file.Key, file.Value);
            }
        }
    }
}
