using System.Linq;
using Mmorpg.Client.Ui;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Mmorpg.Client.Tests.EditMode
{
    // The window of the F2 and F3 menus: tabs with rows of a label and a value.
    public sealed class TemporaryMenuWindowTests
    {
        private GameObject parent;
        private GameObject eventSystemObject;
        private TemporaryMenuWindow window;

        [SetUp]
        public void SetUp()
        {
            eventSystemObject = new GameObject("EventSystem", typeof(EventSystem));
            parent = new GameObject("Menu Window Test");
            window = TemporaryMenuWindow.Create(
                parent.transform,
                "TestMenu",
                "Test",
                "F9",
                new Vector2(1f, 1f),
                new Vector2(-16f, -16f),
                new Vector2(470f, 700f),
                false,
                () => { });
        }

        [TearDown]
        public void TearDown()
        {
            // The window's canvas is a child of the parent.
            Object.DestroyImmediate(parent);
            Object.DestroyImmediate(eventSystemObject);
        }

        // A label or a value that does not fit its column wraps. The row then grows, so the
        // wrapped lines never lie on top of the row below.
        [Test]
        public void AValueRowGrowsWithAWrappedLabelAndValue()
        {
            var page = window.AddTab("Page");
            var shortValue = TemporaryMenuWindow.AddValue(page, "Short");
            shortValue.text = "1";
            var wrappedValue = TemporaryMenuWindow.AddValue(page, "Path");
            wrappedValue.text = string.Join(" ", Enumerable.Repeat("measurements", 12));
            var wrappedLabel = TemporaryMenuWindow.AddValue(page, "A label that is far too long for the label column");
            wrappedLabel.text = "2";
            var last = TemporaryMenuWindow.AddValue(page, "Last");
            last.text = "3";

            window.SetVisible(true);
            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(page);

            var shortRow = Row(shortValue);
            var valueRow = Row(wrappedValue);
            var labelRow = Row(wrappedLabel);
            var lastRow = Row(last);
            Assert.That(valueRow.rect.height, Is.GreaterThan(shortRow.rect.height * 1.5f));
            Assert.That(labelRow.rect.height, Is.GreaterThan(shortRow.rect.height * 1.5f));
            Assert.That(Top(labelRow), Is.LessThanOrEqualTo(Bottom(valueRow) + 0.01f));
            Assert.That(Top(lastRow), Is.LessThanOrEqualTo(Bottom(labelRow) + 0.01f));
        }

        private static RectTransform Row(Component value)
        {
            return (RectTransform)value.transform.parent;
        }

        private static float Top(RectTransform rect)
        {
            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            return corners[1].y;
        }

        private static float Bottom(RectTransform rect)
        {
            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            return corners[0].y;
        }
    }
}
