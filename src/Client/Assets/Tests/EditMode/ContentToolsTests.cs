using System;
using System.Linq;
using Mmorpg.WorldData.Editor.ContentTools;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Mmorpg.Client.Tests.EditMode
{
    public sealed class ContentToolsTests
    {
        private ContentUndoState undoState;

        [SetUp]
        public void CreateUndoState()
        {
            undoState = ContentUndoState.Create("Content Tools Test");
        }

        [TearDown]
        public void DestroyUndoState()
        {
            Undo.ClearUndo(undoState);
            UnityEngine.Object.DestroyImmediate(undoState);
        }

        [Test]
        public void SnapshotKeepsNullApartFromEmptyAndFloatsExact()
        {
            var document = new TestDocument
            {
                entries = new[]
                {
                    new TestEntry { id = "a", weight = 0.55f, nested = null, tags = new string[0] },
                    new TestEntry { id = "b", weight = 1f / 3f, nested = new TestNested(), tags = null }
                }
            };

            var restored = ContentSnapshot.Restore<TestDocument>(ContentSnapshot.Capture(document));

            Assert.That(restored.entries[0].nested, Is.Null);
            Assert.That(restored.entries[0].tags, Is.Empty);
            Assert.That(restored.entries[1].nested, Is.Not.Null);
            Assert.That(restored.entries[1].tags, Is.Null);
            Assert.That(restored.entries[0].weight, Is.EqualTo(0.55f));
            Assert.That(restored.entries[1].weight, Is.EqualTo(1f / 3f));
        }

        [Test]
        public void EveryChangeIsOneUndoStepAndUndoRestoresTheContent()
        {
            var holder = new Holder { Document = CreateDocument() };
            using (var session = CreateSession(holder))
            {
                session.Load(false);
                Assert.That(session.HasUnsavedChanges, Is.False);

                session.Change("Weight", () => holder.Document.entries[0].weight = 2f);
                session.Change("Name", () => holder.Document.entries[1].id = "renamed");
                Assert.That(session.HasUnsavedChanges, Is.True);

                Undo.PerformUndo();
                Assert.That(holder.Document.entries[1].id, Is.EqualTo("b"));
                Assert.That(holder.Document.entries[0].weight, Is.EqualTo(2f));

                Undo.PerformUndo();
                Assert.That(holder.Document.entries[0].weight, Is.EqualTo(1f));
                Assert.That(session.HasUnsavedChanges, Is.False);

                Undo.PerformRedo();
                Assert.That(holder.Document.entries[0].weight, Is.EqualTo(2f));
                Assert.That(session.HasUnsavedChanges, Is.True);
            }
        }

        [Test]
        public void ChangesThatLeaveTheContentEqualRecordNothing()
        {
            var holder = new Holder { Document = CreateDocument() };
            using (var session = CreateSession(holder))
            {
                var raised = 0;
                session.Load(false);
                session.Changed += () => raised++;

                session.Change("Same weight", () => holder.Document.entries[0].weight = 1f);

                Assert.That(raised, Is.Zero);
                Assert.That(session.HasUnsavedChanges, Is.False);
            }
        }

        [Test]
        public void PendingEditsSurviveANewSessionUntilTheyAreDiscarded()
        {
            var holder = new Holder { Document = CreateDocument() };
            using (var session = CreateSession(holder))
            {
                session.Load(false);
                session.Change("Weight", () => holder.Document.entries[0].weight = 5f);
            }

            var reloaded = new Holder { Document = CreateDocument() };
            using (var session = CreateSession(reloaded))
            {
                session.Load(true);
                Assert.That(reloaded.Document.entries[0].weight, Is.EqualTo(5f));
                Assert.That(session.HasUnsavedChanges, Is.True);
            }

            var discarded = new Holder { Document = CreateDocument() };
            using (var session = CreateSession(discarded))
            {
                session.Load(false);
                Assert.That(discarded.Document.entries[0].weight, Is.EqualTo(1f));
                Assert.That(session.HasUnsavedChanges, Is.False);
            }
        }

        [Test]
        public void MarkSavedMakesTheCurrentContentTheBaseline()
        {
            var holder = new Holder { Document = CreateDocument() };
            using (var session = CreateSession(holder))
            {
                session.Load(false);
                session.Change("Weight", () => holder.Document.entries[0].weight = 3f);
                session.MarkSaved();

                Assert.That(session.HasUnsavedChanges, Is.False);
            }
        }

        [TestCase("definitions[3].unitWeight must not be negative.", "definitions", 3, "unitWeight", "unitWeight must not be negative.")]
        [TestCase("definitions[0].bag.slots[1].kind is invalid.", "definitions", 0, "bag.slots[1].kind", "bag.slots[1].kind is invalid.")]
        [TestCase("actors[12] must not be null.", "actors", 12, "", "must not be null.")]
        [TestCase("entries[2].iconResourcePath must be a relative Unity Resources path.", "entries", 2, "iconResourcePath", "iconResourcePath must be a relative Unity Resources path.")]
        public void CompilerErrorPathsAreParsed(string error, string collection, int index, string field, string detail)
        {
            Assert.That(ContentIssuePath.TryParse(error, out var parsedCollection, out var parsedIndex, out var parsedField, out var parsedDetail), Is.True);
            Assert.That(parsedCollection, Is.EqualTo(collection));
            Assert.That(parsedIndex, Is.EqualTo(index));
            Assert.That(parsedField, Is.EqualTo(field));
            Assert.That(parsedDetail, Is.EqualTo(detail));
        }

        [TestCase("definitions contains duplicate id 'sword'.")]
        [TestCase("formatVersion is unsupported.")]
        public void ErrorsWithoutAnEntryPathAreNotParsed(string error)
        {
            Assert.That(ContentIssuePath.TryParse(error, out _, out _, out _, out var detail), Is.False);
            Assert.That(detail, Is.EqualTo(error));
        }

        [Test]
        public void CompilerErrorsBecomeFindingsAboutTheirEntry()
        {
            var mapped = ContentIssuePath.ToIssue(
                "definitions[1].unitWeight must not be negative.",
                "definitions",
                index => index == 1 ? "vest" : null,
                "detail.");
            var duplicate = ContentIssuePath.ToIssue("definitions contains duplicate id 'sword'.", "definitions", _ => null);
            var unrelated = ContentIssuePath.ToIssue("spawnPoints[0].actorId is unknown.", "definitions", _ => "never");

            Assert.That(mapped.EntityKey, Is.EqualTo("vest"));
            Assert.That(mapped.Field, Is.EqualTo("detail.unitWeight"));
            Assert.That(mapped.Message, Is.EqualTo("unitWeight must not be negative."));
            Assert.That(duplicate.EntityKey, Is.EqualTo("sword"));
            Assert.That(duplicate.Field, Is.EqualTo("id"));
            Assert.That(unrelated.EntityKey, Is.Empty);
            Assert.That(unrelated.Message, Is.EqualTo("spawnPoints[0].actorId is unknown."));
        }

        [Test]
        public void IssuesConcernTheirFieldAndTheFieldsAroundIt()
        {
            var issue = new ContentIssue("sword", "bag.slots[0].kind", "kind is invalid.");

            Assert.That(issue.Concerns("bag"), Is.True);
            Assert.That(issue.Concerns("bag.slots[0].kind"), Is.True);
            Assert.That(issue.Concerns("bag.slots[0].kindName"), Is.False);
            Assert.That(issue.Concerns("bagCount"), Is.False);
        }

        [Test]
        public void BulkFieldsShowMixedValuesAndEditEverySelectedEntryInOneStep()
        {
            var holder = new Holder { Document = CreateDocument() };
            using (var session = CreateSession(holder))
            {
                session.Load(false);
                var form = new ContentForm<TestEntry>(session, entry => entry.id);
                var weight = form.Float("Weight", "weight", entry => entry.weight, (entry, value) => entry.weight = value);
                session.Changed += form.Refresh;
                form.SetTargets(holder.Document.entries);

                Assert.That(weight.showMixedValue, Is.True);

                Assert.That(form.TrySetValue("weight", 4f), Is.True);
                Assert.That(holder.Document.entries.Select(entry => entry.weight), Is.All.EqualTo(4f));

                form.SetTargets(holder.Document.entries);
                Assert.That(weight.showMixedValue, Is.False);
                Assert.That(weight.value, Is.EqualTo(4f));

                Undo.PerformUndo();
                Assert.That(holder.Document.entries.Select(entry => entry.weight), Is.EqualTo(new[] { 1f, 2f }));
            }
        }

        [Test]
        public void VectorFieldsEditOnlyTheChangedComponentOfEverySelectedEntry()
        {
            var holder = new Holder { Document = CreateDocument() };
            holder.Document.entries[0].Position = new Vector3(1f, 2f, 3f);
            holder.Document.entries[1].Position = new Vector3(4f, 5f, 3f);
            using (var session = CreateSession(holder))
            {
                session.Load(false);
                var form = new ContentForm<TestEntry>(session, entry => entry.id);
                var field = form.Vector3("Position", "position", entry => entry.Position, (entry, value) => entry.Position = value);
                form.SetTargets(holder.Document.entries);
                var parts = field.Query<FloatField>().ToList();

                Assert.That(parts.Select(part => part.showMixedValue), Is.EqualTo(new[] { true, true, false }));

                form.TrySetValue("position", new Vector3(1f, 9f, 3f));

                Assert.That(holder.Document.entries.Select(entry => entry.Position), Is.EqualTo(new[] { new Vector3(1f, 9f, 3f), new Vector3(4f, 9f, 3f) }));
            }
        }

        [Test]
        public void ContinuousChangesInOneUndoGroupUndoAtOnce()
        {
            var holder = new Holder { Document = CreateDocument() };
            using (var session = CreateSession(holder))
            {
                session.Load(false);
                Undo.IncrementCurrentGroup();
                session.ChangeContinuously("Drag", () => holder.Document.entries[0].weight = 7f);
                session.ChangeContinuously("Drag", () => holder.Document.entries[0].weight = 8f);
                session.ChangeContinuously("Drag", () => holder.Document.entries[0].weight = 9f);

                Undo.PerformUndo();

                Assert.That(holder.Document.entries[0].weight, Is.EqualTo(1f));
                Assert.That(session.HasUnsavedChanges, Is.False);
            }
        }

        [Test]
        public void FieldsForSomeEntriesShowAndEditOnlyThose()
        {
            var holder = new Holder { Document = CreateDocument() };
            holder.Document.entries[0].weight = 5f;
            using (var session = CreateSession(holder))
            {
                session.Load(false);
                var form = new ContentForm<TestEntry>(session, entry => entry.id);
                var weight = form.Float("Weight", "weight", entry => entry.weight, (entry, value) => entry.weight = value, entry => entry.id == "b");
                form.SetTargets(holder.Document.entries);

                Assert.That(weight.showMixedValue, Is.False);
                Assert.That(weight.value, Is.EqualTo(2f));

                form.TrySetValue("weight", 3f);
                Assert.That(holder.Document.entries.Select(entry => entry.weight), Is.EqualTo(new[] { 5f, 3f }));
            }
        }

        [Test]
        public void FlagFieldsAddAndRemoveAValueForEverySelectedEntry()
        {
            var holder = new Holder { Document = CreateDocument() };
            holder.Document.entries[0].tags = new[] { "b" };
            holder.Document.entries[1].tags = new string[0];
            using (var session = CreateSession(holder))
            {
                session.Load(false);
                var form = new ContentForm<TestEntry>(session, entry => entry.id);
                form.Flags(
                    "Tags",
                    "tags",
                    () => new[] { ("a", "A"), ("b", "B") },
                    entry => entry.tags,
                    (entry, value) => entry.tags = value,
                    "No tags.");
                form.SetTargets(holder.Document.entries);

                form.TrySetFlag("tags", "a", true);
                Assert.That(holder.Document.entries[0].tags, Is.EqualTo(new[] { "a", "b" }));
                Assert.That(holder.Document.entries[1].tags, Is.EqualTo(new[] { "a" }));

                form.TrySetFlag("tags", "b", false);
                Assert.That(holder.Document.entries.Select(entry => entry.tags), Is.All.EqualTo(new[] { "a" }));
            }
        }

        [Test]
        public void FieldsShowFindingsAboutTheirCoveredPathsAndKeepTheirOwnTooltip()
        {
            var holder = new Holder { Document = CreateDocument() };
            using (var session = CreateSession(holder))
            {
                session.Load(false);
                var form = new ContentForm<TestEntry>(session, entry => entry.id);
                var position = form.Vector3("Position", "position", _ => UnityEngine.Vector3.zero, (_, _) => { });
                var weight = form.Float("Weight", "weight", entry => entry.weight, (entry, value) => entry.weight = value);
                weight.tooltip = "Whole units.";
                form.Covers("position", "x", "y", "z");
                form.SetTargets(new[] { holder.Document.entries[0] });

                form.ApplyIssues(new[]
                {
                    new ContentIssue("a", "y", "y must be finite."),
                    new ContentIssue("a", "weight", "weight must not be negative.")
                });

                Assert.That(position.ClassListContains(ContentToolStyles.FieldError), Is.True);
                Assert.That(position.tooltip, Is.EqualTo("y must be finite."));
                Assert.That(form.FindField("y"), Is.SameAs(position));
                Assert.That(weight.tooltip, Is.EqualTo("weight must not be negative."));

                form.ApplyIssues(new ContentIssue[0]);

                Assert.That(position.ClassListContains(ContentToolStyles.FieldError), Is.False);
                Assert.That(weight.tooltip, Is.EqualTo("Whole units."));
            }
        }

        [Test]
        public void ListPaneFiltersSearchesAndKeepsTheSelectionByKey()
        {
            var pane = new ContentListPane<TestEntry>("Entries", entry => entry.id, entry => entry.id, entry => "weight " + entry.weight);
            var raised = 0;
            pane.SelectionChanged += () => raised++;
            var first = CreateDocument().entries;
            pane.SetItems(first);
            pane.Select(new[] { "b" });

            var replaced = CreateDocument().entries;
            pane.SetItems(replaced);

            Assert.That(pane.Selection.Single(), Is.SameAs(replaced[1]));
            Assert.That(raised, Is.GreaterThanOrEqualTo(2));

            pane.SearchText = "weight 1";
            Assert.That(pane.Shown.Select(entry => entry.id), Is.EqualTo(new[] { "a" }));
            Assert.That(pane.Selection, Is.Empty);

            pane.SearchText = string.Empty;
            pane.AddToggleFilter("Heavy", entry => entry.weight > 1f).SetValueWithoutNotify(true);
            pane.Refresh();
            Assert.That(pane.Shown.Select(entry => entry.id), Is.EqualTo(new[] { "b" }));
        }

        private ContentEditSession<TestDocument> CreateSession(Holder holder)
        {
            return new ContentEditSession<TestDocument>(
                undoState,
                () => holder.Document,
                snapshot => holder.Document = snapshot);
        }

        private static TestDocument CreateDocument()
        {
            return new TestDocument
            {
                entries = new[]
                {
                    new TestEntry { id = "a", weight = 1f, tags = new string[0] },
                    new TestEntry { id = "b", weight = 2f, tags = new string[0] }
                }
            };
        }

        private sealed class Holder
        {
            public TestDocument Document;
        }

        [Serializable]
        public sealed class TestDocument
        {
            public TestEntry[] entries;
        }

        [Serializable]
        public sealed class TestEntry
        {
            public string id;
            public float weight;
            public TestNested nested;
            public string[] tags;
            public float x;
            public float y;
            public float z;

            public Vector3 Position
            {
                get => new Vector3(x, y, z);
                set
                {
                    x = value.x;
                    y = value.y;
                    z = value.z;
                }
            }
        }

        [Serializable]
        public sealed class TestNested
        {
            public int value;
        }
    }
}
