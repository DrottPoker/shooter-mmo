using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mmorpg.WorldData.Content;
using Mmorpg.WorldData.Editor.ContentTools;
using Mmorpg.WorldData.Editor.Quests;
using Mmorpg.WorldData.Quests;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.UIElements;

namespace Mmorpg.Client.Tests.EditMode
{
    // Quest Studio against a copy of the checked-in quests: bulk edits, templates, steps and
    // objectives, deletion and renaming with retired ids, the chain and NPC views, and a save.
    public sealed class QuestStudioViewTests
    {
        private const string Mira = "npc.city_services";
        private const string Hale = "npc.outpost_sergeant";
        private const string Wolves = "quest.wolves_at_the_gate";
        private const string Pelts = "quest.pelts_and_fangs";
        private const string Letter = "quest.letter_for_the_outpost";
        private const string Request = "quest.sergeants_request";
        private const string Alpha = "quest.the_alpha";

        private ContentUndoState undoState;
        private FixtureQuestContent content;
        private QuestStudioView view;

        [SetUp]
        public void CreateView()
        {
            undoState = ContentUndoState.Create("Quest Studio View Test");
            content = new FixtureQuestContent();
            view = new QuestStudioView(new QuestEditorService(content.Paths), undoState);
            view.Load(false);
        }

        [TearDown]
        public void DestroyView()
        {
            view.Dispose();
            Undo.ClearUndo(undoState);
            UnityEngine.Object.DestroyImmediate(undoState);
            content.Dispose();
        }

        [Test]
        public void LoadingShowsTheCheckedInQuestsWithoutProblems()
        {
            Assert.That(view.Issues, Is.Empty, string.Join("\n", view.Issues.Select(issue => issue.Message)));
            Assert.That(view.List.Shown.Select(QuestStudioView.Key), Is.EquivalentTo(new[] { Wolves, Pelts, Letter, Request, Alpha }));
            Assert.That(view.List.Selection.Count, Is.EqualTo(1));
            Assert.That(view.StatusText, Does.Contain("Loaded 5 quests"));
            Assert.That(view.HasUnsavedChanges, Is.False);
        }

        [Test]
        public void BulkEditsApplyToEverySelectedQuestAndUndoInOneStep()
        {
            var given = view.Workspace.Catalog.quests.Where(quest => quest.giverActorId == Mira).Select(QuestStudioView.Key).ToArray();
            view.List.Select(given);
            Assert.That(view.List.Selection.Count, Is.EqualTo(3));

            Assert.That(view.Form.TrySetValue("minimumLevel", 0), Is.True);

            Assert.That(
                Problems().Where(issue => issue.Field == "minimumLevel").Select(issue => issue.EntityKey),
                Is.EquivalentTo(given));
            Assert.That(view.HasUnsavedChanges, Is.True);

            Undo.PerformUndo();

            Assert.That(Problems(), Is.Empty);
            Assert.That(view.HasUnsavedChanges, Is.False);
            Assert.That(view.Workspace.Catalog.quests.Select(quest => quest.minimumLevel), Is.All.EqualTo(1));
        }

        [Test]
        public void EveryTemplateCreatesAValidQuestWithAUniqueId()
        {
            foreach (var template in QuestEditorTemplates.All)
            {
                view.AddQuest(template);
                Assert.That(view.List.Selection.Single().id, Is.EqualTo("quest.new_" + template), template);
            }

            view.AddQuest(QuestEditorTemplates.Kill);

            Assert.That(view.List.Selection.Single().id, Is.EqualTo("quest.new_kill_2"));
            Assert.That(Problems().Select(issue => issue.EntityKey + ": " + issue.Message), Is.Empty);
        }

        [Test]
        public void StepsAndObjectivesAreAddedMovedAndRemoved()
        {
            Select(Wolves);

            view.AddStep();
            var steps = Quest(Wolves).steps;
            Assert.That(steps.Select(step => step.id), Is.EqualTo(new[] { "hunt", "step_2" }));
            Assert.That(steps[1].objectives.Single().id, Is.EqualTo("objective_2"));

            view.MoveStep(1, -1);
            Assert.That(Quest(Wolves).steps.Select(step => step.id), Is.EqualTo(new[] { "step_2", "hunt" }));

            view.AddObjective(1);
            Assert.That(Quest(Wolves).steps[1].objectives.Select(objective => objective.id), Is.EqualTo(new[] { "feral_wolves", "objective_3" }));
            view.MoveObjective(1, 1, -1);
            Assert.That(Quest(Wolves).steps[1].objectives.Select(objective => objective.id), Is.EqualTo(new[] { "objective_3", "feral_wolves" }));
            view.RemoveObjective(1, 0);
            view.RemoveStep(0);

            Assert.That(QuestEditorJson.Serialize(Quest(Wolves)), Is.EqualTo(File.ReadAllText(DefinitionPath(Wolves)).Replace("\r\n", "\n")));
            Assert.That(Problems(), Is.Empty);

            // Every step was its own undo step.
            for (var step = 0; step < 6; step++)
            {
                Undo.PerformUndo();
            }

            Assert.That(view.HasUnsavedChanges, Is.False);
        }

        [Test]
        public void ChangingAnObjectiveKindClearsAndFillsItsFields()
        {
            Select(Wolves);
            const string kind = "steps[0].objectives[0].kind";

            Assert.That(view.Form.TrySetValue(kind, QuestObjectiveKindIds.Gather), Is.True);
            Assert.That(Objective().actorIds, Is.EqualTo(new[] { "resource.iron_vein" }));
            Assert.That(Problems(), Is.Empty);

            Assert.That(view.Form.TrySetValue(kind, QuestObjectiveKindIds.TalkTo), Is.True);
            Assert.That(Objective().actorIds, Is.Empty);
            Assert.That(Objective().npcActorId, Is.EqualTo(Hale));
            Assert.That(Objective().count, Is.EqualTo(1));
            Assert.That(Problems(), Is.Empty);

            Assert.That(view.Form.TrySetValue(kind, QuestObjectiveKindIds.Collect), Is.True);
            Assert.That(Objective().npcActorId, Is.Empty);
            Assert.That(Objective().line, Is.Empty);
            Assert.That(Objective().itemId, Is.Not.Empty);
            Assert.That(Problems(), Is.Empty);

            Assert.That(view.Form.TrySetValue(kind, QuestObjectiveKindIds.Deliver), Is.True);
            Assert.That(Objective().npcActorId, Is.EqualTo(Hale));
            Assert.That(Problems(), Is.Empty);

            Assert.That(view.Form.TrySetValue(kind, QuestObjectiveKindIds.Kill), Is.True);
            Assert.That(Objective().itemId, Is.Empty);
            Assert.That(Objective().actorIds.Length, Is.EqualTo(1));
            Assert.That(view.Form.TrySetValue("steps[0].objectives[0].targets", QuestStudioView.MobTag), Is.True);
            Assert.That(Objective().actorIds, Is.Empty);
            Assert.That(Objective().actorTag, Is.Not.Empty);
            Assert.That(Problems(), Is.Empty);
        }

        [Test]
        public void TextsStayOneTrimmedLineAndFindingsNameTheirField()
        {
            Select(Wolves);

            Assert.That(view.Form.TrySetValue("offerText", " The wolves are back.\nHunt five of them. "), Is.True);
            Assert.That(Quest(Wolves).offerText, Is.EqualTo("The wolves are back. Hunt five of them."));

            Assert.That(view.Form.TrySetValue("title", string.Empty), Is.True);
            var finding = Problems().Single();
            Assert.That(finding.EntityKey, Is.EqualTo(Wolves));
            Assert.That(finding.Field, Is.EqualTo("title"));
            Assert.That(view.Form.FindField("title"), Is.Not.Null);
        }

        [Test]
        public void DeletingKeepsRequiredQuestsAndRetiresOnlySavedIds()
        {
            view.AddQuest(QuestEditorTemplates.TalkTo);
            var draft = view.List.Selection.Single().id;
            view.List.Select(new[] { Wolves, Pelts, draft });
            string[] approved = null;

            view.DeleteSelected(ids =>
            {
                approved = ids;
                return true;
            });

            Assert.That(approved, Is.EquivalentTo(new[] { Pelts, draft }));
            Assert.That(view.Workspace.Catalog.quests.Select(QuestStudioView.Key), Does.Contain(Wolves).And.Not.Contain(Pelts).And.Not.Contain(draft));
            Assert.That(view.Workspace.Catalog.retiredQuestIds, Is.EqualTo(new[] { Pelts }));
            Assert.That(view.StatusType, Is.EqualTo(HelpBoxMessageType.Warning));
            Assert.That(view.StatusText, Does.Contain(Wolves + " is required by " + Alpha));
            Assert.That(Problems(), Is.Empty);
        }

        [Test]
        public void RenamingUpdatesRequirementsAndRetiresTheSavedId()
        {
            Assert.That(view.RenameQuest(Wolves, "quest.wolf_hunt", id => id == Wolves), Is.True);

            Assert.That(Quest(Pelts).requiredQuestIds, Is.EqualTo(new[] { "quest.wolf_hunt" }));
            Assert.That(Quest(Alpha).requiredQuestIds, Is.EqualTo(new[] { Request, "quest.wolf_hunt" }));
            Assert.That(view.Workspace.Catalog.retiredQuestIds, Is.EqualTo(new[] { Wolves }));
            Assert.That(view.List.Selection.Single().id, Is.EqualTo("quest.wolf_hunt"));
            Assert.That(Problems(), Is.Empty);

            // A retired id never returns, and a quest that was never saved renames freely.
            Assert.That(view.RenameQuest("quest.wolf_hunt", Wolves, _ => true), Is.False);
            view.AddQuest(QuestEditorTemplates.Kill);
            Assert.That(view.RenameQuest("quest.new_kill", "quest.boar_hunt", _ => throw new AssertionException("A draft has no id to retire.")), Is.True);

            Undo.PerformUndo();
            Undo.PerformUndo();
            Undo.PerformUndo();
            Assert.That(Quest(Pelts).requiredQuestIds, Is.EqualTo(new[] { Wolves }));
            Assert.That(view.Workspace.Catalog.retiredQuestIds, Is.Empty);
        }

        [Test]
        public void ChainAndNpcViewsListTheRelatedQuests()
        {
            var layers = QuestEditorRelations.Chain(view.Workspace.Catalog, Pelts)
                .Select(layer => layer.Select(QuestStudioView.Key).ToArray())
                .ToArray();
            Assert.That(layers, Is.EqualTo(new[]
            {
                new[] { Letter, Wolves },
                new[] { Pelts, Request },
                new[] { Alpha }
            }));

            var mira = QuestEditorRelations.ForNpc(view.Workspace.Catalog, Mira);
            Assert.That(mira.Gives.Select(QuestStudioView.Key), Is.EquivalentTo(new[] { Wolves, Pelts, Letter }));
            Assert.That(mira.Receives.Select(QuestStudioView.Key), Is.EquivalentTo(new[] { Wolves, Pelts }));
            Assert.That(mira.AppearsIn.Select(QuestStudioView.Key), Is.EqualTo(new[] { Request }));

            Select(Pelts);
            Assert.That(view.NpcViewActorId, Is.EqualTo(Mira));
            var nodes = Nodes();
            Assert.That(nodes.Count(node => (string)node.userData == Alpha), Is.EqualTo(1));
            Assert.That(
                nodes.Where(node => node.ClassListContains(QuestStudioView.ChainNodeClass + "--selected")).Select(node => (string)node.userData),
                Is.All.EqualTo(Pelts));

            view.ShowNpc(Hale);
            Assert.That(view.NpcViewActorId, Is.EqualTo(Hale));
            Assert.That(Nodes().Count(node => (string)node.userData == Alpha), Is.EqualTo(3));
        }

        [Test]
        public void SaveWritesTheQuestAndMakesItsIdPermanent()
        {
            view.AddQuest(QuestEditorTemplates.Gather);
            string[] changes = null;

            Assert.That(view.Save(values =>
            {
                changes = values;
                return true;
            }), Is.True, view.StatusText);

            Assert.That(changes, Is.EqualTo(new[] { "Added quest: quest.new_gather" }));
            Assert.That(File.Exists(DefinitionPath("quest.new_gather")), Is.True);
            Assert.That(view.HasUnsavedChanges, Is.False);
            var confirmed = false;
            Assert.That(view.RenameQuest("quest.new_gather", "quest.ore_run", _ => confirmed = true), Is.True);
            Assert.That(confirmed, Is.True);
            Assert.That(view.Workspace.Catalog.retiredQuestIds, Is.EqualTo(new[] { "quest.new_gather" }));
        }

        private void Select(string questId)
        {
            view.List.Select(new[] { questId });
            Assert.That(view.List.Selection.Single().id, Is.EqualTo(questId));
        }

        private QuestEditorDefinition Quest(string questId)
        {
            return view.Workspace.Catalog.quests.Single(quest => quest.id == questId);
        }

        private QuestEditorObjective Objective()
        {
            return Quest(Wolves).steps[0].objectives[0];
        }

        private IReadOnlyList<ContentIssue> Problems()
        {
            return QuestEditorIssues.Collect(view.Workspace.Catalog, view.Workspace.References);
        }

        private List<Button> Nodes()
        {
            return view.Detail.Query<Button>(className: QuestStudioView.ChainNodeClass).ToList();
        }

        private string DefinitionPath(string questId)
        {
            return ContentLayout.DefinitionPath(content.Paths.QuestsDirectory, questId);
        }
    }
}
