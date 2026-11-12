using System;
using System.IO;
using Mmorpg.WorldData.Content;
using Mmorpg.WorldData.Editor.Quests;
using Mmorpg.WorldData.Text;

namespace Mmorpg.Client.Tests.EditMode
{
    // A temporary copy of what the quest compiler reads and verifies: the checked-in quests,
    // every World, the compiled items and actors, and the committed quest catalog and quest
    // text table. Editor tests save into the copy, never into the repository.
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
                    ContentLayout.GeneratedWorldActorSpawnsPath(source.GeneratedRoot, worldId),
                    ContentLayout.GeneratedWorldActorSpawnsPath(Paths.GeneratedRoot, worldId));
            }

            Copy(ContentLayout.GeneratedActorCatalogPath(source.GeneratedRoot), ContentLayout.GeneratedActorCatalogPath(Paths.GeneratedRoot));
            Copy(ContentLayout.GeneratedItemCatalogPath(source.GeneratedRoot), ContentLayout.GeneratedItemCatalogPath(Paths.GeneratedRoot));
            Copy(source.RuntimePath, Paths.RuntimePath);

            // The quest compiler verifies the quest text table with the catalog.
            Copy(
                ContentLayout.GeneratedTextTablePath(
                    source.GeneratedRoot,
                    TextTableFormat.SourceLocale,
                    TextTableIds.Quests),
                ContentLayout.GeneratedTextTablePath(
                    Paths.GeneratedRoot,
                    TextTableFormat.SourceLocale,
                    TextTableIds.Quests));
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
