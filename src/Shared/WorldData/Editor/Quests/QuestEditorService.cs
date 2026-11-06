using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Mmorpg.WorldData.Content;
using Mmorpg.WorldData.Quests;
using UnityEngine;

namespace Mmorpg.WorldData.Editor.Quests
{
    public sealed class QuestEditorPaths
    {
        public QuestEditorPaths(string repositoryRoot, string contentRoot, string generatedRoot)
        {
            RepositoryRoot = Path.GetFullPath(repositoryRoot);
            ContentRoot = Path.GetFullPath(contentRoot);
            GeneratedRoot = Path.GetFullPath(generatedRoot);
        }

        public string RepositoryRoot { get; }
        public string ContentRoot { get; }
        public string GeneratedRoot { get; }
        public string QuestsDirectory => ContentLayout.QuestsDirectory(ContentRoot);
        public string RuntimePath => ContentLayout.GeneratedQuestCatalogPath(GeneratedRoot);

        public static QuestEditorPaths CreateDefault()
        {
            var repositoryRoot = RepositoryLayout.FindRoot();
            return new QuestEditorPaths(
                repositoryRoot,
                RepositoryLayout.ContentRoot(repositoryRoot),
                RepositoryLayout.GeneratedRoot(repositoryRoot));
        }
    }

    public sealed class QuestEditorWorkspace
    {
        public QuestEditorWorkspace(QuestEditorCatalog catalog, QuestEditorReferences references)
        {
            Catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            References = references ?? throw new ArgumentNullException(nameof(references));
        }

        public QuestEditorCatalog Catalog { get; }
        public QuestEditorReferences References { get; private set; }

        // Replaces what quest content may name, after the item or actor compiler wrote again.
        public void RefreshReferences(QuestEditorReferences references)
        {
            References = references ?? throw new ArgumentNullException(nameof(references));
        }

        // Replaces the editable content with a snapshot taken earlier, for undo and redo.
        public void RestoreContent(QuestEditorCatalog catalog)
        {
            if (catalog == null)
            {
                throw new ArgumentNullException(nameof(catalog));
            }

            Catalog.formatVersion = catalog.formatVersion;
            Catalog.catalogId = catalog.catalogId;
            Catalog.maximumActiveQuests = catalog.maximumActiveQuests;
            Catalog.retiredQuestIds = catalog.retiredQuestIds;
            Catalog.quests = catalog.quests;
        }
    }

    // One file a successful validation would write, with its canonical text.
    public sealed class QuestEditorFile
    {
        public QuestEditorFile(string displayPath, string fullPath, string text, bool changed)
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

    public sealed class QuestEditorValidationResult
    {
        private QuestEditorValidationResult(
            bool success,
            string message,
            QuestEditorFile[] files,
            string[] removedFiles,
            string[] changes)
        {
            Success = success;
            Message = message ?? string.Empty;
            Files = files ?? Array.Empty<QuestEditorFile>();
            RemovedFiles = removedFiles ?? Array.Empty<string>();
            Changes = changes ?? Array.Empty<string>();
        }

        public bool Success { get; }
        public string Message { get; }

        // The authored quest files and the compiled catalog in their canonical form, changed or not.
        public QuestEditorFile[] Files { get; }

        // Definition files of quests that left the catalog.
        public string[] RemovedFiles { get; }

        // Such as "Added quest: quest.new_hunt", for the save confirmation.
        public string[] Changes { get; }

        public static QuestEditorValidationResult Failed(string message)
        {
            return new QuestEditorValidationResult(false, message, null, null, null);
        }

        public static QuestEditorValidationResult Passed(string message, QuestEditorFile[] files, string[] removedFiles, string[] changes)
        {
            return new QuestEditorValidationResult(true, message, files, removedFiles, changes);
        }
    }

    // Loads quest content, validates it with the shared compiler, and saves it through the quest
    // compiler process, so the Editor never writes content that the command line rejects.
    public sealed class QuestEditorService
    {
        public const string CompilerToolName = "QuestCatalogCompiler";

        private readonly QuestEditorPaths paths;
        private readonly IContentCompilerRunner compilerRunner;

        public QuestEditorService(QuestEditorPaths paths, IContentCompilerRunner compilerRunner = null)
        {
            this.paths = paths ?? throw new ArgumentNullException(nameof(paths));
            this.compilerRunner = compilerRunner ?? new ContentCompilerProcessRunner(paths.RepositoryRoot, CompilerToolName);
        }

        public QuestEditorPaths Paths => paths;

        public QuestEditorWorkspace Load()
        {
            return new QuestEditorWorkspace(
                QuestEditorContent.Load(paths.QuestsDirectory),
                QuestEditorReferences.Load(paths.ContentRoot, paths.GeneratedRoot));
        }

        // Compiles the edited quests with the quest compiler process against the committed
        // Worlds and the compiled items and actors, in a temporary copy.
        public QuestEditorValidationResult Validate(QuestEditorWorkspace workspace)
        {
            if (workspace == null)
            {
                throw new ArgumentNullException(nameof(workspace));
            }

            var temporaryDirectory = Path.Combine(Path.GetTempPath(), "Mmorpg.QuestStudio." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryDirectory);
            try
            {
                QuestCatalogCompiler.Compile(
                    workspace.Catalog.ToDomain(),
                    workspace.References.Items,
                    workspace.References.Worlds);
                var contentRoot = Path.Combine(temporaryDirectory, "content");
                var generatedRoot = Path.Combine(temporaryDirectory, "generated");
                var authoredFiles = QuestEditorContent.CreateFiles(workspace.Catalog)
                    .OrderBy(file => file.Key, StringComparer.Ordinal)
                    .Select(file => new KeyValuePair<string, string>(
                        Path.Combine(ContentLayout.QuestsDirectory(string.Empty), file.Key),
                        file.Value))
                    .ToArray();
                foreach (var file in authoredFiles)
                {
                    WriteText(Path.Combine(contentRoot, file.Key), file.Value);
                }

                // The compiler reads every World and the compiled items and actors it names.
                foreach (var worldId in ContentLayout.ListWorldIds(ContentLayout.WorldsDirectory(paths.ContentRoot)))
                {
                    foreach (var fileName in ContentLayout.RequiredWorldFileNames)
                    {
                        Copy(
                            Path.Combine(ContentLayout.WorldDirectory(paths.ContentRoot, worldId), fileName),
                            Path.Combine(ContentLayout.WorldDirectory(contentRoot, worldId), fileName));
                    }

                    Copy(
                        ContentLayout.GeneratedWorldActorsPath(paths.GeneratedRoot, worldId),
                        ContentLayout.GeneratedWorldActorsPath(generatedRoot, worldId));
                }

                Copy(
                    ContentLayout.GeneratedItemCatalogPath(paths.GeneratedRoot),
                    ContentLayout.GeneratedItemCatalogPath(generatedRoot));
                var compilerResult = compilerRunner.Compile(contentRoot, generatedRoot);
                if (!compilerResult.Success)
                {
                    return QuestEditorValidationResult.Failed(
                        compilerResult.Output.Length == 0 ? CompilerToolName + " failed without output." : compilerResult.Output);
                }

                var runtimePath = ContentLayout.GeneratedQuestCatalogPath(generatedRoot);
                if (!File.Exists(runtimePath))
                {
                    return QuestEditorValidationResult.Failed(CompilerToolName + " did not produce the quest catalog.");
                }

                var runtimeFile = new KeyValuePair<string, string>(
                    ContentLayout.GeneratedQuestCatalogPath(string.Empty),
                    Normalize(File.ReadAllText(runtimePath)));
                var files = authoredFiles
                    .Select(file => CreateFile("content", paths.ContentRoot, file))
                    .Append(CreateFile("Generated", paths.GeneratedRoot, runtimeFile))
                    .ToArray();
                var written = new HashSet<string>(files.Select(file => file.FullPath), StringComparer.OrdinalIgnoreCase);
                var removedFiles = ContentLayout.ListDefinitionFiles(paths.QuestsDirectory)
                    .Select(file => Path.GetFullPath(file.Path))
                    .Where(path => !written.Contains(path))
                    .ToArray();
                var changes = ClassifyChanges(ReadRuntime(paths.RuntimePath), ReadRuntimeJson(runtimeFile.Value));
                var message = compilerResult.Output.Trim() + Environment.NewLine
                    + (changes.Length == 0 ? "No quest changes detected." : string.Join(Environment.NewLine, changes));
                return QuestEditorValidationResult.Passed(message.Trim(), files, removedFiles, changes);
            }
            catch (QuestValidationException exception)
            {
                return QuestEditorValidationResult.Failed(string.Join(Environment.NewLine, exception.Errors));
            }
            catch (Exception exception)
            {
                return QuestEditorValidationResult.Failed(exception.Message);
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

        // Writes the validated files in one transaction and deletes the definitions of removed
        // quests.
        public void Save(QuestEditorValidationResult validation)
        {
            if (validation == null || !validation.Success)
            {
                throw new InvalidOperationException("A successful quest validation is required before saving.");
            }

            ContentFileTransaction.Commit(
                validation.Files.Select(file => new KeyValuePair<string, string>(file.FullPath, file.Text)),
                validation.RemovedFiles);
        }

        public ContentCompilerRunResult VerifyCanonical()
        {
            return compilerRunner.Verify(paths.ContentRoot, paths.GeneratedRoot);
        }

        private static string[] ClassifyChanges(QuestCatalogJsonDocument baseline, QuestCatalogJsonDocument candidate)
        {
            var before = Index(baseline);
            var after = Index(candidate);
            var changes = new List<string>();
            changes.AddRange(before.Keys.Except(after.Keys).OrderBy(id => id, StringComparer.Ordinal).Select(id => "Removed quest: " + id));
            changes.AddRange(after.Keys.Except(before.Keys).OrderBy(id => id, StringComparer.Ordinal).Select(id => "Added quest: " + id));
            changes.AddRange(before.Keys.Intersect(after.Keys)
                .Where(id => !string.Equals(before[id], after[id], StringComparison.Ordinal))
                .OrderBy(id => id, StringComparer.Ordinal)
                .Select(id => "Changed quest: " + id));
            if (baseline != null
                && candidate != null
                && changes.Count == 0
                && !string.Equals(baseline.revision, candidate.revision, StringComparison.Ordinal))
            {
                changes.Add("Changed quest catalog settings");
            }

            return changes.ToArray();
        }

        // Each quest's compiled JSON by id, so any change to a quest shows.
        private static Dictionary<string, string> Index(QuestCatalogJsonDocument document)
        {
            return (document?.quests ?? Array.Empty<QuestJsonDefinition>())
                .Where(quest => quest != null)
                .GroupBy(quest => quest.id, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => JsonUtility.ToJson(group.First()), StringComparer.Ordinal);
        }

        private static QuestCatalogJsonDocument ReadRuntime(string path)
        {
            return File.Exists(path) ? ReadRuntimeJson(File.ReadAllText(path)) : null;
        }

        private static QuestCatalogJsonDocument ReadRuntimeJson(string json)
        {
            return JsonUtility.FromJson<QuestCatalogJsonDocument>(json);
        }

        private static QuestEditorFile CreateFile(string displayRoot, string root, KeyValuePair<string, string> file)
        {
            var fullPath = Path.GetFullPath(Path.Combine(root, file.Key));
            var changed = !File.Exists(fullPath)
                || !string.Equals(Normalize(File.ReadAllText(fullPath)), file.Value, StringComparison.Ordinal);
            return new QuestEditorFile(displayRoot + "/" + file.Key.Replace('\\', '/'), fullPath, file.Value, changed);
        }

        private static void Copy(string source, string target)
        {
            if (!File.Exists(source))
            {
                throw new FileNotFoundException("A file the quest compiler needs is missing; run the item and actor compilers first.", source);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target));
            File.Copy(source, target);
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
