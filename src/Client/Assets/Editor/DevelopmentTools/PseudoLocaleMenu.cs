using Mmorpg.Client.Core;
using UnityEditor;
using UnityEngine;

namespace Mmorpg.Client.Editor
{
    // Switches the pseudo-locale, which shows every text of the text tables with accented
    // vowels. A text that still reads as plain English in Play Mode is a literal in the code.
    public static class PseudoLocaleMenu
    {
        private const string MenuPath = "MMORPG/Tools/Pseudo Locale";

        [MenuItem(MenuPath)]
        private static void Toggle()
        {
            var enabled = PlayerPrefs.GetInt(ClientTexts.PseudoLocalePreference, 0) != 1;
            PlayerPrefs.SetInt(ClientTexts.PseudoLocalePreference, enabled ? 1 : 0);
            PlayerPrefs.Save();
            Debug.Log("The pseudo-locale is " + (enabled ? "on" : "off") + " from the next start of Play Mode.");
        }

        [MenuItem(MenuPath, true)]
        private static bool Validate()
        {
            Menu.SetChecked(MenuPath, PlayerPrefs.GetInt(ClientTexts.PseudoLocalePreference, 0) == 1);
            return true;
        }
    }
}
