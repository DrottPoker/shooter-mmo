using System.IO;
using System.Linq;
using Mmorpg.WorldData.Editor.ContentTools;
using Mmorpg.WorldData.Editor.Items;
using Mmorpg.WorldData.Items.Presentation;
using NUnit.Framework;
using UnityEditor;

namespace Mmorpg.Client.Tests.EditMode
{
    public sealed class ItemCatalogEditorViewTests
    {
        private ContentUndoState undoState;

        [SetUp]
        public void CreateUndoState()
        {
            undoState = ContentUndoState.Create("Item Catalog View Test");
        }

        [TearDown]
        public void DestroyUndoState()
        {
            Undo.ClearUndo(undoState);
            UnityEngine.Object.DestroyImmediate(undoState);
        }

        [Test]
        public void ItemCatalogViewMapsLiveFindingsToFieldsAndUndoesBulkEdits()
        {
            var view = new ItemCatalogEditorView(
                new ItemCatalogEditorService(ItemCatalogEditorPaths.CreateDefault()),
                undoState,
                LoadCheckedInWorkspace);
            try
            {
                view.Load(false);
                Assert.That(view.ValidateLive(), Is.Empty, string.Join("\n", view.ValidateLive().Select(issue => issue.Message)));

                var targets = view.Workspace.Authoring.definitions
                    .Where(definition => definition.bag == null)
                    .Take(2)
                    .Select(definition => definition.id)
                    .ToArray();
                view.List.Select(targets);
                Assert.That(view.List.Selection.Count, Is.EqualTo(2));
                var weights = view.List.Selection.Select(definition => definition.unitWeight).ToArray();

                Assert.That(view.Form.TrySetValue("unitWeight", -1L), Is.True);

                var findings = view.ValidateLive();
                Assert.That(
                    findings.Where(issue => issue.Field == "unitWeight").Select(issue => issue.EntityKey),
                    Is.EquivalentTo(targets));
                Assert.That(view.HasUnsavedChanges, Is.True);

                Undo.PerformUndo();
                Assert.That(view.ValidateLive(), Is.Empty);
                Assert.That(view.HasUnsavedChanges, Is.False);
                Assert.That(
                    targets.Select(id => view.Workspace.Authoring.definitions.Single(definition => definition.id == id).unitWeight),
                    Is.EqualTo(weights));
            }
            finally
            {
                view.Dispose();
            }
        }

        [Test]
        public void ItemCatalogViewKeepsBakedDefinitionsWhenRemovingDrafts()
        {
            var view = new ItemCatalogEditorView(
                new ItemCatalogEditorService(ItemCatalogEditorPaths.CreateDefault()),
                undoState,
                LoadCheckedInWorkspace);
            try
            {
                view.Load(false);
                var baked = view.Workspace.Authoring.definitions[0].id;
                view.CreateDefinition();
                var draft = view.List.Selection.Single().id;
                view.List.Select(new[] { baked, draft });

                view.RemoveSelectedDrafts();

                var ids = view.Workspace.Authoring.definitions.Select(definition => definition.id).ToArray();
                Assert.That(ids, Does.Contain(baked));
                Assert.That(ids, Does.Not.Contain(draft));
                Assert.That(view.Workspace.FindPresentation(draft), Is.Null);
            }
            finally
            {
                view.Dispose();
            }
        }

        private static ItemCatalogEditorWorkspace LoadCheckedInWorkspace()
        {
            var paths = ItemCatalogEditorPaths.CreateDefault();
            return new ItemCatalogEditorWorkspace(
                ItemCatalogEditorContent.Load(paths.ItemsDirectory),
                ItemCatalogEditorJson.DeserializeRuntime(File.ReadAllText(paths.RuntimePath)),
                ItemPresentationCatalogJson.Deserialize(File.ReadAllText(paths.PresentationPath)));
        }
    }
}
