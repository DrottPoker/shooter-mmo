using System;
using System.Collections.Generic;

namespace Mmorpg.Client.Core
{
    // The development override of the GameService address, read once at start: a development
    // player takes it from its command line, the Editor from the switch of the Editor command
    // MMORPG > Tools > GameService Address Override, and a release build ignores both.
    public static class ClientAddressOverride
    {
        // The player's command line argument, followed by the address:
        // MMORPG.exe -gameServiceBaseUrl http://192.168.1.20:5000
        public const string CommandLineArgument = "-gameServiceBaseUrl";

        // The Editor's switch, a PlayerPrefs key of the Editor, read at the start of Play Mode.
        public const string EditorPreference = "Mmorpg.Debug.GameServiceOverride";

        // The address that replaces the environment's for a client of this kind, or empty. The
        // diagnostic says what happened to an override that was asked for, and is empty
        // otherwise.
        public static string Resolve(
            ClientBuildKind kind,
            IReadOnlyList<string> commandLine,
            string editorPreference,
            out string diagnostic)
        {
            var requested = kind == ClientBuildKind.Editor
                ? editorPreference ?? string.Empty
                : FindArgument(commandLine);
            if (requested.Length == 0)
            {
                diagnostic = string.Empty;
                return string.Empty;
            }

            if (kind == ClientBuildKind.Release)
            {
                diagnostic = "A release build ignores the GameService override " + requested + ".";
                return string.Empty;
            }

            if (!ClientEnvironment.TryNormalizeAddress(requested, out var address))
            {
                diagnostic = "The GameService override " + requested
                    + " is no absolute http or https URL and is ignored.";
                return string.Empty;
            }

            diagnostic = "The GameService address is overridden at start: " + address + ".";
            return address;
        }

        // The value after the last occurrence of the argument; an argument without a value
        // counts as an empty override.
        private static string FindArgument(IReadOnlyList<string> commandLine)
        {
            var value = string.Empty;
            if (commandLine == null)
            {
                return value;
            }

            for (var index = 0; index < commandLine.Count; index++)
            {
                if (string.Equals(commandLine[index], CommandLineArgument, StringComparison.OrdinalIgnoreCase))
                {
                    value = index + 1 < commandLine.Count ? commandLine[index + 1] ?? string.Empty : string.Empty;
                }
            }

            return value.Trim();
        }
    }
}
