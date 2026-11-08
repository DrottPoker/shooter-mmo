using System.IO;
using System.Linq;
using Mmorpg.WorldData.Content;
using Mmorpg.WorldData.Editor;
using Mmorpg.WorldData.Editor.ContentTools;
using Mmorpg.WorldData.Editor.Quests;
using Mmorpg.WorldData.Quests;
using NUnit.Framework;
using UnityEngine;

namespace Mmorpg.Client.Tests.EditMode
{
    // Quest Studio's content model and service against the checked-in quests: the editor
    // writes them byte for byte as committed, compiles them like the command line, and saves
    // through the quest compiler process.
    public sealed class QuestStudioServiceTests
    {
        [Test]
        public void CheckedInQuestContentIsInCanonicalEditorFormat()
        {
            var paths = QuestEditorPaths.CreateDefault();
            var expected = QuestEditorContent.CreateFiles(QuestEditorContent.Load(paths.QuestsDirectory));
            var actual = Directory.EnumerateFiles(paths.QuestsDirectory, "*.json", SearchOption.AllDirectories)
                .ToDictionary(path => Path.GetRelativePath(paths.QuestsDirectory, path), ReadNormalized);

            Assert.That(actual.Keys, Is.EquivalentTo(expected.Keys));
            foreach (var file in expected)
            {
                Assert.That(actual[file.Key], Is.EqualTo(file.Value), file.Key);
            }
        }

        [Test]
        public void TheEditorCompilesTheCheckedInQuestsToTheCommittedRevision()
        {
            var workspace = new QuestEditorService(QuestEditorPaths.CreateDefault()).Load();

            var compiled = QuestCatalogCompiler.Compile(
                workspace.Catalog.ToDomain(),
                workspace.References.Items,
                workspace.References.Worlds);

            Assert.That(compiled.Revision, Is.EqualTo(CommittedRevision(QuestEditorPaths.CreateDefault())));
            Assert.That(QuestEditorIssues.Collect(workspace.Catalog, workspace.References), Is.Empty);
            Assert.That(
                workspace.References.QuestNpcs.Select(actor => actor.Id),
                Is.EquivalentTo(new[] { "npc.city_services", "npc.outpost_sergeant" }));
        }

        [Test]
        public void EveryTemplateCompilesAgainstTheCheckedInContent()
        {
            var workspace = new QuestEditorService(QuestEditorPaths.CreateDefault()).Load();

            foreach (var template in QuestEditorTemplates.All)
            {
                var catalog = QuestEditorJson.Clone(workspace.Catalog);
                var quest = QuestEditorTemplates.Create(
                    template,
                    QuestEditorTemplates.UniqueId("quest.new_" + template, catalog),
                    workspace.References);
                catalog.quests = catalog.quests.Append(quest).ToArray();

                Assert.That(
                    QuestEditorIssues.Collect(catalog, workspace.References).Select(issue => issue.EntityKey + ": " + issue.Message),
                    Is.Empty,
                    template);
            }
        }

        [Test]
        public void IssuesNameTheQuestAndTheFieldTheyConcern()
        {
            var workspace = new QuestEditorService(QuestEditorPaths.CreateDefault()).Load();
            var catalog = QuestEditorJson.Clone(workspace.Catalog);
            var wolves = catalog.quests.Single(quest => quest.id == "quest.wolves_at_the_gate");
            wolves.title = string.Empty;
            wolves.steps[0].objectives[0].count = 0;
            catalog.quests = catalog.quests.Append(QuestEditorJson.Clone(wolves)).ToArray();

            var issues = QuestEditorIssues.Collect(catalog, workspace.References);

            Assert.That(issues, Has.Some.Matches<ContentIssue>(issue =>
                issue.EntityKey == "quest.wolves_at_the_gate" && issue.Field == "title"));
            Assert.That(issues, Has.Some.Matches<ContentIssue>(issue =>
                issue.EntityKey == "quest.wolves_at_the_gate" && issue.Field == "steps[0].objectives[0].count"));
            Assert.That(issues, Has.Some.Matches<ContentIssue>(issue =>
                issue.EntityKey == "quest.wolves_at_the_gate" && issue.Field == "id"));
            Assert.That(
                QuestEditorIssues.Describe(issues.First(issue => issue.Field == "title"), catalog),
                Does.StartWith("quest.wolves_at_the_gate: title"));
        }

        [Test]
        public void PreviewAndVerifyUseTheCanonicalCompiler()
        {
            var service = new QuestEditorService(QuestEditorPaths.CreateDefault());
            var workspace = service.Load();

            var preview = service.Validate(workspace);
            var verify = service.VerifyCanonical();

            Assert.That(preview.Success, Is.True, preview.Message);
            Assert.That(preview.Changes, Is.Empty);
            Assert.That(preview.RemovedFiles, Is.Empty);
            Assert.That(preview.Files.Where(file => file.Changed).Select(file => file.DisplayPath), Is.Empty);
            Assert.That(
                preview.Files.Select(file => file.DisplayPath),
                Does.Contain("Generated/Quests/core.quest-catalog.json")
                    .And.Contain("content/quests/definitions/quest.wolves_at_the_gate.json"));
            Assert.That(verify.Success, Is.True, verify.Output);
        }

        [Test]
        public void AddedAndRemovedQuestsWriteAndDeleteTheirFiles()
        {
            using (var content = new FixtureQuestContent())
            {
                var paths = content.Paths;
                var service = new QuestEditorService(paths);
                var workspace = service.Load();
                var added = QuestEditorTemplates.Create(QuestEditorTemplates.Kill, "quest.editor_test_hunt", workspace.References);
                workspace.Catalog.quests = workspace.Catalog.quests.Append(added).ToArray();

                var addition = service.Validate(workspace);
                Assert.That(addition.Success, Is.True, addition.Message);
                Assert.That(addition.Changes, Is.EqualTo(new[] { "Added quest: quest.editor_test_hunt" }));
                service.Save(workspace, addition);

                var addedPath = ContentLayout.DefinitionPath(paths.QuestsDirectory, "quest.editor_test_hunt");
                Assert.That(File.Exists(addedPath), Is.True);
                var verifyAfterAddition = service.VerifyCanonical();
                Assert.That(verifyAfterAddition.Success, Is.True, verifyAfterAddition.Output);

                workspace.Catalog.quests = workspace.Catalog.quests.Where(quest => quest.id != "quest.editor_test_hunt").ToArray();
                workspace.Catalog.retiredQuestIds = new[] { "quest.editor_test_hunt" };
                var removal = service.Validate(workspace);
                Assert.That(removal.Success, Is.True, removal.Message);
                Assert.That(removal.RemovedFiles, Is.EqualTo(new[] { Path.GetFullPath(addedPath) }));
                Assert.That(
                    removal.Changes,
                    Is.EqualTo(new[] { "Removed quest: quest.editor_test_hunt", "Retired quest id: quest.editor_test_hunt" }));
                service.Save(workspace, removal);

                Assert.That(File.Exists(addedPath), Is.False);
                Assert.That(service.VerifyCanonical().Success, Is.True);
                Assert.That(
                    QuestEditorContent.Load(paths.QuestsDirectory).retiredQuestIds,
                    Is.EqualTo(new[] { "quest.editor_test_hunt" }));
            }
        }

        // Undo may take a saved quest away together with its retirement, or bring a retired quest
        // back. A save still retires the removed quest, and the retired id never returns.
        [Test]
        public void SavesRetireRemovedSavedQuestsAndNeverReviveRetiredIds()
        {
            using (var content = new FixtureQuestContent())
            {
                var service = new QuestEditorService(content.Paths);
                var workspace = service.Load();
                var original = QuestEditorJson.Clone(workspace.Catalog);
                var hunt = QuestEditorTemplates.Create(QuestEditorTemplates.Kill, "quest.editor_test_hunt", workspace.References);
                workspace.Catalog.quests = workspace.Catalog.quests.Append(hunt).ToArray();
                service.Save(workspace, service.Validate(workspace));

                workspace.RestoreContent(QuestEditorJson.Clone(original));
                var removal = service.Validate(workspace);

                Assert.That(removal.Success, Is.True, removal.Message);
                Assert.That(
                    removal.Changes,
                    Is.EqualTo(new[] { "Removed quest: quest.editor_test_hunt", "Retired quest id: quest.editor_test_hunt" }));
                Assert.That(removal.RetiredQuestIds, Is.EqualTo(new[] { "quest.editor_test_hunt" }));
                service.Save(workspace, removal);
                Assert.That(
                    QuestEditorContent.Load(content.Paths.QuestsDirectory).retiredQuestIds,
                    Is.EqualTo(new[] { "quest.editor_test_hunt" }));

                workspace.Catalog.quests = workspace.Catalog.quests.Append(QuestEditorJson.Clone(hunt)).ToArray();
                var revival = service.Validate(workspace);

                Assert.That(revival.Success, Is.False);
                Assert.That(revival.Message, Does.Contain("'quest.editor_test_hunt' is retired"));
                Assert.That(service.Reconcile(workspace).retiredQuestIds, Is.EqualTo(new[] { "quest.editor_test_hunt" }));
            }
        }

        // A quest file that a git pull adds after the load is neither deleted nor retired: the
        // validation and the save stop until a load reads it.
        [Test]
        public void QuestFilesThatChangedOnDiskSinceTheLoadStopTheSave()
        {
            using (var content = new FixtureQuestContent())
            {
                var service = new QuestEditorService(content.Paths);
                var workspace = service.Load();
                var validated = service.Validate(workspace);
                Assert.That(validated.Success, Is.True, validated.Message);
                var pulledPath = ContentLayout.DefinitionPath(content.Paths.QuestsDirectory, "quest.pulled_hunt");
                File.WriteAllText(
                    pulledPath,
                    File.ReadAllText(ContentLayout.DefinitionPath(content.Paths.QuestsDirectory, "quest.wolves_at_the_gate"))
                        .Replace("quest.wolves_at_the_gate", "quest.pulled_hunt"));

                var stopped = service.Validate(workspace);

                Assert.That(stopped.Success, Is.False);
                Assert.That(stopped.Message, Does.Contain("changed on disk").And.Contain("quests/definitions/quest.pulled_hunt.json"));
                Assert.Throws<System.InvalidOperationException>(() => service.Save(workspace, validated));
                Assert.That(File.Exists(pulledPath), Is.True);
                Assert.That(QuestEditorContent.Load(content.Paths.QuestsDirectory).retiredQuestIds, Is.Empty);

                var reloaded = service.Load();
                Assert.That(service.Validate(reloaded).Success, Is.True);
            }
        }

        // A cycle among requirements names the quest it starts from and its requirements, so
        // Quest Studio marks that field.
        [Test]
        public void ARequirementCycleIsAFindingAboutTheRequirementsOfAQuest()
        {
            var workspace = new QuestEditorService(QuestEditorPaths.CreateDefault()).Load();
            var catalog = QuestEditorJson.Clone(workspace.Catalog);
            var wolves = catalog.quests.Single(quest => quest.id == "quest.wolves_at_the_gate");
            wolves.requiredQuestIds = new[] { "quest.pelts_and_fangs" };

            var issues = QuestEditorIssues.Collect(catalog, workspace.References);

            Assert.That(issues, Has.Some.Matches<ContentIssue>(issue =>
                issue.Field == "requiredQuestIds"
                && issue.Message.Contains("form a cycle")
                && (issue.EntityKey == "quest.wolves_at_the_gate" || issue.EntityKey == "quest.pelts_and_fangs")));
        }

        [Test]
        public void TheMaximumOfActiveQuestsIsAFindingAboutItsSetting()
        {
            var workspace = new QuestEditorService(QuestEditorPaths.CreateDefault()).Load();
            var catalog = QuestEditorJson.Clone(workspace.Catalog);
            catalog.maximumActiveQuests = 0;

            var issue = QuestEditorIssues.Collect(catalog, workspace.References).Single();

            Assert.That((issue.EntityKey, issue.Field), Is.EqualTo((string.Empty, QuestEditorIssues.MaximumActiveQuests)));
        }

        // A changed setting shows also when quests changed, and a committed catalog that cannot
        // be read, such as one with merge markers, is written again.
        [Test]
        public void ASaveListsChangedSettingsAndRepairsAnUnreadableCatalog()
        {
            using (var content = new FixtureQuestContent())
            {
                var service = new QuestEditorService(content.Paths);
                var workspace = service.Load();
                workspace.Catalog.maximumActiveQuests = 20;
                workspace.Catalog.quests.Single(quest => quest.id == "quest.wolves_at_the_gate").title = "Wolves at the Old Gate";

                var changes = service.Validate(workspace).Changes;

                Assert.That(changes, Is.EqualTo(new[] { "Changed quest: quest.wolves_at_the_gate", "Changed quest catalog settings" }));

                File.WriteAllText(content.Paths.RuntimePath, "<<<<<<< HEAD\n{}\n=======\n{}\n>>>>>>> main\n");
                var repair = service.Validate(workspace);
                Assert.That(repair.Success, Is.True, repair.Message);
                Assert.That(repair.Changes, Is.EqualTo(new[] { "Wrote the committed quest catalog again, which could not be read" }));
                service.Save(workspace, repair);
                Assert.That(service.VerifyCanonical().Success, Is.True);
            }
        }

        // The quest compiler rejects a field the content model does not know, which the editor's
        // own load drops, so Verify Canonical names the file.
        [Test]
        public void VerifyCanonicalNamesAQuestFileWithAnUnknownField()
        {
            using (var content = new FixtureQuestContent())
            {
                var path = ContentLayout.DefinitionPath(content.Paths.QuestsDirectory, "quest.wolves_at_the_gate");
                File.WriteAllText(path, File.ReadAllText(path).Replace("\"title\":", "\"titel\": \"Typo\",\n    \"title\":"));
                var service = new QuestEditorService(content.Paths);

                Assert.That(service.NonCanonicalFiles(), Is.EqualTo(new[] { "definitions/quest.wolves_at_the_gate.json" }));
                var verify = service.VerifyCanonical();
                Assert.That(verify.Success, Is.False);
                Assert.That(verify.Output, Does.Contain("definitions/quest.wolves_at_the_gate.json"));
            }
        }

        private static string CommittedRevision(QuestEditorPaths paths)
        {
            return JsonUtility.FromJson<QuestCatalogJsonDocument>(File.ReadAllText(paths.RuntimePath)).revision;
        }

        private static string ReadNormalized(string path)
        {
            return ContentFileTransaction.NormalizeLineEndings(File.ReadAllText(path));
        }
    }
}
