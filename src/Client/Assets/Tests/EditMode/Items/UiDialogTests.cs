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
    }
}
