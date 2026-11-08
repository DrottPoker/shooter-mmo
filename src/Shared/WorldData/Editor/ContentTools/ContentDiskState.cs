using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Mmorpg.WorldData.Editor.ContentTools
{
    // The content files of a tool as they were on disk when it loaded or saved them. A save
    // compares them with the disk first, so it never deletes or overwrites a file that someone
    // else, such as a git pull, added or changed since.
    public sealed class ContentDiskState
    {
        private readonly string[] directories;
        private readonly string[] files;
        private readonly Dictionary<string, string> texts;

        private ContentDiskState(string[] directories, string[] files, Dictionary<string, string> texts)
        {
            this.directories = directories;
            this.files = files;
            this.texts = texts;
        }

        // Reads every JSON file below the directories, and the single files.
        public static ContentDiskState Read(IEnumerable<string> directories, IEnumerable<string> files = null)
        {
            var directoryList = (directories ?? Array.Empty<string>()).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var fileList = (files ?? Array.Empty<string>()).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            return new ContentDiskState(directoryList, fileList, ReadTexts(directoryList, fileList));
        }

        // The state now, of the same directories and files.
        public ContentDiskState ReadAgain()
        {
            return new ContentDiskState(directories, files, ReadTexts(directories, files));
        }

        // The files that were added, removed, or changed since this state, by full path.
        public IReadOnlyList<string> Changes()
        {
            var now = ReadTexts(directories, files);
            return texts.Keys
                .Union(now.Keys, StringComparer.OrdinalIgnoreCase)
                .Where(path => !texts.TryGetValue(path, out var before)
                    || !now.TryGetValue(path, out var after)
                    || !string.Equals(before, after, StringComparison.Ordinal))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        // Why a save stops when the files changed, naming them below the root.
        public static string ChangedMessage(IReadOnlyList<string> changes, string root, string toolName)
        {
            var names = changes
                .Take(5)
                .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
                .ToArray();
            return "The content files changed on disk since " + toolName + " loaded them, for example by a git pull: "
                + string.Join(", ", names)
                + (changes.Count > names.Length ? " and " + (changes.Count - names.Length) + " more" : string.Empty)
                + ". Nothing was written. Reload to load the files on disk first.";
        }

        private static Dictionary<string, string> ReadTexts(IEnumerable<string> directories, IEnumerable<string> files)
        {
            var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var directory in directories.Where(Directory.Exists))
            {
                foreach (var path in Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories))
                {
                    texts[Path.GetFullPath(path)] = ContentFileTransaction.NormalizeLineEndings(File.ReadAllText(path));
                }
            }

            foreach (var path in files.Where(File.Exists))
            {
                texts[path] = ContentFileTransaction.NormalizeLineEndings(File.ReadAllText(path));
            }

            return texts;
        }
    }
}
