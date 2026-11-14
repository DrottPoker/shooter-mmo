using System;
using Mmorpg.Client.Core;
using Mmorpg.Client.Editor;
using Mmorpg.Client.Tests.EditMode;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.TestTools;
using UnityEngine;

[assembly: TestPlayerBuildModifier(typeof(ClientTestPlayerBuild))]

namespace Mmorpg.Client.Tests.EditMode
{
    // Makes a test player of the Unity Test Framework compile like a player build of one kind.
    // tools/Run-UnityTests.ps1 passes -clientTestPlayerBuild development|release with a player
    // run: the test player then gets that kind's IL2CPP settings from ClientBuildKindSettings and
    // the C# code variant of a shipped build instead of the test runner's checked variant, which
    // adds assertions and checks that a player never runs. It stays a development build with the
    // test assemblies, because the test runner reports the results through the player connection
    // of a development build. Without the argument, as in the Editor's Test Runner, it changes
    // nothing. The settings last for the headless run, whose script puts the project settings
    // back afterwards.
    public sealed class ClientTestPlayerBuild : ITestPlayerBuildModifier
    {
        public const string Argument = "-clientTestPlayerBuild";

        public BuildPlayerOptions ModifyOptions(BuildPlayerOptions playerOptions)
        {
            var arguments = Environment.GetCommandLineArgs();
            var kindName = string.Empty;
            for (var index = 0; index + 1 < arguments.Length; index++)
            {
                if (string.Equals(arguments[index], Argument, StringComparison.Ordinal))
                {
                    kindName = arguments[index + 1];
                }
            }

            if (kindName.Length == 0)
            {
                return playerOptions;
            }

            ClientBuildKind kind;
            if (kindName == ClientBuildInfo.DevelopmentKindName)
            {
                kind = ClientBuildKind.Development;
            }
            else if (kindName == ClientBuildInfo.ReleaseKindName)
            {
                kind = ClientBuildKind.Release;
            }
            else
            {
                throw new ArgumentException(Argument + " takes development or release, not '" + kindName + "'.");
            }

            var settings = ClientBuildKindSettings.For(kind);
            _ = settings.Apply();
            PlayerSettings.SetManagedCodeVariant(NamedBuildTarget.Standalone, ManagedCodeVariant.Release);
            Debug.Log("[BUILD] The test player compiles like a " + kindName + " build: "
                + settings.ScriptingBackend + ", " + settings.StrippingLevel + " stripping, "
                + settings.CodeGeneration + ", C++ " + settings.CompilerConfiguration
                + ", the Release code variant.");
            return playerOptions;
        }
    }
}
