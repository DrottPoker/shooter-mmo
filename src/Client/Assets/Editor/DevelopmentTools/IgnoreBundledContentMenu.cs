using Mmorpg.Client.Core;
using UnityEditor;
using UnityEngine;

namespace Mmorpg.Client.Editor
{
    // Makes the client treat no bundled content file as current, so every file of the content
    // set comes from the cache or a download from GameService. In the Editor the bundled files
    // always equal the served ones, so without it the download path never runs.
    public static class IgnoreBundledContentMenu
    {
        private const string MenuPath = "MMORPG/Tools/Ignore Bundled Content";

        [MenuItem(MenuPath)]
        private static void Toggle()
        {
            var enabled = PlayerPrefs.GetInt(ClientContentResolver.IgnoreBundledContentPreference, 0) != 1;
            PlayerPrefs.SetInt(ClientContentResolver.IgnoreBundledContentPreference, enabled ? 1 : 0);
            PlayerPrefs.Save();
            Debug.Log("Ignore Bundled Content is " + (enabled ? "on" : "off") + " from the next start of Play Mode.");
        }

        [MenuItem(MenuPath, true)]
        private static bool Validate()
        {
            Menu.SetChecked(MenuPath, PlayerPrefs.GetInt(ClientContentResolver.IgnoreBundledContentPreference, 0) == 1);
            return true;
        }
    }
}
