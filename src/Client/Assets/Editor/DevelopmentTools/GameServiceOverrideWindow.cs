using Mmorpg.Client.Core;
using UnityEditor;
using UnityEngine;

namespace Mmorpg.Client.Editor
{
    // The Editor's switch for the development override of the GameService address: Play Mode then
    // talks to another GameService than the local environment's, such as one on another machine.
    // It stores the address in the Editor's PlayerPrefs, which the client reads at the next start
    // of Play Mode; the login screen says that the address is overridden.
    public sealed class GameServiceOverrideWindow : EditorWindow
    {
        private const string MenuPath = "MMORPG/Tools/GameService Address Override";

        private string address = string.Empty;

        [MenuItem(MenuPath)]
        private static void Open()
        {
            var window = GetWindow<GameServiceOverrideWindow>(true, "GameService Address Override");
            window.minSize = new Vector2(420f, 150f);
            window.address = PlayerPrefs.GetString(ClientAddressOverride.EditorPreference, string.Empty);
            window.Show();
        }

        private void OnGUI()
        {
            var current = PlayerPrefs.GetString(ClientAddressOverride.EditorPreference, string.Empty);
            EditorGUILayout.HelpBox(
                current.Length == 0
                    ? "Play Mode uses the address of Environments/local.json."
                    : "Play Mode uses " + current + " from its next start.",
                MessageType.Info);
            address = EditorGUILayout.TextField("GameService address", address);
            var valid = ClientEnvironment.TryNormalizeAddress(address, out var normalized);
            if (address.Length > 0 && !valid)
            {
                EditorGUILayout.HelpBox("Enter an absolute http or https URL, such as http://192.168.1.20:5000.", MessageType.Warning);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(!valid))
                {
                    if (GUILayout.Button("Use This Address"))
                    {
                        Save(normalized);
                    }
                }

                using (new EditorGUI.DisabledScope(current.Length == 0))
                {
                    if (GUILayout.Button("Use The Local Environment"))
                    {
                        address = string.Empty;
                        Save(string.Empty);
                    }
                }
            }
        }

        private static void Save(string value)
        {
            if (value.Length == 0)
            {
                PlayerPrefs.DeleteKey(ClientAddressOverride.EditorPreference);
            }
            else
            {
                PlayerPrefs.SetString(ClientAddressOverride.EditorPreference, value);
            }

            PlayerPrefs.Save();
            Debug.Log(value.Length == 0
                ? "Play Mode uses the local environment's GameService address from its next start."
                : "Play Mode uses the GameService at " + value + " from its next start.");
        }
    }
}
