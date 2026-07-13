using ShooterMmo.Gameplay;
using UnityEngine;

namespace ShooterMmo.Ui
{
    [DisallowMultipleComponent]
    public sealed class CrosshairPanel : MonoBehaviour
    {
        private CrosshairController controller;

        private void Start()
        {
            controller = FindAnyObjectByType<CrosshairController>();
        }

        private void Update()
        {
            if (controller == null)
            {
                controller = FindAnyObjectByType<CrosshairController>();
            }
        }

        private void OnGUI()
        {
            if (controller == null || Cursor.lockState != CursorLockMode.Locked)
            {
                return;
            }

            var definition = controller.CurrentDefinition;
            var center = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            if (definition.Shape == CrosshairShape.Dot)
            {
                DrawRectangle(
                    new Rect(
                        center.x - (definition.Size * 0.5f),
                        center.y - (definition.Size * 0.5f),
                        definition.Size,
                        definition.Size),
                    definition.Color);
                return;
            }

            DrawCross(center, definition, controller.DynamicSpread);
        }

        private static void DrawCross(
            Vector2 center,
            CrosshairDefinition definition,
            float dynamicSpread)
        {
            var gap = definition.Gap + dynamicSpread;
            var length = definition.Size;
            var thickness = definition.Thickness;
            var halfThickness = thickness * 0.5f;

            DrawRectangle(
                new Rect(center.x - halfThickness, center.y - gap - length, thickness, length),
                definition.Color);
            DrawRectangle(
                new Rect(center.x - halfThickness, center.y + gap, thickness, length),
                definition.Color);
            DrawRectangle(
                new Rect(center.x - gap - length, center.y - halfThickness, length, thickness),
                definition.Color);
            DrawRectangle(
                new Rect(center.x + gap, center.y - halfThickness, length, thickness),
                definition.Color);
        }

        private static void DrawRectangle(Rect rectangle, Color color)
        {
            var previousColor = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rectangle, Texture2D.whiteTexture);
            GUI.color = previousColor;
        }
    }
}
