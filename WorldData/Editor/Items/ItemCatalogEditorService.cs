using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ShooterMmo.WorldData.Items.Presentation;
using UnityEngine;

namespace ShooterMmo.WorldData.Editor.Items
{
    public sealed class ItemCatalogEditorPaths
    {
        public ItemCatalogEditorPaths(
            string repositoryRoot,
            string authoringPath,
            string runtimePath,
            string presentationPath,
            string compilerProjectPath)
        {
            RepositoryRoot = Path.GetFullPath(repositoryRoot);
            AuthoringPath = Path.GetFullPath(authoringPath);
            RuntimePath = Path.GetFullPath(runtimePath);
            PresentationPath = Path.GetFullPath(presentationPath);
            CompilerProjectPath = Path.GetFullPath(compilerProjectPath);
        }

        public string RepositoryRoot { get; }

        public string AuthoringPath { get; }

        public string RuntimePath { get; }

        public string PresentationPath { get; }

        public string CompilerProjectPath { get; }

        public static ItemCatalogEditorPaths CreateDefault()
        {
            var repositoryRoot = Path.GetFullPath(
                Path.Combine(Application.dataPath, "..", ".."));
            return new ItemCatalogEditorPaths(
                repositoryRoot,
                Path.Combine(
                    repositoryRoot,
                    "WorldData",
                    "Shared",
                    "Authoring",
                    "Items",
                    "core.item-catalog.json"),
                Path.Combine(
                    repositoryRoot,
                    "WorldData",
                    "Shared",
                    "Runtime",
                    "Items",
                    "core.item-catalog.json"),
                Path.Combine(
                    Application.dataPath,
                    "Resources",
                    "Items",
                    "Presentation",
                    "item-presentation-catalog.json"),
                Path.Combine(
                    repositoryRoot,
                    "Tools",
                    "ItemCatalogCompiler",
                    "ItemCatalogCompiler.csproj"));
        }
    }

    public sealed class ItemCatalogEditorValidationResult
    {
        private ItemCatalogEditorValidationResult(
            bool success,
            string message,
            string authoringJson,
            string runtimeJson,
            string presentationJson,
            ItemCatalogEditorRuntimeDocument candidateRuntime,
            ItemPresentationCatalogDocument candidatePresentation,
            ItemCatalogDefinitionChange[] changes)
        {
            Success = success;
            Message = message ?? string.Empty;
            AuthoringJson = authoringJson;
            RuntimeJson = runtimeJson;
            PresentationJson = presentationJson;
            CandidateRuntime = candidateRuntime;
            CandidatePresentation = candidatePresentation;
            Changes = changes ?? Array.Empty<ItemCatalogDefinitionChange>();
        }

        public bool Success { get; }

        public string Message { get; }

        public string AuthoringJson { get; }

        public string RuntimeJson { get; }

        public string PresentationJson { get; }

        public ItemCatalogEditorRuntimeDocument CandidateRuntime { get; }

        public ItemPresentationCatalogDocument CandidatePresentation { get; }

        public ItemCatalogDefinitionChange[] Changes { get; }

        public bool RequiresStructuralConfirmation => Changes.Any(change =>
            change.Kind == ItemCatalogDefinitionChangeKind.Structural
            || change.Kind == ItemCatalogDefinitionChangeKind.Removed);

        public static ItemCatalogEditorValidationResult Failed(string message)
        {
            return new ItemCatalogEditorValidationResult(
                false,
                message,
                null,
                null,
                null,
                null,
                null,
                null);
        }

        public static ItemCatalogEditorValidationResult Passed(
            string message,
            string authoringJson,
            string runtimeJson,
            string presentationJson,
            ItemCatalogEditorRuntimeDocument candidateRuntime,
            ItemPresentationCatalogDocument candidatePresentation,
            ItemCatalogDefinitionChange[] changes)
        {
            return new ItemCatalogEditorValidationResult(
                true,
                message,
                authoringJson,
                runtimeJson,
                presentationJson,
                candidateRuntime,
                candidatePresentation,
                changes);
        }
    }

    public sealed class ItemCatalogEditorService
    {
        private readonly ItemCatalogEditorPaths paths;
        private readonly IItemCatalogCompilerRunner compilerRunner;

        public ItemCatalogEditorService(
            ItemCatalogEditorPaths paths,
            IItemCatalogCompilerRunner compilerRunner = null)
        {
            this.paths = paths ?? throw new ArgumentNullException(nameof(paths));
            this.compilerRunner = compilerRunner ?? new ItemCatalogCompilerProcessRunner(
                paths.RepositoryRoot,
                paths.CompilerProjectPath);
        }

        public ItemCatalogEditorPaths Paths => paths;

        public ItemCatalogEditorWorkspace Load()
        {
            RequireFile(paths.AuthoringPath, "authoring catalog");
            RequireFile(paths.RuntimePath, "runtime catalog");

            ValidateCanonicalAuthoringBeforeLoad();
            var authoring = ItemCatalogEditorJson.DeserializeAuthoring(
                File.ReadAllText(paths.AuthoringPath));
            var runtime = ItemCatalogEditorJson.DeserializeRuntime(
                File.ReadAllText(paths.RuntimePath));
            var presentation = File.Exists(paths.PresentationPath)
                ? ItemPresentationCatalogJson.Deserialize(
                    File.ReadAllText(paths.PresentationPath))
                : new ItemPresentationCatalogDocument
                {
                    formatVersion = ItemPresentationCatalogFormat.Version,
                    sourceCatalogRevision = runtime.revision,
                    presentationRevision = string.Empty,
                    entries = Array.Empty<ItemPresentationEntry>()
                };

            return new ItemCatalogEditorWorkspace(authoring, runtime, presentation);
        }

        public ItemCatalogEditorValidationResult Validate(
            ItemCatalogEditorWorkspace workspace)
        {
            if (workspace == null)
            {
                throw new ArgumentNullException(nameof(workspace));
            }

            var temporaryDirectory = CreateTemporaryDirectory();
            try
            {
                var authoringJson = ItemCatalogEditorJson.SerializeAuthoring(
                    workspace.Authoring);
                var temporaryAuthoringPath = Path.Combine(
                    temporaryDirectory,
                    "candidate.item-catalog.json");
                var temporaryRuntimePath = Path.Combine(
                    temporaryDirectory,
                    "candidate.runtime.json");
                WriteText(temporaryAuthoringPath, authoringJson);

                var compilerResult = compilerRunner.Compile(
                    temporaryAuthoringPath,
                    temporaryRuntimePath);
                if (!compilerResult.Success)
                {
                    return ItemCatalogEditorValidationResult.Failed(compilerResult.Output);
                }

                if (!File.Exists(temporaryRuntimePath))
                {
                    return ItemCatalogEditorValidationResult.Failed(
                        "ItemCatalogCompiler did not produce candidate runtime JSON.");
                }

                var runtimeJson = NormalizeLineEndings(File.ReadAllText(temporaryRuntimePath));
                var candidateRuntime = ItemCatalogEditorJson.DeserializeRuntime(runtimeJson);
                ItemPresentationCatalogDocument candidatePresentation;
                try
                {
                    candidatePresentation = ItemPresentationCatalogCompiler.Compile(
                        candidateRuntime.revision,
                        candidateRuntime.definitions.Select(definition => definition.id),
                        workspace.Presentation.entries);
                }
                catch (ItemPresentationCatalogValidationException exception)
                {
                    return ItemCatalogEditorValidationResult.Failed(exception.Message);
                }

                var presentationJson = ItemPresentationCatalogJson.Serialize(
                    candidatePresentation);
                var changes = ItemCatalogChangeClassifier.Compare(
                    workspace.CurrentRuntime,
                    candidateRuntime);
                var message = BuildSuccessMessage(
                    compilerResult.Output,
                    candidateRuntime,
                    candidatePresentation,
                    changes);
                return ItemCatalogEditorValidationResult.Passed(
                    message,
                    authoringJson,
                    runtimeJson,
                    presentationJson,
                    candidateRuntime,
                    candidatePresentation,
                    changes);
            }
            catch (Exception exception)
            {
                return ItemCatalogEditorValidationResult.Failed(exception.Message);
            }
            finally
            {
                DeleteTemporaryDirectory(temporaryDirectory);
            }
        }

        public void Save(
            ItemCatalogEditorWorkspace workspace,
            ItemCatalogEditorValidationResult validation)
        {
            RequireSuccessfulValidation(validation);
            CommitFiles(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [paths.AuthoringPath] = validation.AuthoringJson,
                [paths.PresentationPath] = validation.PresentationJson
            });
            ApplyPresentation(workspace, validation.CandidatePresentation);
        }

        public void SaveAndBake(
            ItemCatalogEditorWorkspace workspace,
            ItemCatalogEditorValidationResult validation)
        {
            RequireSuccessfulValidation(validation);
            CommitFiles(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [paths.AuthoringPath] = validation.AuthoringJson,
                [paths.RuntimePath] = validation.RuntimeJson,
                [paths.PresentationPath] = validation.PresentationJson
            });
            ApplyRuntime(workspace, validation.CandidateRuntime);
            ApplyPresentation(workspace, validation.CandidatePresentation);
        }

        private void ValidateCanonicalAuthoringBeforeLoad()
        {
            var temporaryDirectory = CreateTemporaryDirectory();
            try
            {
                var temporaryRuntimePath = Path.Combine(
                    temporaryDirectory,
                    "load-validation.runtime.json");
                var result = compilerRunner.Compile(paths.AuthoringPath, temporaryRuntimePath);
                if (!result.Success)
                {
                    throw new InvalidDataException(
                        "Canonical item catalog cannot be loaded: " + result.Output);
                }
            }
            finally
            {
                DeleteTemporaryDirectory(temporaryDirectory);
            }
        }

        private static void ApplyRuntime(
            ItemCatalogEditorWorkspace workspace,
            ItemCatalogEditorRuntimeDocument candidate)
        {
            workspace.ApplyBakedRuntime(candidate);
        }

        private static void ApplyPresentation(
            ItemCatalogEditorWorkspace workspace,
            ItemPresentationCatalogDocument candidate)
        {
            workspace.Presentation.formatVersion = candidate.formatVersion;
            workspace.Presentation.sourceCatalogRevision = candidate.sourceCatalogRevision;
            workspace.Presentation.presentationRevision = candidate.presentationRevision;
            workspace.Presentation.entries = candidate.entries;
        }

        private static void RequireFile(string path, string label)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("Item catalog " + label + " is missing.", path);
            }
        }

        private static void RequireSuccessfulValidation(
            ItemCatalogEditorValidationResult validation)
        {
            if (validation == null || !validation.Success)
            {
                throw new InvalidOperationException(
                    "A successful validation result is required before writing item catalog files.");
            }
        }

        private static string BuildSuccessMessage(
            string compilerOutput,
            ItemCatalogEditorRuntimeDocument runtime,
            ItemPresentationCatalogDocument presentation,
            ItemCatalogDefinitionChange[] changes)
        {
            var builder = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(compilerOutput))
            {
                builder.AppendLine(compilerOutput.Trim());
            }

            builder.Append("Presentation revision ")
                .Append(presentation.presentationRevision)
                .Append(" covers ")
                .Append(presentation.entries.Length)
                .Append(" definitions for gameplay revision ")
                .Append(runtime.revision)
                .AppendLine(".");
            if (changes.Length == 0)
            {
                builder.Append("No gameplay definition changes detected.");
                return builder.ToString();
            }

            builder.AppendLine("Definition changes:");
            foreach (var change in changes)
            {
                builder.Append("- ")
                    .Append(change.DefinitionId)
                    .Append(": ")
                    .AppendLine(change.Kind.ToString());
            }

            return builder.ToString().TrimEnd();
        }

        private static void CommitFiles(IReadOnlyDictionary<string, string> contents)
        {
            var snapshots = contents.Keys.ToDictionary(
                path => path,
                path => File.Exists(path) ? File.ReadAllText(path) : null,
                StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var pair in contents)
                {
                    AtomicWriteText(pair.Key, pair.Value);
                }
            }
            catch
            {
                foreach (var snapshot in snapshots)
                {
                    if (snapshot.Value == null)
                    {
                        if (File.Exists(snapshot.Key))
                        {
                            File.Delete(snapshot.Key);
                        }

                        continue;
                    }

                    AtomicWriteText(snapshot.Key, snapshot.Value);
                }

                throw;
            }
        }

        private static void AtomicWriteText(string path, string content)
        {
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new InvalidOperationException("Output path has no directory: " + path);
            }

            Directory.CreateDirectory(directory);
            var temporaryPath = Path.Combine(
                directory,
                "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            var backupPath = temporaryPath + ".bak";
            try
            {
                WriteText(temporaryPath, content);
                if (File.Exists(path))
                {
                    File.Replace(temporaryPath, path, backupPath);
                    File.Delete(backupPath);
                }
                else
                {
                    File.Move(temporaryPath, path);
                }
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }

                if (File.Exists(backupPath))
                {
                    File.Delete(backupPath);
                }
            }
        }

        private static string CreateTemporaryDirectory()
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "ShooterMmoItemCatalogEditor",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            return directory;
        }

        private static void DeleteTemporaryDirectory(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return;
            }

            var expectedRoot = Path.GetFullPath(Path.Combine(
                Path.GetTempPath(),
                "ShooterMmoItemCatalogEditor"));
            var resolved = Path.GetFullPath(directory);
            if (!resolved.StartsWith(expectedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Refusing to delete unexpected item catalog temporary directory.");
            }

            Directory.Delete(resolved, true);
        }

        private static void WriteText(string path, string content)
        {
            File.WriteAllText(path, NormalizeLineEndings(content), new UTF8Encoding(false));
        }

        private static string NormalizeLineEndings(string value)
        {
            return (value ?? string.Empty).Replace("\r\n", "\n").Replace("\r", "\n");
        }
    }
}
