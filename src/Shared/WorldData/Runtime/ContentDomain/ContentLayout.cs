using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mmorpg.WorldData.Worlds;

namespace Mmorpg.WorldData.Content
{
    // One authored definition file below a content domain's definitions directory.
    public sealed class ContentDefinitionFile
    {
        public ContentDefinitionFile(string id, string path)
        {
            Id = id;
            Path = path;
        }

        public string Id { get; }

        public string Path { get; }
    }

    // Repository layout of authored content and the generated data compiled from it.
    // Unity tools, .NET tools, and tests share these locations and naming rules.
    public static class ContentLayout
    {
        public const string SolutionFileName = "Mmorpg.slnx";
        public const string CatalogFileName = "catalog.json";
        public const string DefinitionsDirectoryName = "definitions";
        public const string ActorSpawnsFileName = "actor-spawns.json";
        public const string CollisionSourceFileName = "collision.json";
        public const string ItemCatalogFileName = "core.item-catalog.json";
        public const string WorldActorsFileName = "world-actors.json";
        public const string CollisionManifestFileName = "manifest.json";

        // Unity loads collision as Addressables whose addresses mirror this directory:
        // "WorldCollision/<world>/manifest.json" and "WorldCollision/<world>/<chunk>.bytes".
        public const string CollisionDirectoryName = "WorldCollision";

        public const int MaximumDefinitionIdLength = 128;

        private const string DefinitionFileExtension = ".json";

        // Every World directory holds these authored files. The SimulationWorker needs the
        // manifest and the data compiled from the other two at startup.
        public static readonly IReadOnlyList<string> RequiredWorldFileNames = Array.AsReadOnly(
            new[]
            {
                WorldManifestFormat.FileName,
                ActorSpawnsFileName,
                CollisionSourceFileName
            });

        private static readonly HashSet<string> ReservedFileNames = new HashSet<string>(
            new[]
            {
                "con", "prn", "aux", "nul",
                "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
                "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9"
            },
            StringComparer.Ordinal);

        public static string FindRepositoryRoot(string startDirectory)
        {
            if (string.IsNullOrWhiteSpace(startDirectory))
            {
                throw new ArgumentException("A start directory is required.", nameof(startDirectory));
            }

            var directory = new DirectoryInfo(Path.GetFullPath(startDirectory));
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, SolutionFileName)))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException(
                "Could not locate " + SolutionFileName + " above '" + startDirectory + "'.");
        }

        public static string ContentRoot(string repositoryRoot)
        {
            return Path.Combine(repositoryRoot, "content");
        }

        public static string GeneratedRoot(string repositoryRoot)
        {
            return Path.Combine(repositoryRoot, "src", "Shared", "WorldData", "Generated");
        }

        public static string ItemsDirectory(string contentRoot)
        {
            return Path.Combine(contentRoot, "items");
        }

        public static string ActorsDirectory(string contentRoot)
        {
            return Path.Combine(contentRoot, "actors");
        }

        // Development-only data, such as the item packages of the development item tool.
        public static string DevelopmentDirectory(string contentRoot)
        {
            return Path.Combine(contentRoot, "development");
        }

        public static string DevelopmentItemPackagesPath(string contentRoot)
        {
            return Path.Combine(DevelopmentDirectory(contentRoot), "item-packages.json");
        }

        public static string WorldsDirectory(string contentRoot)
        {
            return Path.Combine(contentRoot, "worlds");
        }

        public static string WorldDirectory(string contentRoot, string worldId)
        {
            return Path.Combine(WorldsDirectory(contentRoot), RequireWorldId(worldId));
        }

        public static string ActorSpawnsPath(string contentRoot, string worldId)
        {
            return Path.Combine(WorldDirectory(contentRoot, worldId), ActorSpawnsFileName);
        }

        public static string CollisionSourcePath(string contentRoot, string worldId)
        {
            return Path.Combine(
                WorldDirectory(contentRoot, worldId),
                CollisionSourceFileName);
        }

        public static string GeneratedItemCatalogPath(string generatedRoot)
        {
            return Path.Combine(generatedRoot, "Items", ItemCatalogFileName);
        }

        public static string GeneratedWorldsDirectory(string generatedRoot)
        {
            return Path.Combine(generatedRoot, "Worlds");
        }

        public static string GeneratedWorldActorsPath(string generatedRoot, string worldId)
        {
            return Path.Combine(
                GeneratedWorldsDirectory(generatedRoot),
                RequireWorldId(worldId),
                WorldActorsFileName);
        }

        public static string GeneratedCollisionRoot(string generatedRoot)
        {
            return Path.Combine(generatedRoot, CollisionDirectoryName);
        }

        public static string GeneratedCollisionDirectory(string generatedRoot, string worldId)
        {
            return Path.Combine(GeneratedCollisionRoot(generatedRoot), RequireWorldId(worldId));
        }

        public static string CatalogPath(string domainDirectory)
        {
            return Path.Combine(domainDirectory, CatalogFileName);
        }

        public static string DefinitionsDirectory(string domainDirectory)
        {
            return Path.Combine(domainDirectory, DefinitionsDirectoryName);
        }

        public static string DefinitionPath(string domainDirectory, string definitionId)
        {
            return Path.Combine(
                DefinitionsDirectory(domainDirectory),
                DefinitionFileName(definitionId));
        }

        public static string DefinitionFileName(string definitionId)
        {
            if (!IsValidDefinitionId(definitionId))
            {
                throw new InvalidDataException(
                    "Definition id '" + definitionId + "' cannot name a content file. Use "
                    + "lowercase letters, digits, '.', '_', or '-', starting and ending with "
                    + "a letter or digit.");
            }

            return definitionId + DefinitionFileExtension;
        }

        // Definition ids name their files, so they must be portable file names on every
        // platform and can never reach outside the definitions directory.
        public static bool IsValidDefinitionId(string definitionId)
        {
            if (string.IsNullOrEmpty(definitionId)
                || definitionId.Length > MaximumDefinitionIdLength
                || !IsLowerAsciiLetterOrDigit(definitionId[0])
                || !IsLowerAsciiLetterOrDigit(definitionId[definitionId.Length - 1]))
            {
                return false;
            }

            foreach (var character in definitionId)
            {
                if (!IsLowerAsciiLetterOrDigit(character)
                    && character != '.'
                    && character != '_'
                    && character != '-')
                {
                    return false;
                }
            }

            var baseName = definitionId.Split('.')[0];
            return !ReservedFileNames.Contains(baseName);
        }

        // Lists definition files in id order. Hidden files are ignored; anything else that
        // is not <definition-id>.json fails, so a misplaced file never drops out silently.
        public static ContentDefinitionFile[] ListDefinitionFiles(string domainDirectory)
        {
            var directory = DefinitionsDirectory(domainDirectory);
            if (!Directory.Exists(directory))
            {
                throw new DirectoryNotFoundException(
                    "Content definitions directory does not exist at '" + directory + "'.");
            }

            var files = new List<ContentDefinitionFile>();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var name = Path.GetFileName(entry);
                if (name.StartsWith(".", StringComparison.Ordinal))
                {
                    continue;
                }

                if (Directory.Exists(entry))
                {
                    throw new InvalidDataException(
                        "Content definitions directory '" + directory
                        + "' must not contain subdirectories, found '" + name + "'.");
                }

                if (!name.EndsWith(DefinitionFileExtension, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "Content definition file '" + entry + "' must use the "
                        + DefinitionFileExtension + " extension.");
                }

                var id = name.Substring(0, name.Length - DefinitionFileExtension.Length);
                if (!IsValidDefinitionId(id))
                {
                    throw new InvalidDataException(
                        "Content definition file '" + entry
                        + "' must be named after a valid definition id.");
                }

                files.Add(new ContentDefinitionFile(id, entry));
            }

            return files.OrderBy(file => file.Id, StringComparer.Ordinal).ToArray();
        }

        // Lists world ids below a world root in id order. Every directory except hidden ones
        // is a World and must contain every required World file, so an incomplete World
        // fails instead of dropping out of compilation silently.
        public static string[] ListWorldIds(string worldsDirectory)
        {
            if (!Directory.Exists(worldsDirectory))
            {
                return Array.Empty<string>();
            }

            var worldIds = new List<string>();
            foreach (var directory in Directory.EnumerateDirectories(worldsDirectory))
            {
                var name = Path.GetFileName(directory);
                if (name.StartsWith(".", StringComparison.Ordinal))
                {
                    continue;
                }

                var worldId = RequireWorldId(name);
                foreach (var fileName in RequiredWorldFileNames)
                {
                    if (!File.Exists(Path.Combine(directory, fileName)))
                    {
                        throw new InvalidDataException(
                            "World '" + worldId + "' at '" + directory + "' is missing "
                            + fileName + ".");
                    }
                }

                worldIds.Add(worldId);
            }

            return worldIds.OrderBy(worldId => worldId, StringComparer.Ordinal).ToArray();
        }

        public static bool IsValidWorldId(string worldId)
        {
            if (string.IsNullOrEmpty(worldId) || worldId.Length > MaximumDefinitionIdLength)
            {
                return false;
            }

            foreach (var character in worldId)
            {
                if (!IsAsciiLetterOrDigit(character) && character != '-' && character != '_')
                {
                    return false;
                }
            }

            return true;
        }

        private static string RequireWorldId(string worldId)
        {
            if (!IsValidWorldId(worldId))
            {
                throw new InvalidDataException("World id '" + worldId + "' is invalid.");
            }

            return worldId;
        }

        private static bool IsLowerAsciiLetterOrDigit(char value)
        {
            return (value >= 'a' && value <= 'z') || (value >= '0' && value <= '9');
        }

        private static bool IsAsciiLetterOrDigit(char value)
        {
            return IsLowerAsciiLetterOrDigit(value) || (value >= 'A' && value <= 'Z');
        }
    }
}
