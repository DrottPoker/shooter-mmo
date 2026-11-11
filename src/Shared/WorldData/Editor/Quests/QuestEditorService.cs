using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Mmorpg.WorldData.Content;
using Mmorpg.WorldData.Editor.ContentTools;
using Mmorpg.WorldData.Quests;
using Mmorpg.WorldData.Text;
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
        // diskState is the quest files as they were when the catalog was read from them, or
        // null for a workspace that no files back, such as one a test builds.
        public QuestEditorWorkspace(
            QuestEditorCatalog catalog,
            QuestEditorReferences references,
            ContentDiskState diskState = null)
        {
            Catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            References = references ?? throw new ArgumentNullException(nameof(references));
            MarkSaved(diskState);
        }

        public QuestEditorCatalog Catalog { get; }
        public QuestEditorReferences References { get; private set; }

        // The quest files as the workspace last read or wrote them.
        public ContentDiskState DiskState { get; private set; }

        // The ids of the quests and the retired ids that the files held then. A saved id is
        // permanent: no new quest may take it, and a saved quest that leaves is retired.
        public IReadOnlyCollection<string> SavedQuestIds { get; private set; } = Array.Empty<string>();

        public IReadOnlyCollection<string> SavedRetiredQuestIds { get; private set; } = Array.Empty<string>();

        // The ids that a new or renamed quest may not take: those of the edited catalog, its
        // retired ids, and every saved id.
        public bool IsReserved(string questId)
        {
            return !string.IsNullOrEmpty(questId)
                && (SavedQuestIds.Contains(questId, StringComparer.Ordinal)
                    || SavedRetiredQuestIds.Contains(questId, StringComparer.Ordinal)
                    || (Catalog.retiredQuestIds ?? Array.Empty<string>()).Contains(questId, StringComparer.Ordinal)
                    || (Catalog.quests ?? Array.Empty<QuestEditorDefinition>()).Any(quest => quest?.id == questId));
        }

        // The files now hold the catalog as it is.
        public void MarkSaved(ContentDiskState diskState)
        {
            MarkSaved(
                diskState,
                (Catalog.quests ?? Array.Empty<QuestEditorDefinition>()).Select(quest => quest?.id).Where(id => id != null),
                Catalog.retiredQuestIds ?? Array.Empty<string>());
        }

        // The files now hold these quests and retired ids, as a save wrote them.
        public void MarkSaved(ContentDiskState diskState, IEnumerable<string> questIds, IEnumerable<string> retiredQuestIds)
        {
            DiskState = diskState;
            SavedQuestIds = diskState == null ? Array.Empty<string>() : questIds.ToArray();
            SavedRetiredQuestIds = diskState == null ? Array.Empty<string>() : retiredQuestIds.ToArray();
        }

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
            string[] changes,
            string[] questIds,
            string[] retiredQuestIds)
        {
            Success = success;
            Message = message ?? string.Empty;
            Files = files ?? Array.Empty<QuestEditorFile>();
            RemovedFiles = removedFiles ?? Array.Empty<string>();
            Changes = changes ?? Array.Empty<string>();
            QuestIds = questIds ?? Array.Empty<string>();
            RetiredQuestIds = retiredQuestIds ?? Array.Empty<string>();
        }

        public bool Success { get; }
        public string Message { get; }

        // The authored quest files and the compiled catalog in their canonical form, changed or not.
        public QuestEditorFile[] Files { get; }

        // Definition files of quests that left the catalog.
        public string[] RemovedFiles { get; }

        // Such as "Added quest: quest.new_hunt", for the save confirmation.
        public string[] Changes { get; }

        // The ids of the quests that the save writes.
        public string[] QuestIds { get; }

        // The retired quest ids that the save writes, which may hold more than the edited ones.
        public string[] RetiredQuestIds { get; }

        public static QuestEditorValidationResult Failed(string message)
        {
            return new QuestEditorValidationResult(false, message, null, null, null, null, null);
        }

        public static QuestEditorValidationResult Passed(
            string message,
            QuestEditorFile[] files,
            string[] removedFiles,
            string[] changes,
            string[] questIds,
            string[] retiredQuestIds)
        {
            return new QuestEditorValidationResult(true, message, files, removedFiles, changes, questIds, retiredQuestIds);
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

        // The quest files are read before their content, so a file that changes in between makes
        // the next save stop instead of writing over it.
        public QuestEditorWorkspace Load()
        {
            var diskState = ReadDiskState();
            return new QuestEditorWorkspace(
                QuestEditorContent.Load(paths.QuestsDirectory),
                QuestEditorReferences.Load(paths.ContentRoot, paths.GeneratedRoot),
                diskState);
        }

        // Compiles the edited quests with the quest compiler process against the committed
        // Worlds and the compiled items and actors, in a temporary copy. Quest files that changed
        // on disk since the workspace read them stop it, because the save would delete or write
        // over them.
        public QuestEditorValidationResult Validate(QuestEditorWorkspace workspace)
        {
            if (workspace == null)
            {
                throw new ArgumentNullException(nameof(workspace));
            }

            var changedOnDisk = ChangesOnDisk(workspace);
            if (changedOnDisk.Count > 0)
            {
                return QuestEditorValidationResult.Failed(ContentDiskState.ChangedMessage(changedOnDisk, paths.ContentRoot, "Quest Studio"));
            }

            var temporaryDirectory = Path.Combine(Path.GetTempPath(), "Mmorpg.QuestStudio." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryDirectory);
            try
            {
                var savedRetired = workspace.SavedRetiredQuestIds;
                var catalog = Reconcile(workspace);
                QuestCatalogCompiler.Compile(
                    catalog.ToDomain(),
                    workspace.References.Items,
                    workspace.References.Worlds);
                var contentRoot = Path.Combine(temporaryDirectory, "content");
                var generatedRoot = Path.Combine(temporaryDirectory, "generated");
                var authoredFiles = QuestEditorContent.CreateFiles(catalog)
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

                // The compiler writes the quest text table with the catalog, and a save keeps
                // it current with the catalog.
                var textTablePath = ContentLayout.GeneratedTextTablePath(
                    string.Empty,
                    TextTableFormat.SourceLocale,
                    TextTableIds.Quests);
                if (!File.Exists(Path.Combine(generatedRoot, textTablePath)))
                {
                    return QuestEditorValidationResult.Failed(CompilerToolName + " did not produce the quest text table.");
                }

                var runtimeFile = new KeyValuePair<string, string>(
                    ContentLayout.GeneratedQuestCatalogPath(string.Empty),
                    Normalize(File.ReadAllText(runtimePath)));
                var textTableFile = new KeyValuePair<string, string>(
                    textTablePath,
                    Normalize(File.ReadAllText(Path.Combine(generatedRoot, textTablePath))));
                var files = authoredFiles
                    .Select(file => CreateFile("content", paths.ContentRoot, file))
                    .Append(CreateFile("Generated", paths.GeneratedRoot, runtimeFile))
                    .Append(CreateFile("Generated", paths.GeneratedRoot, textTableFile))
                    .ToArray();
                var written = new HashSet<string>(files.Select(file => file.FullPath), StringComparer.OrdinalIgnoreCase);
                var removedFiles = ContentLayout.ListDefinitionFiles(paths.QuestsDirectory)
                    .Select(file => Path.GetFullPath(file.Path))
                    .Where(path => !written.Contains(path))
                    .ToArray();
                var committed = ReadRuntime(paths.RuntimePath, out var unreadable);
                var classified = unreadable
                    ? new[] { "Wrote the committed quest catalog again, which could not be read" }
                    : ClassifyChanges(committed, ReadRuntimeJson(runtimeFile.Value));

                // The compiled catalog carries no text, so a new wording shows only in the
                // quest text table.
                var committedTextTablePath = Path.Combine(paths.GeneratedRoot, textTablePath);
                var reworded = unreadable
                    ? Array.Empty<string>()
                    : QuestsWithChangedTexts(
                            File.Exists(committedTextTablePath) ? File.ReadAllText(committedTextTablePath) : string.Empty,
                            textTableFile.Value)
                        .Where(id => !classified.Any(change => change.EndsWith(": " + id, StringComparison.Ordinal)))
                        .Select(id => "Changed quest text: " + id)
                        .ToArray();
                var changes = classified
                    .Concat(reworded)
                    .Concat((catalog.retiredQuestIds ?? Array.Empty<string>())
                        .Where(id => !savedRetired.Contains(id, StringComparer.Ordinal))
                        .Select(id => "Retired quest id: " + id))
                    .ToArray();
                var message = compilerResult.Output.Trim() + Environment.NewLine
                    + (changes.Length == 0 ? "No quest changes detected." : string.Join(Environment.NewLine, changes));
                return QuestEditorValidationResult.Passed(
                    message.Trim(),
                    files,
                    removedFiles,
                    changes,
                    (catalog.quests ?? Array.Empty<QuestEditorDefinition>()).Select(quest => quest.id).ToArray(),
                    catalog.retiredQuestIds);
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
        // quests, unless the quest files changed on disk since the workspace read them. The
        // workspace then holds the files as written.
        public void Save(QuestEditorWorkspace workspace, QuestEditorValidationResult validation)
        {
            if (workspace == null)
            {
                throw new ArgumentNullException(nameof(workspace));
            }

            if (validation == null || !validation.Success)
            {
                throw new InvalidOperationException("A successful quest validation is required before saving.");
            }

            var changedOnDisk = ChangesOnDisk(workspace);
            if (changedOnDisk.Count > 0)
            {
                throw new InvalidOperationException(ContentDiskState.ChangedMessage(changedOnDisk, paths.ContentRoot, "Quest Studio"));
            }

            ContentFileTransaction.Commit(
                validation.Files.Select(file => new KeyValuePair<string, string>(file.FullPath, file.Text)),
                validation.RemovedFiles);
            workspace.MarkSaved(ReadDiskState(), validation.QuestIds, validation.RetiredQuestIds);
        }

        // Checks that the quest files are in the form the editor writes, as the Unity tests do,
        // and that the committed quest catalog matches them, as the command line does.
        public ContentCompilerRunResult VerifyCanonical()
        {
            var result = compilerRunner.Verify(paths.ContentRoot, paths.GeneratedRoot);
            var nonCanonical = NonCanonicalFiles();
            return nonCanonical.Count == 0
                ? result
                : new ContentCompilerRunResult(
                    false,
                    NonCanonicalMessage(nonCanonical) + Environment.NewLine + result.Output);
        }

        // The quest files, relative to the quests directory, whose text is not what the editor
        // writes for the content they hold, such as one with a field the content model does not
        // know: the quest compiler rejects such a field, and a save drops it.
        public IReadOnlyList<string> NonCanonicalFiles()
        {
            var expected = QuestEditorContent.CreateFiles(QuestEditorContent.Load(paths.QuestsDirectory))
                .ToDictionary(file => file.Key.Replace('\\', '/'), file => Normalize(file.Value), StringComparer.Ordinal);
            var actual = Directory.EnumerateFiles(paths.QuestsDirectory, "*.json", SearchOption.AllDirectories)
                .ToDictionary(
                    path => Path.GetRelativePath(paths.QuestsDirectory, path).Replace('\\', '/'),
                    path => Normalize(File.ReadAllText(path)),
                    StringComparer.Ordinal);
            return expected.Keys
                .Union(actual.Keys, StringComparer.Ordinal)
                .Where(file => !expected.TryGetValue(file, out var text)
                    || !actual.TryGetValue(file, out var current)
                    || !string.Equals(text, current, StringComparison.Ordinal))
                .OrderBy(file => file, StringComparer.Ordinal)
                .ToArray();
        }

        public static string NonCanonicalMessage(IReadOnlyList<string> files)
        {
            return "These quest files are not in the form Quest Studio writes, for example because of a field it does not know, "
                + "which the quest compiler rejects: " + string.Join(", ", files) + ". Saving in Quest Studio writes them in that form.";
        }

        // The catalog as a save writes it: the edited content, plus every id that the saved
        // catalog retired and the id of every saved quest that left the catalog, because a
        // released quest id is permanent. So undo across a save can neither remove a saved quest
        // without retiring its id nor bring a retired id back, which the compiler then rejects.
        // Saved means as the workspace last read or wrote the files, never a file it did not see.
        public QuestEditorCatalog Reconcile(QuestEditorWorkspace workspace)
        {
            if (workspace == null)
            {
                throw new ArgumentNullException(nameof(workspace));
            }

            var catalog = workspace.Catalog;
            var retired = catalog.retiredQuestIds ?? Array.Empty<string>();
            var kept = new HashSet<string>(
                (catalog.quests ?? Array.Empty<QuestEditorDefinition>()).Select(quest => quest?.id).Where(id => id != null),
                StringComparer.Ordinal);
            var missing = workspace.SavedRetiredQuestIds
                .Concat(workspace.SavedQuestIds.Where(id => !kept.Contains(id)))
                .Where(id => !retired.Contains(id, StringComparer.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();
            if (missing.Length == 0)
            {
                return catalog;
            }

            var reconciled = QuestEditorJson.Clone(catalog);
            reconciled.retiredQuestIds = retired.Concat(missing).ToArray();
            return reconciled;
        }

        private ContentDiskState ReadDiskState()
        {
            return ContentDiskState.Read(new[] { paths.QuestsDirectory });
        }

        private static IReadOnlyList<string> ChangesOnDisk(QuestEditorWorkspace workspace)
        {
            return workspace.DiskState?.Changes() ?? Array.Empty<string>();
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
                && (baseline.maximumActiveQuests != candidate.maximumActiveQuests
                    || !string.Equals(baseline.catalogId, candidate.catalogId, StringComparison.Ordinal)
                    || baseline.formatVersion != candidate.formatVersion))
            {
                changes.Add("Changed quest catalog settings");
            }

            return changes.ToArray();
        }

        // The ids of the quests whose texts differ between two quest text tables.
        private static string[] QuestsWithChangedTexts(string committedJson, string candidateJson)
        {
            var before = TextsByKey(committedJson);
            var after = TextsByKey(candidateJson);
            return before.Keys.Union(after.Keys, StringComparer.Ordinal)
                .Where(key => !before.TryGetValue(key, out var earlier)
                    || !after.TryGetValue(key, out var later)
                    || !string.Equals(earlier, later, StringComparison.Ordinal))
                .Select(QuestIdOfKey)
                .Where(id => id != null)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();
        }

        // A table that is missing or unreadable counts as empty, so every text shows as new.
        private static Dictionary<string, string> TextsByKey(string json)
        {
            TextTableJsonDocument table = null;
            try
            {
                table = string.IsNullOrWhiteSpace(json) ? null : JsonUtility.FromJson<TextTableJsonDocument>(json);
            }
            catch (ArgumentException)
            {
            }

            return (table?.entries ?? Array.Empty<TextTableJsonEntry>())
                .Where(entry => entry != null && !string.IsNullOrEmpty(entry.key))
                .GroupBy(entry => entry.key, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First().text ?? string.Empty, StringComparer.Ordinal);
        }

        // "quest/<quest-id>/..." names its quest in the second segment.
        private static string QuestIdOfKey(string key)
        {
            var segments = key.Split(TextKeys.Separator);
            return segments.Length >= 3 && string.Equals(segments[0], "quest", StringComparison.Ordinal)
                ? segments[1]
                : null;
        }

        // Each quest's compiled JSON by id, so any change to a quest shows.
        private static Dictionary<string, string> Index(QuestCatalogJsonDocument document)
        {
            return (document?.quests ?? Array.Empty<QuestJsonDefinition>())
                .Where(quest => quest != null)
                .GroupBy(quest => quest.id, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => JsonUtility.ToJson(group.First()), StringComparer.Ordinal);
        }

        // The committed catalog, or null when it is missing or cannot be read, such as one with
        // merge markers, which a save then writes again.
        private static QuestCatalogJsonDocument ReadRuntime(string path, out bool unreadable)
        {
            unreadable = false;
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                var document = ReadRuntimeJson(File.ReadAllText(path));
                unreadable = document == null;
                return document;
            }
            catch (ArgumentException)
            {
                unreadable = true;
                return null;
            }
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
