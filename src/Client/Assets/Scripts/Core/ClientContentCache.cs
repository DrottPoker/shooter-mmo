using System;
using System.IO;

namespace Mmorpg.Client.Core
{
    // The content files the client downloaded, one folder per kind, locale, and id, with the
    // revision as the file name: <root>/item-catalog/core/<revision>.json and
    // <root>/text-table/en/items/<revision>.json. A file is written only after it validated, and
    // through a temporary file, so a crash never leaves half a file under a revision. Every
    // failure is reported, never thrown: a cache that cannot be used costs a download.
    public sealed class ClientContentCache
    {
        private const string FileSuffix = ".json";
        private const string TemporarySuffix = ".tmp";

        private readonly string rootDirectory;

        public ClientContentCache(string rootDirectory)
        {
            if (string.IsNullOrWhiteSpace(rootDirectory))
            {
                throw new ArgumentException("The content cache needs a folder.", nameof(rootDirectory));
            }

            this.rootDirectory = rootDirectory;
        }

        // False with an empty error when the cache holds no such file.
        public bool TryRead(ClientContentRequest file, string revision, out byte[] bytes, out string error)
        {
            bytes = null;
            error = string.Empty;
            try
            {
                var path = FilePath(file, revision);
                if (!File.Exists(path))
                {
                    return false;
                }

                bytes = File.ReadAllBytes(path);
                return true;
            }
            catch (Exception exception) when (IsFileSystemFailure(exception))
            {
                error = "The cached " + file.Name + " could not be read: " + exception.Message;
                return false;
            }
        }

        public bool TryWrite(ClientContentRequest file, string revision, byte[] bytes, out string error)
        {
            error = string.Empty;
            var temporaryPath = string.Empty;
            try
            {
                var path = FilePath(file, revision);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                temporaryPath = path + "." + Guid.NewGuid().ToString("N") + TemporarySuffix;
                File.WriteAllBytes(temporaryPath, bytes);

                // A file of the same revision holds the same bytes, so replacing it is safe.
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                File.Move(temporaryPath, path);
                return true;
            }
            catch (Exception exception) when (IsFileSystemFailure(exception))
            {
                TryDeleteFile(temporaryPath);
                error = "The downloaded " + file.Name + " could not be cached: " + exception.Message;
                return false;
            }
        }

        public bool TryDelete(ClientContentRequest file, string revision, out string error)
        {
            error = string.Empty;
            try
            {
                var path = FilePath(file, revision);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                return true;
            }
            catch (Exception exception) when (IsFileSystemFailure(exception))
            {
                error = "The cached " + file.Name + " could not be deleted: " + exception.Message;
                return false;
            }
        }

        // Deletes every other revision and every leftover temporary file of one content file.
        public bool TryDeleteOtherRevisions(ClientContentRequest file, string keptRevision, out string error)
        {
            error = string.Empty;
            try
            {
                var directory = FileDirectory(file);
                if (!Directory.Exists(directory))
                {
                    return true;
                }

                var kept = keptRevision + FileSuffix;
                foreach (var path in Directory.GetFiles(directory))
                {
                    if (!string.Equals(Path.GetFileName(path), kept, StringComparison.Ordinal))
                    {
                        File.Delete(path);
                    }
                }

                return true;
            }
            catch (Exception exception) when (IsFileSystemFailure(exception))
            {
                error = "The old revisions of " + file.Name + " could not be deleted: " + exception.Message;
                return false;
            }
        }

        public string FilePath(ClientContentRequest file, string revision)
        {
            if (!ClientContentManifest.IsRevision(revision))
            {
                throw new ArgumentException("A cached file is named by a SHA-256 revision.", nameof(revision));
            }

            return Path.Combine(FileDirectory(file), revision + FileSuffix);
        }

        // The id and locale come from the client's own content set, never from the server.
        private string FileDirectory(ClientContentRequest file)
        {
            return file.Locale.Length > 0
                ? Path.Combine(rootDirectory, file.Kind, file.Locale, file.Id)
                : Path.Combine(rootDirectory, file.Kind, file.Id);
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception exception) when (IsFileSystemFailure(exception))
            {
                // The next cleanup of this file deletes it.
            }
        }

        private static bool IsFileSystemFailure(Exception exception)
        {
            return exception is IOException
                || exception is UnauthorizedAccessException
                || exception is NotSupportedException
                || exception is System.Security.SecurityException;
        }
    }
}
