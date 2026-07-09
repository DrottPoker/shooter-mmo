using UnityEngine;

namespace ShooterMmo.Ui
{
    public static class TemporaryPanelStyles
    {
        public static Rect GetPanelRect(float width, float height)
        {
            return new Rect(16f, 16f, Mathf.Min(width, Screen.width - 32f), Mathf.Min(height, Screen.height - 32f));
        }

        public static string LabeledTextField(string label, string value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(120f));
            var nextValue = GUILayout.TextField(value);
            GUILayout.EndHorizontal();
            return nextValue;
        }

        public static string LabeledPasswordField(string label, string value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(120f));
            var nextValue = GUILayout.PasswordField(value, '*');
            GUILayout.EndHorizontal();
            return nextValue;
        }

        public static void DrawStatus(bool isBusy, string status)
        {
            GUILayout.Label("Status");
            GUILayout.TextArea(isBusy ? "Working..." : status, GUILayout.MinHeight(80f));
        }
    }
}

