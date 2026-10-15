using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Mmorpg.WorldData.Editor
{
    // Applies content file writes and deletions as one unit. Writes run in the given order
    // and before any deletion, unchanged files are left untouched, and if a step fails
    // every touched file is restored.
    public static class ContentFileTransaction
    {
        private static readonly UTF8Encoding Utf8WithoutBom = new UTF8Encoding(false);

        public static void Commit(
            IEnumerable<KeyValuePair<string, string>> writes,
            IEnumerable<string> deletes)
        {
            var writeList = (writes ?? throw new ArgumentNullException(nameof(writes)))
                .Select(pair => new KeyValuePair<string, string>(
                    Path.GetFullPath(pair.Key),
                    pair.Value ?? throw new ArgumentException(
                        "Content file '" + pair.Key + "' has no text.",
                        nameof(writes))))
                .ToArray();
            var writePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in writeList)
            {
                if (!writePaths.Add(pair.Key))
                {
                    throw new ArgumentException(
                        "Content file '" + pair.Key + "' is written more than once.",
                        nameof(writes));
                }
            }

            var deleteList = (deletes ?? Enumerable.Empty<string>())
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (deleteList.Any(writePaths.Contains))
            {
                throw new ArgumentException(
                    "A content file cannot be written and deleted in one commit.",
                    nameof(deletes));
            }

            var snapshots = writeList.Select(pair => pair.Key)
                .Concat(deleteList)
                .ToDictionary(
                    path => path,
                    path => File.Exists(path) ? File.ReadAllBytes(path) : null,
                    StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var pair in writeList)
                {
                    var content = Utf8WithoutBom.GetBytes(NormalizeLineEndings(pair.Value));
                    var current = snapshots[pair.Key];
                    if (current == null || !current.SequenceEqual(content))
                    {
                        AtomicWrite(pair.Key, content);
                    }
                }

                foreach (var path in deleteList)
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
            }
            catch (Exception exception)
            {
                var restoreFailures = Restore(snapshots);
                if (restoreFailures.Count == 0)
                {
                    throw;
                }

                restoreFailures.Insert(0, exception);
                throw new AggregateException(
                    "Content files could not be committed or fully restored.",
                    restoreFailures);
            }
        }

        public static string NormalizeLineEndings(string value)
        {
            return (value ?? string.Empty).Replace("\r\n", "\n").Replace("\r", "\n");
        }

        private static List<Exception> Restore(IReadOnlyDictionary<string, byte[]> snapshots)
        {
            var failures = new List<Exception>();
            foreach (var snapshot in snapshots)
            {
                try
                {
                    if (snapshot.Value != null)
                    {
                        AtomicWrite(snapshot.Key, snapshot.Value);
                    }
                    else if (File.Exists(snapshot.Key))
                    {
                        File.Delete(snapshot.Key);
                    }
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }

            return failures;
        }

        private static void AtomicWrite(string path, byte[] content)
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
                File.WriteAllBytes(temporaryPath, content);
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
    }
}
