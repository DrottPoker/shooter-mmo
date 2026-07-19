using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ShooterMmo.WorldData.Actors;
using UnityEngine;

namespace ShooterMmo.WorldData.Editor.Actors
{
    public sealed class WorldActorEditorPaths
    {
        public WorldActorEditorPaths(
            string repositoryRoot,
            string catalogPath,
            string spawnPath,
            string runtimePath,
            string compilerProjectPath)
        {
            RepositoryRoot = Path.GetFullPath(repositoryRoot);
            CatalogPath = Path.GetFullPath(catalogPath);
            SpawnPath = Path.GetFullPath(spawnPath);
            RuntimePath = Path.GetFullPath(runtimePath);
            CompilerProjectPath = Path.GetFullPath(compilerProjectPath);
        }

        public string RepositoryRoot { get; }
        public string CatalogPath { get; }
        public string SpawnPath { get; }
        public string RuntimePath { get; }
        public string CompilerProjectPath { get; }

        public static WorldActorEditorPaths CreateDefault()
        {
            var repositoryRoot = Path.GetFullPath(
                Path.Combine(Application.dataPath, "..", ".."));
            return new WorldActorEditorPaths(
                repositoryRoot,
                Path.Combine(
                    repositoryRoot,
                    "WorldData",
                    "Shared",
                    "Authoring",
                    "Actors",
                    "core.world-actors.json"),
                Path.Combine(
                    repositoryRoot,
                    "WorldData",
                    "Worlds",
                    "development-world-1",
                    "Authoring",
                    "actor-spawns.json"),
                Path.Combine(
                    repositoryRoot,
                    "WorldData",
                    "Worlds",
                    "development-world-1",
                    "Runtime",
                    "world-actors.json"),
                Path.Combine(
                    repositoryRoot,
                    "Tools",
                    "WorldActorCompiler",
                    "WorldActorCompiler.csproj"));
        }
    }

    public sealed class WorldActorEditorWorkspace
    {
        public WorldActorEditorWorkspace(
            WorldActorEditorCatalog catalog,
            WorldActorEditorSpawns spawns,
            WorldActorEditorRuntime runtime)
        {
            Catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            Spawns = spawns ?? throw new ArgumentNullException(nameof(spawns));
            CurrentRuntime = runtime
                ?? throw new ArgumentNullException(nameof(runtime));
        }

        public WorldActorEditorCatalog Catalog { get; }
        public WorldActorEditorSpawns Spawns { get; }
        public WorldActorEditorRuntime CurrentRuntime { get; private set; }

        public void ApplyRuntime(WorldActorEditorRuntime runtime)
        {
            CurrentRuntime = runtime
                ?? throw new ArgumentNullException(nameof(runtime));
        }
    }

    public sealed class WorldActorCompilerRunResult
    {
        public WorldActorCompilerRunResult(bool success, string output)
        {
            Success = success;
            Output = output ?? string.Empty;
        }

        public bool Success { get; }
        public string Output { get; }
    }

    public interface IWorldActorCompilerRunner
    {
        WorldActorCompilerRunResult Compile(
            string catalogPath,
            string spawnPath,
            string runtimePath);

        WorldActorCompilerRunResult Verify(
            string catalogPath,
            string spawnPath,
            string runtimePath);
    }

    public sealed class WorldActorCompilerProcessRunner : IWorldActorCompilerRunner
    {
        private const int TimeoutMilliseconds = 120000;
        private readonly string repositoryRoot;
        private readonly string projectPath;

        public WorldActorCompilerProcessRunner(
            string repositoryRoot,
            string projectPath)
        {
            this.repositoryRoot = Path.GetFullPath(repositoryRoot);
            this.projectPath = Path.GetFullPath(projectPath);
        }

        public WorldActorCompilerRunResult Compile(
            string catalogPath,
            string spawnPath,
            string runtimePath)
        {
            return Run(catalogPath, spawnPath, runtimePath, false);
        }

        public WorldActorCompilerRunResult Verify(
            string catalogPath,
            string spawnPath,
            string runtimePath)
        {
            return Run(catalogPath, spawnPath, runtimePath, true);
        }

        private WorldActorCompilerRunResult Run(
            string catalogPath,
            string spawnPath,
            string runtimePath,
            bool verifyOnly)
        {
            if (!File.Exists(projectPath))
            {
                return new WorldActorCompilerRunResult(
                    false,
                    "WorldActorCompiler project was not found at '" + projectPath + "'.");
            }

            var arguments = "run --project " + Quote(projectPath)
                + " --configuration Release -- "
                + Quote(Path.GetFullPath(catalogPath)) + " "
                + Quote(Path.GetFullPath(spawnPath)) + " "
                + Quote(Path.GetFullPath(runtimePath))
                + (verifyOnly ? " --verify" : string.Empty);
            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = arguments,
                WorkingDirectory = repositoryRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            try
            {
                using (var process = Process.Start(startInfo))
                {
                    if (process == null)
                    {
                        return new WorldActorCompilerRunResult(
                            false,
                            "Could not start the world actor compiler.");
                    }

                    var outputTask = process.StandardOutput.ReadToEndAsync();
                    var errorTask = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(TimeoutMilliseconds))
                    {
                        TryKill(process);
                        return new WorldActorCompilerRunResult(
                            false,
                            "World actor compilation exceeded the two-minute timeout.");
                    }

                    Task.WaitAll(new Task[] { outputTask, errorTask });
                    var output = Combine(outputTask.Result, errorTask.Result);
                    return new WorldActorCompilerRunResult(process.ExitCode == 0, output);
                }
            }
            catch (Exception exception)
            {
                return new WorldActorCompilerRunResult(
                    false,
                    "Could not run the world actor compiler: " + exception.Message);
            }
        }

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private static string Combine(string output, string error)
        {
            var cleanOutput = (output ?? string.Empty).Trim();
            var cleanError = (error ?? string.Empty).Trim();
            if (cleanOutput.Length == 0)
            {
                return cleanError;
            }

            return cleanError.Length == 0
                ? cleanOutput
                : cleanOutput + Environment.NewLine + cleanError;
        }

        private static void TryKill(Process process)
        {
            try
            {
                process.Kill();
            }
            catch
            {
                // The process may have exited before timeout cleanup completed.
            }
        }
    }

    public sealed class WorldActorEditorValidationResult
    {
        private WorldActorEditorValidationResult(
            bool success,
            string message,
            string catalogJson,
            string spawnJson,
            string runtimeJson,
            WorldActorRuntimeDocument candidate,
            string[] changes)
        {
            Success = success;
            Message = message ?? string.Empty;
            CatalogJson = catalogJson;
            SpawnJson = spawnJson;
            RuntimeJson = runtimeJson;
            Candidate = candidate;
            Changes = changes ?? Array.Empty<string>();
        }

        public bool Success { get; }
        public string Message { get; }
        public string CatalogJson { get; }
        public string SpawnJson { get; }
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
            string catalogJson,
            string spawnJson,
            string runtimeJson,
            WorldActorRuntimeDocument candidate,
            string[] changes)
        {
            return new WorldActorEditorValidationResult(
                true,
                message,
                catalogJson,
                spawnJson,
                runtimeJson,
                candidate,
                changes);
        }
    }

    public sealed class WorldActorEditorService
    {
        private readonly WorldActorEditorPaths paths;
        private readonly IWorldActorCompilerRunner compilerRunner;

        public WorldActorEditorService(
            WorldActorEditorPaths paths,
            IWorldActorCompilerRunner compilerRunner = null)
        {
            this.paths = paths ?? throw new ArgumentNullException(nameof(paths));
            this.compilerRunner = compilerRunner ?? new WorldActorCompilerProcessRunner(
                paths.RepositoryRoot,
                paths.CompilerProjectPath);
        }

        public WorldActorEditorPaths Paths => paths;

        public WorldActorEditorWorkspace Load()
        {
            RequireFile(paths.CatalogPath);
            RequireFile(paths.SpawnPath);
            RequireFile(paths.RuntimePath);
            return new WorldActorEditorWorkspace(
                WorldActorEditorJson.DeserializeCatalog(
                    File.ReadAllText(paths.CatalogPath)),
                WorldActorEditorJson.DeserializeSpawns(
                    File.ReadAllText(paths.SpawnPath)),
                WorldActorEditorJson.DeserializeRuntime(
                    File.ReadAllText(paths.RuntimePath)));
        }

        public WorldActorEditorValidationResult Validate(
            WorldActorEditorWorkspace workspace)
        {
            if (workspace == null)
            {
                throw new ArgumentNullException(nameof(workspace));
            }

            var temporaryDirectory = Path.Combine(
                Path.GetTempPath(),
                "ShooterMmo.WorldActors." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryDirectory);
            try
            {
                var catalogJson = WorldActorEditorJson.SerializeCatalog(workspace.Catalog);
                var spawnJson = WorldActorEditorJson.SerializeSpawns(workspace.Spawns);
                var candidate = WorldActorCompiler.Compile(
                    workspace.Catalog.ToDomain(),
                    workspace.Spawns.ToDomain());
                var catalogPath = Path.Combine(temporaryDirectory, "actors.json");
                var spawnPath = Path.Combine(temporaryDirectory, "spawns.json");
                var runtimePath = Path.Combine(temporaryDirectory, "runtime.json");
                WriteText(catalogPath, catalogJson);
                WriteText(spawnPath, spawnJson);
                var compilerResult = compilerRunner.Compile(
                    catalogPath,
                    spawnPath,
                    runtimePath);
                if (!compilerResult.Success || !File.Exists(runtimePath))
                {
                    return WorldActorEditorValidationResult.Failed(
                        compilerResult.Output.Length == 0
                            ? "WorldActorCompiler did not produce runtime JSON."
                            : compilerResult.Output);
                }

                var runtimeJson = Normalize(File.ReadAllText(runtimePath));
                var parsedRuntime = WorldActorEditorJson.DeserializeRuntime(runtimeJson);
                if (!string.Equals(
                        candidate.Revision,
                        parsedRuntime.revision,
                        StringComparison.Ordinal))
                {
                    return WorldActorEditorValidationResult.Failed(
                        "Editor validation and WorldActorCompiler revisions differ.");
                }

                var changes = ClassifyChanges(workspace.CurrentRuntime, candidate);
                var message = compilerResult.Output.Trim();
                if (changes.Length == 0)
                {
                    message += Environment.NewLine + "No runtime actor changes detected.";
                }
                else
                {
                    message += Environment.NewLine + string.Join(Environment.NewLine, changes);
                }

                return WorldActorEditorValidationResult.Passed(
                    message.Trim(),
                    catalogJson,
                    spawnJson,
                    runtimeJson,
                    candidate,
                    changes);
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

        public void SaveAndCompile(
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

            WriteText(paths.CatalogPath, validation.CatalogJson);
            WriteText(paths.SpawnPath, validation.SpawnJson);
            WriteText(paths.RuntimePath, validation.RuntimeJson);
            workspace.ApplyRuntime(
                WorldActorEditorJson.DeserializeRuntime(validation.RuntimeJson));
        }

        public WorldActorCompilerRunResult VerifyCanonical()
        {
            return compilerRunner.Verify(
                paths.CatalogPath,
                paths.SpawnPath,
                paths.RuntimePath);
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

        private static void WriteText(string path, string value)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, Normalize(value));
        }

        private static string Normalize(string value)
        {
            return value.Replace("\r\n", "\n").Replace("\r", "\n");
        }
    }
}
