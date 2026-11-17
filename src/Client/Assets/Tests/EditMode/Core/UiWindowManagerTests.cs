using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Mmorpg.Client.Ui;
using Mmorpg.WorldData.Editor;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Mmorpg.Client.Tests.EditMode
{
    // The window manager: the layer table, the stack of open windows, menus, and panels, the order
    // in which Escape closes them, the typing guard, and the one dialog host.
    public sealed class UiWindowManagerTests
    {
        // Code that makes a canvas or sets a canvas order.
        private static readonly Regex CanvasCode = new Regex(
            @"typeof\((UnityEngine\.)?(Canvas|CanvasScaler)\)|AddComponent<(UnityEngine\.)?(Canvas|CanvasScaler)>|\.sortingOrder\s*=[^=]|\.overrideSorting\s*=",
            RegexOptions.Compiled);

        // The only scripts below Assets/Scripts that make a canvas or set its order: the kit's
        // canvas builder and the window manager, which take every order from the layer table.
        private static readonly string[] CanvasScripts = { "Ui/Kit/UiLayers.cs", "Ui/Kit/UiWindowManager.cs" };

        private GameObject managerObject;
        private GameObject eventSystemObject;
        private UiTheme theme;
        private UiWindowManager manager;
        private List<string> closed;

        [SetUp]
        public void SetUp()
        {
            eventSystemObject = new GameObject("EventSystem", typeof(EventSystem));
            theme = ScriptableObject.CreateInstance<UiTheme>();
            theme.TitleFont = TMP_Settings.defaultFontAsset;
            theme.BodyFont = TMP_Settings.defaultFontAsset;
            managerObject = new GameObject("Window Manager");
            manager = managerObject.AddComponent<UiWindowManager>();
            manager.Initialize(theme, FixtureTexts.Ui(), null);
            closed = new List<string>();
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(managerObject);
            UnityEngine.Object.DestroyImmediate(eventSystemObject);
            UnityEngine.Object.DestroyImmediate(theme);
        }

        // Every layer has an order of its own, the layers sort in the order the table names them,
        // and a stacked layer's band ends below the next layer.
        [Test]
        public void TheLayerTableHasNoTieAndRoomForEveryStack()
        {
            var layers = (UiLayer[])Enum.GetValues(typeof(UiLayer));
            Assert.That(UiLayers.All, Is.EqualTo(layers.OrderBy(layer => (int)layer).ToArray()));
            var orders = UiLayers.All.Select(UiLayers.SortingOrder).ToArray();
            Assert.That(orders.Distinct().Count(), Is.EqualTo(orders.Length), "No two layers tie.");
            for (var index = 1; index < orders.Length; index++)
            {
                Assert.That(orders[index], Is.GreaterThan(orders[index - 1]), UiLayers.All[index].ToString());
                if (UiLayers.IsStacked(UiLayers.All[index - 1]))
                {
                    Assert.That(
                        UiLayers.StackedOrder(UiLayers.All[index - 1], int.MaxValue),
                        Is.LessThan(orders[index]),
                        UiLayers.All[index - 1] + " leaves room below " + UiLayers.All[index]);
                }
            }

            Assert.That(
                UiLayers.All.Where(UiLayers.IsStacked),
                Is.EqualTo(new[] { UiLayer.Panels, UiLayer.Windows, UiLayer.Menus }));
            Assert.That(orders.Max(), Is.LessThan(short.MaxValue), "A canvas order is a 16-bit number.");

            // The order the player sees, bottom up.
            Assert.That(UiLayers.SortingOrder(UiLayer.WorldNameplates), Is.LessThan(UiLayers.SortingOrder(UiLayer.Hud)));
            Assert.That(UiLayers.SortingOrder(UiLayer.Hud), Is.LessThan(UiLayers.SortingOrder(UiLayer.Panels)));
            Assert.That(UiLayers.SortingOrder(UiLayer.Windows), Is.LessThan(UiLayers.SortingOrder(UiLayer.Menus)));
            Assert.That(UiLayers.SortingOrder(UiLayer.ContextMenu), Is.LessThan(UiLayers.SortingOrder(UiLayer.Dialogs)));
            Assert.That(UiLayers.SortingOrder(UiLayer.Dialogs), Is.LessThan(UiLayers.SortingOrder(UiLayer.DeathOverlay)));
            Assert.That(UiLayers.SortingOrder(UiLayer.DeathOverlay), Is.LessThan(UiLayers.SortingOrder(UiLayer.ReconnectNotice)));
        }

        // Every canvas the game creates comes from the kit's canvas builder or the window manager,
        // which take its order from the layer table, so the table covers every canvas.
        [Test]
        public void OnlyTheKitMakesACanvasOrSetsItsOrder()
        {
            var scripts = Path.Combine(RepositoryLayout.FindRoot(), "src", "Client", "Assets", "Scripts");
            var makers = new List<string>();
            var used = new HashSet<string>();
            foreach (var path in Directory.GetFiles(scripts, "*.cs", SearchOption.AllDirectories))
            {
                var script = Path.GetRelativePath(scripts, path).Replace('\\', '/');
                var lines = File.ReadAllLines(path);
                for (var index = 0; index < lines.Length; index++)
                {
                    if (!CanvasCode.IsMatch(WithoutComment(lines[index])))
                    {
                        continue;
                    }

                    if (CanvasScripts.Contains(script))
                    {
                        used.Add(script);
                    }
                    else
                    {
                        makers.Add(script + ":" + (index + 1));
                    }
                }
            }

            Assert.That(makers, Is.Empty, "Make the canvas with UiCanvas, in a layer of UiLayers.");
            Assert.That(used, Is.EquivalentTo(CanvasScripts));
        }

        // A canvas that UiCanvas makes has the order of its layer and the one scaler setting.
        [Test]
        public void ACanvasTakesItsLayersOrderAndTheOneScalerSetting()
        {
            foreach (var layer in UiLayers.All)
            {
                var canvas = UiCanvas.Create(managerObject.transform, layer.ToString(), layer, false);
                Assert.That(canvas.sortingOrder, Is.EqualTo(UiLayers.SortingOrder(layer)));
                Assert.That(canvas.renderMode, Is.EqualTo(RenderMode.ScreenSpaceOverlay));
                var scaler = canvas.GetComponent<UnityEngine.UI.CanvasScaler>();
                Assert.That(scaler.uiScaleMode, Is.EqualTo(UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize));
                Assert.That(scaler.referenceResolution, Is.EqualTo(new Vector2(1920f, 1080f)));
                Assert.That(scaler.matchWidthOrHeight, Is.EqualTo(0.5f));
                Assert.That(canvas.GetComponent<UnityEngine.UI.GraphicRaycaster>(), Is.Null);
            }
        }

        // Within a layer the newest open entry sorts on top; focusing one brings it up, and
        // closing one takes it off the stack.
        [Test]
        public void TheStackKeepsTheOrderOfOpeningAndFocus()
        {
            var first = Entry("First", UiLayer.Windows);
            var second = Entry("Second", UiLayer.Windows);
            var menu = Entry("Menu", UiLayer.Menus);
            var panel = Entry("Panel", UiLayer.Panels);
            Assert.That(manager.OpenEntries, Is.Empty);
            Assert.That(manager.Top, Is.Null);

            first.SetOpen(true);
            menu.SetOpen(true);
            second.SetOpen(true);
            panel.SetOpen(true);
            Assert.That(Names(), Is.EqualTo(new[] { "First", "Menu", "Second", "Panel" }));
            Assert.That(manager.Top, Is.SameAs(panel));
            Assert.That(first.Canvas.sortingOrder, Is.EqualTo(UiLayers.StackedOrder(UiLayer.Windows, 0)));
            Assert.That(second.Canvas.sortingOrder, Is.EqualTo(UiLayers.StackedOrder(UiLayer.Windows, 1)));
            Assert.That(menu.Canvas.sortingOrder, Is.EqualTo(UiLayers.StackedOrder(UiLayer.Menus, 0)));
            Assert.That(panel.Canvas.sortingOrder, Is.EqualTo(UiLayers.StackedOrder(UiLayer.Panels, 0)));
            Assert.That(menu.Canvas.sortingOrder, Is.GreaterThan(second.Canvas.sortingOrder), "A menu draws over every window.");
            Assert.That(panel.Canvas.sortingOrder, Is.LessThan(first.Canvas.sortingOrder), "A panel draws under every window.");

            first.Focus();
            Assert.That(Names(), Is.EqualTo(new[] { "Menu", "Second", "Panel", "First" }));
            Assert.That(first.Canvas.sortingOrder, Is.GreaterThan(second.Canvas.sortingOrder));

            // Opening an open entry again leaves its place; a closed one does not focus.
            second.SetOpen(true);
            Assert.That(manager.Top, Is.SameAs(first));
            first.SetOpen(false);
            first.Focus();
            Assert.That(Names(), Is.EqualTo(new[] { "Menu", "Second", "Panel" }));
            Assert.That(second.Canvas.sortingOrder, Is.EqualTo(UiLayers.StackedOrder(UiLayer.Windows, 0)));

            first.SetOpen(true);
            Assert.That(manager.Top, Is.SameAs(first));
            first.Remove();
            Assert.That(Names(), Is.EqualTo(new[] { "Menu", "Second", "Panel" }));
            first.SetOpen(true);
            Assert.That(Names(), Is.EqualTo(new[] { "Menu", "Second", "Panel" }), "A removed entry never opens again.");
            Assert.That(() => manager.Register("Notice", UiLayer.Notices, NewCanvas("Notice"), null), Throws.ArgumentException);
        }

        // Each Escape closes one thing, the top-most first: the dialog, the world menu, what rides
        // on the pointer, the newest window, menu, or panel, and only with nothing open what the
        // scene does with nothing open, where every handler runs.
        [Test]
        public void EscapeClosesOneThingPerPressInOrder()
        {
            var contextMenuOpen = true;
            var carrying = true;
            var nothingOpen = new List<string>();
            manager.AddEscapeHandler(UiEscapeLevel.NothingOpen, () => Took(nothingOpen, "target"));
            manager.AddEscapeHandler(UiEscapeLevel.PointerMode, () => Take(ref carrying, "carry"));
            manager.AddEscapeHandler(UiEscapeLevel.ContextMenu, () => Take(ref contextMenuOpen, "context menu"));
            manager.AddEscapeHandler(UiEscapeLevel.NothingOpen, () => Took(nothingOpen, "interaction"));
            var inventory = Entry("Inventory", UiLayer.Windows);
            var debug = Entry("Debug", UiLayer.Menus);
            var log = Entry("Log", UiLayer.Windows);
            inventory.SetOpen(true);
            debug.SetOpen(true);
            log.SetOpen(true);
            manager.Dialog.AskConfirmation(this, "Destroy Item", "Destroy Iron Ore?", "Destroy", () => closed.Add("confirmed"));

            manager.Press(UiCommand.Cancel, null);
            Assert.That(manager.Dialog.IsOpen, Is.False);
            Assert.That(closed, Is.Empty, "The dialog closes alone and confirms nothing.");
            Assert.That(contextMenuOpen, Is.True);

            manager.Press(UiCommand.Cancel, null);
            Assert.That(closed, Is.EqualTo(new[] { "context menu" }));
            manager.Press(UiCommand.Cancel, null);
            Assert.That(closed, Is.EqualTo(new[] { "context menu", "carry" }));
            manager.Press(UiCommand.Cancel, null);
            manager.Press(UiCommand.Cancel, null);
            manager.Press(UiCommand.Cancel, null);
            Assert.That(closed, Is.EqualTo(new[] { "context menu", "carry", "Log", "Debug", "Inventory" }));
            Assert.That(nothingOpen, Is.Empty, "An Escape that closed something does nothing else.");

            manager.Press(UiCommand.Cancel, null);
            Assert.That(nothingOpen, Is.EqualTo(new[] { "target", "interaction" }));
        }

        // A key typed into a text field is text: no hotkey runs while the player types, and
        // Escape leaves a field of an open window first. A field outside every window, such as
        // the login form's, leaves Escape to its screen, and the dialog's own field cancels it.
        [Test]
        public void TheTypingGuardBlocksEveryHotkeyAndEscapeLeavesTheFieldFirst()
        {
            var ran = new List<UiCommand>();
            var hotkeys = ((UiCommand[])Enum.GetValues(typeof(UiCommand))).Where(command => command != UiCommand.Cancel).ToArray();
            foreach (var command in hotkeys)
            {
                var bound = command;
                manager.SetCommand(command, () => ran.Add(bound));
            }

            var window = Entry("Inventory", UiLayer.Windows);
            window.SetOpen(true);
            var nothingOpen = 0;
            manager.AddEscapeHandler(UiEscapeLevel.NothingOpen, () => ++nothingOpen > 0);
            var windowField = Field(window.Canvas.transform);
            var formField = Field(managerObject.transform);

            foreach (var command in hotkeys)
            {
                manager.Press(command, windowField);
                manager.Press(command, formField);
            }

            Assert.That(ran, Is.Empty, "The typing guard blocks every hotkey.");
            manager.Press(UiCommand.Cancel, windowField);
            Assert.That(window.IsOpen, Is.True, "Escape leaves the window's field and closes nothing.");
            Assert.That(closed, Is.Empty);
            manager.Press(UiCommand.Cancel, null);
            Assert.That(closed, Is.EqualTo(new[] { "Inventory" }));

            manager.Press(UiCommand.Cancel, formField);
            Assert.That(nothingOpen, Is.EqualTo(1), "A form's field leaves Escape to the screen.");

            manager.Dialog.AskQuantity(this, "Split Stack", "Iron Ore (20)", 1, 19, 10, Vector2.zero, _ => { });
            manager.Press(UiCommand.Cancel, manager.Dialog.GetComponentInChildren<TMP_InputField>());
            Assert.That(manager.Dialog.IsOpen, Is.False, "Escape in the dialog's field cancels the dialog.");

            foreach (var command in hotkeys)
            {
                manager.Press(command, null);
            }

            Assert.That(ran, Is.EqualTo(hotkeys), "Without typing every hotkey runs its action once.");
            Assert.That(() => manager.SetCommand(UiCommand.Cancel, () => { }), Throws.ArgumentException);
            Action other = () => ran.Add(UiCommand.ToggleQuestLog);
            manager.ClearCommand(UiCommand.ToggleInventory, other);
            manager.Press(UiCommand.ToggleInventory, null);
            Assert.That(ran.Count(command => command == UiCommand.ToggleInventory), Is.EqualTo(2), "Only the bound action clears it.");
        }

        // The scene has one dialog, on a canvas of the Dialogs layer, and an owner closes only
        // its own question.
        [Test]
        public void OneDialogHostServesEveryOwner()
        {
            var dialog = manager.Dialog;
            Assert.That(manager.Dialog, Is.SameAs(dialog));
            Assert.That(managerObject.GetComponentsInChildren<UiDialog>(true), Has.Length.EqualTo(1));
            var canvas = dialog.GetComponentInParent<Canvas>(true);
            Assert.That(canvas.sortingOrder, Is.EqualTo(UiLayers.SortingOrder(UiLayer.Dialogs)));

            var inventory = new object();
            var quests = new object();
            dialog.AskConfirmation(quests, "Abandon Quest", "Abandon it?", "Abandon", () => { });
            Assert.That(dialog.Owner, Is.SameAs(quests));
            dialog.CloseFor(inventory);
            Assert.That(dialog.IsOpen, Is.True, "Another owner leaves the question open.");
            dialog.CloseFor(quests);
            Assert.That(dialog.IsOpen, Is.False);
            Assert.That(dialog.Owner, Is.Null);
        }

        private UiStackEntry Entry(string name, UiLayer layer)
        {
            UiStackEntry entry = null;
            entry = manager.Register(name, layer, NewCanvas(name), () =>
            {
                closed.Add(name);
                entry.SetOpen(false);
            });
            return entry;
        }

        private Canvas NewCanvas(string name)
        {
            return UiCanvas.Create(managerObject.transform, name + "Canvas", UiLayer.Windows, true);
        }

        private TMP_InputField Field(Transform parent)
        {
            return UiFactory.Field(theme, parent, "Field", string.Empty, null, null, TMP_InputField.ContentType.Standard);
        }

        private string[] Names()
        {
            return manager.OpenEntries.Select(entry => entry.Name).ToArray();
        }

        private bool Take(ref bool open, string name)
        {
            if (!open)
            {
                return false;
            }

            open = false;
            closed.Add(name);
            return true;
        }

        private static bool Took(List<string> list, string name)
        {
            list.Add(name);
            return true;
        }

        private static string WithoutComment(string line)
        {
            var comment = line.IndexOf("//", StringComparison.Ordinal);
            return comment < 0 ? line : line.Substring(0, comment);
        }
    }
}
