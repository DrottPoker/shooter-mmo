using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Mmorpg.WorldData.Actors;
using Mmorpg.WorldData.Content;
using UnityEngine;

namespace Mmorpg.WorldData.Editor.Actors
{
    public sealed class WorldActorEditorPaths
    {
        public const string DefaultWorldId = "development-world-1";

        public WorldActorEditorPaths(
            string repositoryRoot,
            string contentRoot,
            string generatedRoot,
            string worldId)
        {
            RepositoryRoot = Path.GetFullPath(repositoryRoot);
            ContentRoot = Path.GetFullPath(contentRoot);
            GeneratedRoot = Path.GetFullPath(generatedRoot);
            WorldId = worldId;
            SpawnPath = ContentLayout.ActorSpawnsPath(ContentRoot, worldId);
            RuntimePath = ContentLayout.GeneratedWorldActorsPath(GeneratedRoot, worldId);
        }

        public string RepositoryRoot { get; }

        public string ContentRoot { get; }

        public string GeneratedRoot { get; }

        // The World whose spawns the Editor edits. Every World is recompiled on save.
        public string WorldId { get; }

        public string ActorsDirectory => ContentLayout.ActorsDirectory(ContentRoot);

        public string SpawnPath { get; }

        public string RuntimePath { get; }

        public static WorldActorEditorPaths CreateDefault(string worldId = DefaultWorldId)
        {
            var repositoryRoot = RepositoryLayout.FindRoot();
            return new WorldActorEditorPaths(
                repositoryRoot,
                RepositoryLayout.ContentRoot(repositoryRoot),
                RepositoryLayout.GeneratedRoot(repositoryRoot),
                worldId);
        }
    }

    public sealed class WorldActorEditorWorkspace
    {
        public WorldActorEditorWorkspace(
            WorldActorEditorCatalog catalog,
            WorldActorEditorSpawns spawns,
            WorldActorEditorRuntime runtime,
            WorldActorEditorItemReferences itemReferences = null)
        {
            Catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            Spawns = spawns ?? throw new ArgumentNullException(nameof(spawns));
            CurrentRuntime = runtime
                ?? throw new ArgumentNullException(nameof(runtime));
            ItemReferences = itemReferences ?? new WorldActorEditorItemReferences();
        }

        public WorldActorEditorCatalog Catalog { get; }
        public WorldActorEditorSpawns Spawns { get; }
        public WorldActorEditorRuntime CurrentRuntime { get; private set; }

        // The vendor profiles of the compiled item catalog, which vendor capabilities name.
        public WorldActorEditorItemReferences ItemReferences { get; }

        public void ApplyRuntime(WorldActorEditorRuntime runtime)
        {
            CurrentRuntime = runtime
                ?? throw new ArgumentNullException(nameof(runtime));
        }

        // Replaces the editable content with a snapshot taken earlier, for undo and redo.
        public void RestoreContent(WorldActorEditorCatalog catalog, WorldActorEditorSpawns spawns)
        {
            if (catalog == null)
            {
                throw new ArgumentNullException(nameof(catalog));
            }

            if (spawns == null)
            {
                throw new ArgumentNullException(nameof(spawns));
            }

            Catalog.formatVersion = catalog.formatVersion;
            Catalog.catalogId = catalog.catalogId;
            Catalog.factions = catalog.factions;
            Catalog.presentationArchetypes = catalog.presentationArchetypes;
            Catalog.activityProfiles = catalog.activityProfiles;
            Catalog.respawnProfiles = catalog.respawnProfiles;
            Catalog.actors = catalog.actors;
            Spawns.formatVersion = spawns.formatVersion;
            Spawns.worldId = spawns.worldId;
            Spawns.actorCatalogId = spawns.actorCatalogId;
            Spawns.spawnPoints = spawns.spawnPoints;
            Spawns.spawnGroups = spawns.spawnGroups;
            Spawns.spawnAreas = spawns.spawnAreas;
            Spawns.patrolPaths = spawns.patrolPaths;
        }
    }

    // The part of the compiled item catalog that actor content refers to. Unknown fields of
    // the runtime catalog are ignored.
    [Serializable]
    public sealed class WorldActorEditorItemReferences
    {
        public WorldActorEditorItemReference[] vendors = Array.Empty<WorldActorEditorItemReference>();

        public static WorldActorEditorItemReferences Load(string generatedRoot)
        {
            var path = ContentLayout.GeneratedItemCatalogPath(generatedRoot);
            if (!File.Exists(path))
            {
                return new WorldActorEditorItemReferences();
            }

            var references = JsonUtility.FromJson<WorldActorEditorItemReferences>(File.ReadAllText(path))
                ?? new WorldActorEditorItemReferences();
            references.vendors = references.vendors ?? Array.Empty<WorldActorEditorItemReference>();
            return references;
        }
    }

    [Serializable]
    public sealed class WorldActorEditorItemReference
    {
        public string id;
        public string displayName;
    }

    // The editable part of a workspace: what undo and redo restore.
    [Serializable]
    public sealed class WorldActorEditorSnapshot
    {
        public WorldActorEditorCatalog catalog;
        public WorldActorEditorSpawns spawns;
    }

    // One file a successful validation would write, with its canonical text.
    public sealed class WorldActorEditorFile
    {
        public WorldActorEditorFile(string displayPath, string fullPath, string text, bool changed)
        {
            DisplayPath = displayPath;
            FullPath = fullPath;
            Text = text;
            Changed = changed;
        }

        public string DisplayPath { get; }
        public string FullPath { get; }
        public string Text { get; }
        public bool Changed { get; }
    }

    public sealed class WorldActorEditorValidationResult
    {
        private WorldActorEditorValidationResult(
            bool success,
            string message,
            WorldActorEditorFile[] files,
            string[] removedFiles,
            string runtimeJson,
            WorldActorRuntimeDocument candidate,
            string[] changes)
        {
            Success = success;
            Message = message ?? string.Empty;
            Files = files ?? Array.Empty<WorldActorEditorFile>();
            RemovedFiles = removedFiles ?? Array.Empty<string>();
            RuntimeJson = runtimeJson;
            Candidate = candidate;
            Changes = changes ?? Array.Empty<string>();
        }

        public bool Success { get; }
        public string Message { get; }

        // Authored and generated files in their canonical form, changed or not.
        public WorldActorEditorFile[] Files { get; }

        // Actor definition files whose actors were removed from the catalog.
        public string[] RemovedFiles { get; }

        // Compiled runtime of the edited World.
        public string RuntimeJson { get; }
        public WorldActorRuntimeDocument Candidate { get; }
        public string[] Changes { get; }

        public static WorldActorEditorValidationResult Failed(string message)
        {
            return new WorldActorEditorValidationResult(
                false,
                message,
                null,
                null,
                null,
                null,
                null);
        }

        public static WorldActorEditorValidationResult Passed(
            string message,
            WorldActorEditorFile[] files,
            string[] removedFiles,
            string runtimeJson,
            WorldActorRuntimeDocument candidate,
            string[] changes)
        {
            return new WorldActorEditorValidationResult(
                true,
                message,
                files,
                removedFiles,
                runtimeJson,
                candidate,
                changes);
        }
    }

    public sealed class WorldActorEditorService
    {
        public const string CompilerToolName = "WorldActorCompiler";

        // Resource nodes block movement through the World collision, so a save recompiles it.
        public const string CollisionCompilerToolName = "WorldCollisionCompiler";

        private readonly WorldActorEditorPaths paths;
        private readonly IContentCompilerRunner compilerRunner;
        private readonly IContentCompilerRunner collisionCompilerRunner;

        public WorldActorEditorService(
            WorldActorEditorPaths paths,
            IContentCompilerRunner compilerRunner = null,
            IContentCompilerRunner collisionCompilerRunner = null)
        {
            this.paths = paths ?? throw new ArgumentNullException(nameof(paths));
            this.compilerRunner = compilerRunner ?? new ContentCompilerProcessRunner(
                paths.RepositoryRoot,
                CompilerToolName);
            this.collisionCompilerRunner = collisionCompilerRunner ?? new ContentCompilerProcessRunner(
                paths.RepositoryRoot,
                CollisionCompilerToolName);
        }

        public WorldActorEditorPaths Paths => paths;

        public WorldActorEditorWorkspace Load()
        {
            RequireFile(paths.SpawnPath);
            RequireFile(paths.RuntimePath);
            return new WorldActorEditorWorkspace(
                WorldActorEditorContent.Load(paths.ActorsDirectory),
                WorldActorEditorJson.DeserializeSpawns(File.ReadAllText(paths.SpawnPath)),
                WorldActorEditorJson.DeserializeRuntime(File.ReadAllText(paths.RuntimePath)),
                WorldActorEditorItemReferences.Load(paths.GeneratedRoot));
        }

        // Compiles the edited catalog and spawns together with the committed spawns of every
        // other World, because each World's runtime embeds the shared actor catalog.
        public WorldActorEditorValidationResult Validate(
            WorldActorEditorWorkspace workspace)
        {
            if (workspace == null)
            {
                throw new ArgumentNullException(nameof(workspace));
            }

            var temporaryDirectory = Path.Combine(
                Path.GetTempPath(),
                "Mmorpg.Client.WorldActors." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryDirectory);
            try
            {
                var candidate = WorldActorCompiler.Compile(
                    workspace.Catalog.ToDomain(),
                    workspace.Spawns.ToDomain());
                var contentRoot = Path.Combine(temporaryDirectory, "content");
                var generatedRoot = Path.Combine(temporaryDirectory, "generated");
                var authoredFiles = CreateAuthoredFiles(workspace);
                foreach (var file in authoredFiles)
                {
                    WriteText(Path.Combine(contentRoot, file.Key), file.Value);
                }

                string[] worldIds;
                try
                {
                    worldIds = ContentLayout.ListWorldIds(
                        ContentLayout.WorldsDirectory(paths.ContentRoot));
                }
                catch (InvalidDataException exception)
                {
                    return WorldActorEditorValidationResult.Failed(exception.Message);
                }

                if (!worldIds.Contains(paths.WorldId, StringComparer.Ordinal))
                {
                    return WorldActorEditorValidationResult.Failed(
                        "World '" + paths.WorldId + "' does not exist below '"
                        + ContentLayout.WorldsDirectory(paths.ContentRoot) + "'.");
                }

                // The compiler only accepts complete Worlds, so every committed World file is
                // copied except the edited spawns, which come from the workspace.
                foreach (var worldId in worldIds)
                {
                    foreach (var fileName in ContentLayout.RequiredWorldFileNames)
                    {
                        if (worldId == paths.WorldId
                            && fileName == ContentLayout.ActorSpawnsFileName)
                        {
                            continue;
                        }

                        CopyWorldFile(paths.ContentRoot, contentRoot, worldId, fileName);
                    }
                }

                // Loot, vendor capabilities, and resource profiles are checked against the
                // compiled item catalog, which the item compiler writes before the actor
                // compiler runs.
                var itemCatalogPath = ContentLayout.GeneratedItemCatalogPath(generatedRoot);
                Directory.CreateDirectory(Path.GetDirectoryName(itemCatalogPath));
                File.Copy(ContentLayout.GeneratedItemCatalogPath(paths.GeneratedRoot), itemCatalogPath);

                var compilerResult = compilerRunner.Compile(contentRoot, generatedRoot);
                if (!compilerResult.Success)
                {
                    return WorldActorEditorValidationResult.Failed(
                        compilerResult.Output.Length == 0
                            ? CompilerToolName + " failed without output."
                            : compilerResult.Output);
                }

                var generatedFiles = new List<KeyValuePair<string, string>>();
                foreach (var worldId in worldIds)
                {
                    var relativePath = ContentLayout.GeneratedWorldActorsPath(string.Empty, worldId);
                    var path = Path.Combine(generatedRoot, relativePath);
                    if (!File.Exists(path))
                    {
                        return WorldActorEditorValidationResult.Failed(
                            CompilerToolName + " did not produce runtime JSON for World '"
                            + worldId + "'.");
                    }

                    generatedFiles.Add(new KeyValuePair<string, string>(
                        relativePath,
                        Normalize(File.ReadAllText(path))));
                }

                var runtimeJson = generatedFiles.Single(file => string.Equals(
                    file.Key,
                    ContentLayout.GeneratedWorldActorsPath(string.Empty, paths.WorldId),
                    StringComparison.Ordinal)).Value;
                var parsedRuntime = WorldActorEditorJson.DeserializeRuntime(runtimeJson);
                if (!string.Equals(
                        candidate.Revision,
                        parsedRuntime.revision,
                        StringComparison.Ordinal))
                {
                    return WorldActorEditorValidationResult.Failed(
                        "Editor validation and " + CompilerToolName + " revisions differ.");
                }

                var files = authoredFiles
                    .Select(file => CreateFile("content", paths.ContentRoot, file))
                    .Concat(generatedFiles.Select(file =>
                        CreateFile("Generated", paths.GeneratedRoot, file)))
                    .ToArray();
                var written = new HashSet<string>(
                    files.Select(file => file.FullPath),
                    StringComparer.OrdinalIgnoreCase);
                var removedFiles = ContentLayout.ListDefinitionFiles(paths.ActorsDirectory)
                    .Select(file => Path.GetFullPath(file.Path))
                    .Where(path => !written.Contains(path))
                    .ToArray();
                var changes = ClassifyChanges(workspace.CurrentRuntime, candidate).ToList();
                foreach (var worldId in worldIds.Where(worldId => worldId != paths.WorldId))
                {
                    var runtimeFile = files.Single(file => string.Equals(
                        file.FullPath,
                        Path.GetFullPath(ContentLayout.GeneratedWorldActorsPath(
                            paths.GeneratedRoot,
                            worldId)),
                        StringComparison.OrdinalIgnoreCase));
                    if (runtimeFile.Changed)
                    {
                        changes.Add("Recompiled world actors: " + worldId);
                    }
                }

                var message = compilerResult.Output.Trim();
                if (changes.Count == 0)
                {
                    message += Environment.NewLine + "No runtime actor changes detected.";
                }
                else
                {
                    message += Environment.NewLine + string.Join(Environment.NewLine, changes);
                }

                return WorldActorEditorValidationResult.Passed(
                    message.Trim(),
                    files,
                    removedFiles,
                    runtimeJson,
                    candidate,
                    changes.ToArray());
            }
            catch (Exception exception)
            {
                return WorldActorEditorValidationResult.Failed(exception.Message);
            }
            finally
            {
                try
                {
                    Directory.Delete(temporaryDirectory, true);
                }
                catch
                {
                    // Temporary validation files are safe to leave for OS cleanup.
                }
            }
        }

        // Writes the validated files, then recompiles the World collision from the new actor
        // runtime, since each resource node adds a box to it. A failed collision compile leaves
        // the actor files written and the collision stale until WorldCollisionCompiler runs.
        public ContentCompilerRunResult SaveAndCompile(
            WorldActorEditorWorkspace workspace,
            WorldActorEditorValidationResult validation)
        {
            if (workspace == null)
            {
                throw new ArgumentNullException(nameof(workspace));
            }

            if (validation == null || !validation.Success)
            {
                throw new InvalidOperationException(
                    "Successful actor validation is required before saving.");
            }

            ContentFileTransaction.Commit(
                validation.Files.Select(file =>
                    new KeyValuePair<string, string>(file.FullPath, file.Text)),
                validation.RemovedFiles);
            workspace.ApplyRuntime(
                WorldActorEditorJson.DeserializeRuntime(validation.RuntimeJson));
            return collisionCompilerRunner.Compile(paths.ContentRoot, paths.GeneratedRoot);
        }

        public ContentCompilerRunResult VerifyCanonical()
        {
            return compilerRunner.Verify(paths.ContentRoot, paths.GeneratedRoot);
        }

        // The other Worlds whose committed spawns place the actor. Only the edited World's
        // spawns are part of the workspace, but every World compiles against the one catalog.
        public string[] FindOtherWorldReferences(string actorId)
        {
            return ContentLayout.ListWorldIds(ContentLayout.WorldsDirectory(paths.ContentRoot))
                .Where(worldId => !string.Equals(worldId, paths.WorldId, StringComparison.Ordinal))
                .Where(worldId =>
                {
                    var spawnPath = ContentLayout.ActorSpawnsPath(paths.ContentRoot, worldId);
                    return File.Exists(spawnPath)
                        && WorldActorEditorJson.DeserializeSpawns(File.ReadAllText(spawnPath)).CountActorReferences(actorId) > 0;
                })
                .ToArray();
        }

        // Authored file text keyed by path relative to the content root.
        private List<KeyValuePair<string, string>> CreateAuthoredFiles(
            WorldActorEditorWorkspace workspace)
        {
            var actorsDirectory = ContentLayout.ActorsDirectory(string.Empty);
            var files = WorldActorEditorContent.CreateFiles(workspace.Catalog)
                .OrderBy(file => file.Key, StringComparer.Ordinal)
                .Select(file => new KeyValuePair<string, string>(
                    Path.Combine(actorsDirectory, file.Key),
                    file.Value))
                .ToList();
            files.Add(new KeyValuePair<string, string>(
                ContentLayout.ActorSpawnsPath(string.Empty, paths.WorldId),
                WorldActorEditorJson.SerializeSpawns(workspace.Spawns)));
            return files;
        }

        private static WorldActorEditorFile CreateFile(
            string displayRoot,
            string root,
            KeyValuePair<string, string> file)
        {
            var fullPath = Path.GetFullPath(Path.Combine(root, file.Key));
            var changed = !File.Exists(fullPath)
                || !string.Equals(
                    Normalize(File.ReadAllText(fullPath)),
                    file.Value,
                    StringComparison.Ordinal);
            return new WorldActorEditorFile(
                displayRoot + "/" + file.Key.Replace('\\', '/'),
                fullPath,
                file.Value,
                changed);
        }

        private static string[] ClassifyChanges(
            WorldActorEditorRuntime baseline,
            WorldActorRuntimeDocument candidate)
        {
            var changes = new List<string>();
            Compare(
                "actor",
                baseline.actors,
                candidate.Actors.Select(value =>
                    new KeyValuePair<string, string>(value.Id, value.StructuralFingerprint)),
                changes);
            Compare(
                "spawn source",
                baseline.spawnSources,
                candidate.SpawnSources.Select(value =>
                    new KeyValuePair<string, string>(value.Id, value.StructuralFingerprint)),
                changes);
            Compare(
                "spawn instance",
                baseline.spawnInstances,
                candidate.SpawnInstances.Select(value =>
                    new KeyValuePair<string, string>(value.Id, value.StructuralFingerprint)),
                changes);
            return changes.ToArray();
        }

        private static void Compare(
            string label,
            WorldActorEditorRuntimeEntry[] baseline,
            IEnumerable<KeyValuePair<string, string>> candidate,
            ICollection<string> changes)
        {
            var oldValues = (baseline ?? Array.Empty<WorldActorEditorRuntimeEntry>())
                .ToDictionary(value => value.id, value => value.structuralFingerprint,
                    StringComparer.Ordinal);
            var newValues = candidate.ToDictionary(
                value => value.Key,
                value => value.Value,
                StringComparer.Ordinal);
            foreach (var id in oldValues.Keys.Except(newValues.Keys).OrderBy(value => value))
            {
                changes.Add("Removed " + label + ": " + id);
            }

            foreach (var id in newValues.Keys.Except(oldValues.Keys).OrderBy(value => value))
            {
                changes.Add("Added " + label + ": " + id);
            }

            foreach (var id in oldValues.Keys.Intersect(newValues.Keys).OrderBy(value => value))
            {
                if (!string.Equals(oldValues[id], newValues[id], StringComparison.Ordinal))
                {
                    changes.Add("Changed " + label + ": " + id);
                }
            }
        }

        private static void RequireFile(string path)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    "World actor content file is missing.", path);
            }
        }

        private static void CopyWorldFile(
            string sourceContentRoot,
            string targetContentRoot,
            string worldId,
            string fileName)
        {
            var target = Path.Combine(
                ContentLayout.WorldDirectory(targetContentRoot, worldId),
                fileName);
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            File.Copy(
                Path.Combine(ContentLayout.WorldDirectory(sourceContentRoot, worldId), fileName),
                target);
        }

        private static void WriteText(string path, string value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, Normalize(value), new UTF8Encoding(false));
        }

        private static string Normalize(string value)
        {
            return ContentFileTransaction.NormalizeLineEndings(value);
        }
    }
}
