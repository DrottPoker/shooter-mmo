using System;
using System.IO;
using Mmorpg.WorldData.Content;
using Mmorpg.WorldData.Editor.Quests;

namespace Mmorpg.Client.Tests.EditMode
{
    // A temporary copy of what the quest compiler reads: the checked-in quests, every World,
    // the compiled items and actors, and the committed quest catalog. Editor tests save into
    // the copy, never into the repository.
    internal sealed class FixtureQuestContent : IDisposable
    {
        private readonly string temporaryDirectory;

        public FixtureQuestContent()
        {
            var source = QuestEditorPaths.CreateDefault();
            temporaryDirectory = Path.Combine(Path.GetTempPath(), "MmorpgQuestFixture", Guid.NewGuid().ToString("N"));
            Paths = new QuestEditorPaths(
                source.RepositoryRoot,
                Path.Combine(temporaryDirectory, "content"),
                Path.Combine(temporaryDirectory, "generated"));
            foreach (var path in Directory.EnumerateFiles(source.QuestsDirectory, "*.json", SearchOption.AllDirectories))
            {
                Copy(path, Path.Combine(Paths.QuestsDirectory, Path.GetRelativePath(source.QuestsDirectory, path)));
            }

            foreach (var worldId in ContentLayout.ListWorldIds(ContentLayout.WorldsDirectory(source.ContentRoot)))
            {
                foreach (var fileName in ContentLayout.RequiredWorldFileNames)
                {
                    Copy(
                        Path.Combine(ContentLayout.WorldDirectory(source.ContentRoot, worldId), fileName),
                        Path.Combine(ContentLayout.WorldDirectory(Paths.ContentRoot, worldId), fileName));
                }

                Copy(
                    ContentLayout.GeneratedWorldActorsPath(source.GeneratedRoot, worldId),
                    ContentLayout.GeneratedWorldActorsPath(Paths.GeneratedRoot, worldId));
            }

            Copy(ContentLayout.GeneratedItemCatalogPath(source.GeneratedRoot), ContentLayout.GeneratedItemCatalogPath(Paths.GeneratedRoot));
            Copy(source.RuntimePath, Paths.RuntimePath);
        }

        public QuestEditorPaths Paths { get; }

        public void Dispose()
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, true);
            }
        }

        private static void Copy(string source, string target)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            File.Copy(source, target);
        }
    }
}
