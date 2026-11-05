using Mmorpg.Client.Combat;
using Mmorpg.Client.Ui;
using Mmorpg.Client.WorldActors;
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
            dialog = UiDialog.Create(theme, canvasObject.GetComponent<RectTransform>());
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(canvasObject);
            Object.DestroyImmediate(eventSystemObject);
            Object.DestroyImmediate(theme);
        }

        [Test]
        public void AQuantityStaysInItsRangeAndConfirmingSendsIt()
        {
            int? chosen = null;
            Assert.That(dialog.IsOpen, Is.False);

            dialog.AskQuantity("Split Stack", "Iron Ore (20)", 1, 19, 40, Vector2.zero, quantity => chosen = quantity);

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

            dialog.AskConfirmation("Destroy Item", "Destroy Iron Ore x20?", "Destroy", () => confirmed = true);
            Assert.That(dialog.IsOpen, Is.True);
            Assert.That(dialog.GetComponentInChildren<TMP_InputField>(), Is.Null, "A confirmation asks for no quantity.");
            dialog.Close();

            Assert.That(dialog.IsOpen, Is.False);
            Assert.That(confirmed, Is.False);

            dialog.AskConfirmation("Destroy Item", "Destroy Iron Ore x20?", "Destroy", () => confirmed = true);
            dialog.Confirm();
            Assert.That(confirmed, Is.True);
        }

        // An Escape that dismisses a dialog reaches no feature: the windows take it after the
        // world menu and mark it taken before the features that close an interaction or drop
        // the combat target read it.
        [Test]
        public void AnEscapeThatDismissesADialogReachesNoFeature()
        {
            var windowsOrder = ExecutionOrderOf(typeof(InventoryWindows));
            Assert.That(windowsOrder, Is.GreaterThan(ExecutionOrderOf(typeof(WorldMenuController))));
            foreach (var feature in new[]
                     {
                         typeof(WorldInteractionTargetingController),
                         typeof(CombatTargetingController),
                         typeof(TemporaryAdminMenu)
                     })
            {
                Assert.That(ExecutionOrderOf(feature), Is.GreaterThan(windowsOrder), feature.Name);
            }

            var menuObject = new GameObject("World Menu");
            try
            {
                var menu = menuObject.AddComponent<WorldMenuController>();
                Assert.That(menu.CancelConsumedThisFrame, Is.False);
                menu.ConsumeCancel();
                Assert.That(menu.CancelConsumedThisFrame, Is.True);
            }
            finally
            {
                Object.DestroyImmediate(menuObject);
            }
        }

        // A dialog that another window owns, such as the quest log's abandon question, takes its
        // Escape before the inventory windows close their windows, and the quest log after them
        // stays open.
        [Test]
        public void AnEscapeClosesAnotherWindowsDialogAloneBeforeTheInventoryWindows()
        {
            var escapeOrder = ExecutionOrderOf(typeof(UiDialogEscape));
            Assert.That(escapeOrder, Is.GreaterThan(ExecutionOrderOf(typeof(WorldMenuController))));
            Assert.That(escapeOrder, Is.LessThan(ExecutionOrderOf(typeof(InventoryWindows))));
            Assert.That(ExecutionOrderOf(typeof(QuestWindows)), Is.GreaterThan(ExecutionOrderOf(typeof(InventoryWindows))));

            var menuObject = new GameObject("World Menu");
            try
            {
                var menu = menuObject.AddComponent<WorldMenuController>();
                var escape = menuObject.AddComponent<UiDialogEscape>();
                escape.Initialize(dialog, menu, () => null);
                var confirmed = false;

                Assert.That(escape.HandleCancel(true), Is.False, "A closed dialog leaves the Escape to the windows.");
                Assert.That(menu.CancelConsumedThisFrame, Is.False);

                dialog.AskConfirmation("Abandon Quest", "Abandon A Letter for the Outpost?", "Abandon", () => confirmed = true);
                Assert.That(escape.HandleCancel(false), Is.False);
                Assert.That(dialog.IsOpen, Is.True);

                Assert.That(escape.HandleCancel(true), Is.True);
                Assert.That(dialog.IsOpen, Is.False);
                Assert.That(confirmed, Is.False);
                Assert.That(menu.CancelConsumedThisFrame, Is.True, "The windows and the features leave a taken Escape alone.");

                dialog.AskConfirmation("Abandon Quest", "Abandon A Letter for the Outpost?", "Abandon", () => confirmed = true);
                Assert.That(escape.HandleCancel(true), Is.False, "An Escape that something earlier took leaves the dialog open.");
                Assert.That(dialog.IsOpen, Is.True);
            }
            finally
            {
                Object.DestroyImmediate(menuObject);
            }
        }

        private static int ExecutionOrderOf(System.Type type)
        {
            var attribute = (DefaultExecutionOrder)System.Attribute.GetCustomAttribute(type, typeof(DefaultExecutionOrder));
            return attribute != null ? attribute.order : 0;
        }
    }
}
