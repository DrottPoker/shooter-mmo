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

        [Test]
        public void VendorModeEditsOffersAndMapsVendorFindingsToTheProfile()
        {
            var view = new ItemCatalogEditorView(
                new ItemCatalogEditorService(ItemCatalogEditorPaths.CreateDefault()),
                undoState,
                LoadCheckedInWorkspace);
            try
            {
                view.Load(false);
                view.ShowVendors(true);
                Assert.That(view.IsShowingVendors, Is.True);
                Assert.That(view.VendorList.Shown.Select(vendor => vendor.id), Is.EqualTo(new[] { "vendor.city_general_store" }));
                view.VendorList.Select(new[] { "vendor.city_general_store" });
                var vendor = view.VendorList.Selection.Single();
                var offers = vendor.offers.Select(offer => offer.definitionId).ToArray();

                Assert.That(view.VendorForm.TrySetValue("purchasePriceBasisPoints", 0), Is.True);
                var finding = view.ValidateLive().Single(issue => issue.EntityKey.StartsWith(ItemCatalogEditorView.VendorIssueKeyPrefix));
                Assert.That(finding.EntityKey, Is.EqualTo(ItemCatalogEditorView.VendorIssueKeyPrefix + "vendor.city_general_store"));
                Assert.That(finding.Field, Is.EqualTo("purchasePriceBasisPoints"));
                Undo.PerformUndo();
                Assert.That(view.ValidateLive(), Is.Empty);

                view.CreateVendor();
                var created = view.VendorList.Selection.Single();
                Assert.That(created.id, Is.EqualTo("vendor.new_vendor"));
                // A vendor that neither sells nor buys anything is reported until it does.
                Assert.That(
                    view.ValidateLive().Select(issue => issue.EntityKey),
                    Is.EqualTo(new[] { ItemCatalogEditorView.VendorIssueKeyPrefix + "vendor.new_vendor" }));
                Assert.That(
                    ItemCatalogEditorContent.CreateFiles(view.Workspace.Authoring).Keys,
                    Does.Contain(Path.Combine("vendors", "vendor.new_vendor.json")));
                Undo.PerformUndo();
                Assert.That(view.Workspace.Authoring.vendors.Select(value => value.id), Is.EqualTo(new[] { "vendor.city_general_store" }));
                Assert.That(view.Workspace.Authoring.vendors.Single().offers.Select(offer => offer.definitionId), Is.EqualTo(offers));
                Assert.That(view.HasUnsavedChanges, Is.False);
            }
            finally
            {
                view.Dispose();
            }
        }

        [Test]
        public void DefinitionValueAndToolKindsAreEditedLikeOtherFields()
        {
            var view = new ItemCatalogEditorView(
                new ItemCatalogEditorService(ItemCatalogEditorPaths.CreateDefault()),
                undoState,
                LoadCheckedInWorkspace);
            try
            {
                view.Load(false);
                view.List.Select(new[] { "tool.starter_pickaxe" });

                Assert.That(view.Form.TrySetValue("value", -5L), Is.True);
                Assert.That(
                    view.ValidateLive().Single(issue => issue.Field == "value").EntityKey,
                    Is.EqualTo("tool.starter_pickaxe"));
                Undo.PerformUndo();

                Assert.That(view.Form.TrySetFlag("toolKinds", "pickaxe", false), Is.True);
                Assert.That(view.Workspace.Authoring.definitions.Single(value => value.id == "tool.starter_pickaxe").toolKinds, Is.Empty);
                Undo.PerformUndo();
                Assert.That(view.ValidateLive(), Is.Empty);
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
