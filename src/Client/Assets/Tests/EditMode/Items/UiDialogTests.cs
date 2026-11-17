using System.Collections.Generic;
using Mmorpg.Client.Ui;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Mmorpg.Client.Tests.EditMode
{
    // The dialog that asks how many items to split or whether to destroy one.
    public sealed class UiDialogTests
    {
        private GameObject canvasObject;
        private GameObject eventSystemObject;
        private GameObject managerObject;
        private UiTheme theme;
        private UiDialog dialog;

        [SetUp]
        public void SetUp()
        {
            eventSystemObject = new GameObject("EventSystem", typeof(EventSystem));
            canvasObject = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas));
            canvasObject.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            theme = ScriptableObject.CreateInstance<UiTheme>();
            theme.TitleFont = TMP_Settings.defaultFontAsset;
            theme.BodyFont = TMP_Settings.defaultFontAsset;
            dialog = UiDialog.Create(theme, canvasObject.GetComponent<RectTransform>(), FixtureTexts.Ui());
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(canvasObject);
            Object.DestroyImmediate(eventSystemObject);
            if (managerObject != null)
            {
                Object.DestroyImmediate(managerObject);
            }

            Object.DestroyImmediate(theme);
        }

        [Test]
        public void AQuantityStaysInItsRangeAndConfirmingSendsIt()
        {
            int? chosen = null;
            Assert.That(dialog.IsOpen, Is.False);

            dialog.AskQuantity(this, "Split Stack", "Iron Ore (20)", 1, 19, 40, Vector2.zero, quantity => chosen = quantity);

            Assert.That(dialog.IsOpen, Is.True);
            Assert.That(dialog.Title, Is.EqualTo("Split Stack"));
            Assert.That(dialog.Message, Is.EqualTo("Iron Ore (20)"));
            Assert.That(dialog.Quantity, Is.EqualTo(19), "The starting value is clamped to the range.");
            dialog.GetComponentInChildren<TMP_InputField>().text = "0";
            Assert.That(dialog.Quantity, Is.EqualTo(1));
            dialog.GetComponentInChildren<TMP_InputField>().text = "7";

            dialog.Confirm();

            Assert.That(chosen, Is.EqualTo(7));
            Assert.That(dialog.IsOpen, Is.False);
            dialog.Confirm();
            Assert.That(chosen, Is.EqualTo(7), "A closed dialog confirms nothing.");
        }

        [Test]
        public void ClosingAConfirmationRunsNothing()
        {
            var confirmed = false;

            dialog.AskConfirmation(this, "Destroy Item", "Destroy Iron Ore x20?", "Destroy", () => confirmed = true);
            Assert.That(dialog.IsOpen, Is.True);
            Assert.That(dialog.GetComponentInChildren<TMP_InputField>(), Is.Null, "A confirmation asks for no quantity.");
            dialog.Close();

            Assert.That(dialog.IsOpen, Is.False);
            Assert.That(confirmed, Is.False);

            dialog.AskConfirmation(this, "Destroy Item", "Destroy Iron Ore x20?", "Destroy", () => confirmed = true);
            dialog.Confirm();
            Assert.That(confirmed, Is.True);
        }

        // An Escape that dismisses a dialog reaches nothing else: the windows stay open, the world
        // menu stays open, and what the scene does with nothing open, such as ending the attack
        // or closing the interaction, does not run.
        [Test]
        public void AnEscapeThatDismissesADialogReachesNothingElse()
        {
            var manager = NewManager();
            var reached = new List<string>();
            manager.AddEscapeHandler(UiEscapeLevel.ContextMenu, () => Reached(reached, "world menu"));
            manager.AddEscapeHandler(UiEscapeLevel.NothingOpen, () => Reached(reached, "feature"));
            var window = manager.Register("Inventory", UiLayer.Windows, NewCanvas("Inventory"), () => reached.Add("Inventory"));
            window.SetOpen(true);
            var confirmed = false;

            manager.Dialog.AskConfirmation(this, "Destroy Item", "Destroy Iron Ore x20?", "Destroy", () => confirmed = true);
            manager.Press(UiCommand.Cancel, null);

            Assert.That(manager.Dialog.IsOpen, Is.False);
            Assert.That(confirmed, Is.False);
            Assert.That(reached, Is.Empty);
            Assert.That(window.IsOpen, Is.True);
        }

        // A dialog that another window owns, such as the quest log's abandon question, stays open
        // when the inventory windows close their own question, and an Escape closes it alone.
        [Test]
        public void AnEscapeClosesAnotherWindowsDialogAloneAndAnOwnerClosesOnlyItsOwn()
        {
            var manager = NewManager();
            var quests = new object();
            var inventory = new object();
            var reached = new List<string>();
            var log = manager.Register("QuestLog", UiLayer.Windows, NewCanvas("QuestLog"), () => reached.Add("QuestLog"));
            log.SetOpen(true);
            var confirmed = false;

            manager.Dialog.AskConfirmation(quests, "Abandon Quest", "Abandon A Letter for the Outpost?", "Abandon", () => confirmed = true);
            manager.Dialog.CloseFor(inventory);
            Assert.That(manager.Dialog.IsOpen, Is.True, "The inventory windows close only their own question.");

            manager.Press(UiCommand.Cancel, null);
            Assert.That(manager.Dialog.IsOpen, Is.False);
            Assert.That(confirmed, Is.False);
            Assert.That(log.IsOpen, Is.True, "The log behind the dialog stays open.");
            Assert.That(reached, Is.Empty);

            manager.Press(UiCommand.Cancel, null);
            Assert.That(reached, Is.EqualTo(new[] { "QuestLog" }), "The next Escape closes the log.");
        }

        private UiWindowManager NewManager()
        {
            managerObject = new GameObject("Window Manager");
            var manager = managerObject.AddComponent<UiWindowManager>();
            manager.Initialize(theme, FixtureTexts.Ui(), null);
            return manager;
        }

        private Canvas NewCanvas(string name)
        {
            return UiCanvas.Create(managerObject.transform, name, UiLayer.Windows, true);
        }

        private static bool Reached(List<string> reached, string name)
        {
            reached.Add(name);
            return true;
        }
    }
}
