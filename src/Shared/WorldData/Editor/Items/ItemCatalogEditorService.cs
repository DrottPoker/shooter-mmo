using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Mmorpg.WorldData.Content;
using Mmorpg.WorldData.Editor.ContentTools;
using Mmorpg.WorldData.Items.Presentation;
using Mmorpg.WorldData.Text;
using UnityEngine;

namespace Mmorpg.WorldData.Editor.Items
{
    public sealed class ItemCatalogEditorPaths
    {
        public ItemCatalogEditorPaths(
            string repositoryRoot,
            string contentRoot,
            string generatedRoot,
            string presentationPath)
        {
            RepositoryRoot = Path.GetFullPath(repositoryRoot);
            ContentRoot = Path.GetFullPath(contentRoot);
            GeneratedRoot = Path.GetFullPath(generatedRoot);
            PresentationPath = Path.GetFullPath(presentationPath);
        }

        public string RepositoryRoot { get; }

        public string ContentRoot { get; }

        public string GeneratedRoot { get; }

        public string PresentationPath { get; }

        public string ItemsDirectory => ContentLayout.ItemsDirectory(ContentRoot);

        public string RuntimePath => ContentLayout.GeneratedItemCatalogPath(GeneratedRoot);

        public static ItemCatalogEditorPaths CreateDefault()
        {
            var repositoryRoot = RepositoryLayout.FindRoot();
            return new ItemCatalogEditorPaths(
                repositoryRoot,
                RepositoryLayout.ContentRoot(repositoryRoot),
                RepositoryLayout.GeneratedRoot(repositoryRoot),
                Path.Combine(
                    Application.dataPath,
                    "Content",
                    "Items",
                    "Presentation",
                    "item-presentation-catalog.json"));
        }
    }

    public sealed class ItemCatalogEditorValidationResult
    {
        private ItemCatalogEditorValidationResult(
            bool success,
            string message,
            IReadOnlyDictionary<string, string> authoringFiles,
            string runtimeJson,
            string textTableJson,
            string presentationJson,
            ItemCatalogEditorRuntimeDocument candidateRuntime,
            ItemPresentationCatalogDocument candidatePresentation,
            ItemCatalogDefinitionChange[] changes)
        {
            Success = success;
            Message = message ?? string.Empty;
            AuthoringFiles = authoringFiles;
            RuntimeJson = runtimeJson;
            TextTableJson = textTableJson;
            PresentationJson = presentationJson;
            CandidateRuntime = candidateRuntime;
            CandidatePresentation = candidatePresentation;
            Changes = changes ?? Array.Empty<ItemCatalogDefinitionChange>();
        }

        public bool Success { get; }

        public string Message { get; }

        // Item content file text keyed by path relative to the items directory.
        public IReadOnlyDictionary<string, string> AuthoringFiles { get; }

        public string RuntimeJson { get; }

        // The item text table that the compiler wrote with the runtime catalog. It is baked
        // with the runtime catalog, so the two never drift apart.
        public string TextTableJson { get; }

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
                null,
                null);
        }

        public static ItemCatalogEditorValidationResult Passed(
            string message,
            IReadOnlyDictionary<string, string> authoringFiles,
            string runtimeJson,
            string textTableJson,
            string presentationJson,
            ItemCatalogEditorRuntimeDocument candidateRuntime,
            ItemPresentationCatalogDocument candidatePresentation,
            ItemCatalogDefinitionChange[] changes)
        {
            return new ItemCatalogEditorValidationResult(
                true,
                message,
                authoringFiles,
                runtimeJson,
                textTableJson,
                presentationJson,
                candidateRuntime,
                candidatePresentation,
                changes);
        }
    }

    public sealed class ItemCatalogEditorService
    {
        public const string CompilerToolName = "ItemCatalogCompiler";

        private readonly ItemCatalogEditorPaths paths;
        private readonly IContentCompilerRunner compilerRunner;

        public ItemCatalogEditorService(
            ItemCatalogEditorPaths paths,
            IContentCompilerRunner compilerRunner = null)
        {
            this.paths = paths ?? throw new ArgumentNullException(nameof(paths));
            this.compilerRunner = compilerRunner ?? new ContentCompilerProcessRunner(
                paths.RepositoryRoot,
                CompilerToolName);
        }

        public ItemCatalogEditorPaths Paths => paths;

        // The item files are read before their content, so a file that changes in between makes
        // the next save stop instead of writing over it.
        public ItemCatalogEditorWorkspace Load()
        {
            RequireFile(paths.RuntimePath, "runtime catalog");

            ValidateCanonicalAuthoringBeforeLoad();
            var diskState = ReadDiskState();
            var authoring = ItemCatalogEditorContent.Load(paths.ItemsDirectory);
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

            return new ItemCatalogEditorWorkspace(authoring, runtime, presentation, diskState);
        }

        // Item files that changed on disk since the workspace read them stop the validation,
        // because the save would delete or write over them.
        public ItemCatalogEditorValidationResult Validate(
            ItemCatalogEditorWorkspace workspace)
        {
            if (workspace == null)
            {
                throw new ArgumentNullException(nameof(workspace));
            }

            var changedOnDisk = workspace.DiskState?.Changes() ?? Array.Empty<string>();
            if (changedOnDisk.Count > 0)
            {
                return ItemCatalogEditorValidationResult.Failed(
                    ContentDiskState.ChangedMessage(changedOnDisk, paths.ContentRoot, "the item catalog editor"));
            }

            var temporaryDirectory = CreateTemporaryDirectory();
            try
            {
                var authoringFiles = ItemCatalogEditorContent.CreateFiles(workspace.Authoring);
                var contentRoot = Path.Combine(temporaryDirectory, "content");
                var generatedRoot = Path.Combine(temporaryDirectory, "generated");
                var itemsDirectory = ContentLayout.ItemsDirectory(contentRoot);
                foreach (var file in authoringFiles)
                {
                    WriteText(Path.Combine(itemsDirectory, file.Key), file.Value);
                }

                var compilerResult = compilerRunner.Compile(contentRoot, generatedRoot);
                if (!compilerResult.Success)
                {
                    return ItemCatalogEditorValidationResult.Failed(compilerResult.Output);
                }

                var runtimePath = ContentLayout.GeneratedItemCatalogPath(generatedRoot);
                if (!File.Exists(runtimePath))
                {
                    return ItemCatalogEditorValidationResult.Failed(
                        CompilerToolName + " did not produce candidate runtime JSON.");
                }

                var textTablePath = TextTablePath(generatedRoot);
                if (!File.Exists(textTablePath))
                {
                    return ItemCatalogEditorValidationResult.Failed(
                        CompilerToolName + " did not produce the item text table.");
                }

                var runtimeJson = NormalizeLineEndings(File.ReadAllText(runtimePath));
                var textTableJson = NormalizeLineEndings(File.ReadAllText(textTablePath));
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
                    authoringFiles,
                    runtimeJson,
                    textTableJson,
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
            Commit(workspace, validation, false);
            ApplyPresentation(workspace, validation.CandidatePresentation);
        }

        public void SaveAndBake(
            ItemCatalogEditorWorkspace workspace,
            ItemCatalogEditorValidationResult validation)
        {
            RequireSuccessfulValidation(validation);
            Commit(workspace, validation, true);
            ApplyRuntime(workspace, validation.CandidateRuntime);
            ApplyPresentation(workspace, validation.CandidatePresentation);
        }

        private ContentDiskState ReadDiskState()
        {
            return ContentDiskState.Read(new[] { paths.ItemsDirectory });
        }

        private static string TextTablePath(string generatedRoot)
        {
            return ContentLayout.GeneratedTextTablePath(
                generatedRoot,
                TextTableFormat.SourceLocale,
                TextTableIds.Items);
        }

        // Writes authored files first and generated files last, then deletes definition
        // files for drafts that were removed and files of removed vendor profiles. A failure
        // restores every file. Item files that changed on disk since the workspace read them
        // stop it before anything is written.
        private void Commit(
            ItemCatalogEditorWorkspace workspace,
            ItemCatalogEditorValidationResult validation,
            bool includeRuntime)
        {
            var changedOnDisk = workspace?.DiskState?.Changes() ?? Array.Empty<string>();
            if (changedOnDisk.Count > 0)
            {
                throw new InvalidOperationException(
                    ContentDiskState.ChangedMessage(changedOnDisk, paths.ContentRoot, "the item catalog editor"));
            }

            var writes = validation.AuthoringFiles
                .OrderBy(file => file.Key, StringComparer.Ordinal)
                .Select(file => new KeyValuePair<string, string>(
                    Path.GetFullPath(Path.Combine(paths.ItemsDirectory, file.Key)),
                    file.Value))
                .ToList();
            if (includeRuntime)
            {
                writes.Add(new KeyValuePair<string, string>(
                    paths.RuntimePath,
                    validation.RuntimeJson));
                writes.Add(new KeyValuePair<string, string>(
                    TextTablePath(paths.GeneratedRoot),
                    validation.TextTableJson));
            }

            writes.Add(new KeyValuePair<string, string>(
                paths.PresentationPath,
                validation.PresentationJson));
            var written = new HashSet<string>(
                writes.Select(write => write.Key),
                StringComparer.OrdinalIgnoreCase);
            var removed = ExistingDefinitionPaths()
                .Concat(ContentLayout.ListVendorFiles(paths.ItemsDirectory)
                    .Select(file => Path.GetFullPath(file.Path)))
                .Where(path => !written.Contains(path))
                .ToArray();
            ContentFileTransaction.Commit(writes, removed);
            workspace?.MarkFilesWritten(ReadDiskState());
        }

        private IEnumerable<string> ExistingDefinitionPaths()
        {
            if (!Directory.Exists(ContentLayout.DefinitionsDirectory(paths.ItemsDirectory)))
            {
                return Enumerable.Empty<string>();
            }

            return ContentLayout.ListDefinitionFiles(paths.ItemsDirectory)
                .Select(file => Path.GetFullPath(file.Path));
        }

        private void ValidateCanonicalAuthoringBeforeLoad()
        {
            var temporaryDirectory = CreateTemporaryDirectory();
            try
            {
                var result = compilerRunner.Compile(paths.ContentRoot, temporaryDirectory);
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

        private static string CreateTemporaryDirectory()
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "MmorpgItemCatalogEditor",
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
                "MmorpgItemCatalogEditor"));
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
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, NormalizeLineEndings(content), new UTF8Encoding(false));
        }

        private static string NormalizeLineEndings(string value)
        {
            return ContentFileTransaction.NormalizeLineEndings(value);
        }
    }
}
