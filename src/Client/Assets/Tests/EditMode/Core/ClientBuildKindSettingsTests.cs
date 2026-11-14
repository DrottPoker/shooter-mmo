using System.IO;
using Mmorpg.Client.Core;
using Mmorpg.Client.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace Mmorpg.Client.Tests.EditMode
{
    // The player builds are IL2CPP. The project settings hold the release kind, so a build from
    // the Build Profiles window and a test player compile like a release build.
    public sealed class ClientBuildKindSettingsTests
    {
        [Test]
        public void TheProjectSettingsAreTheReleaseBuildsSettings()
        {
            var target = NamedBuildTarget.Standalone;
            var release = ClientBuildKindSettings.Release;
            Assert.That(PlayerSettings.GetScriptingBackend(target), Is.EqualTo(ScriptingImplementation.IL2CPP));
            Assert.That(PlayerSettings.GetManagedStrippingLevel(target), Is.EqualTo(release.StrippingLevel));
            Assert.That(PlayerSettings.GetIl2CppCodeGeneration(target), Is.EqualTo(release.CodeGeneration));
            Assert.That(PlayerSettings.GetIl2CppCompilerConfiguration(target), Is.EqualTo(release.CompilerConfiguration));
        }

        // A development build builds faster, but strips exactly what a release build strips, so
        // the tests in development players prove the stripping that ships.
        [Test]
        public void ADevelopmentBuildDiffersOnlyInHowItCompiles()
        {
            var development = ClientBuildKindSettings.Development;
            var release = ClientBuildKindSettings.Release;
            Assert.That(development.ScriptingBackend, Is.EqualTo(ScriptingImplementation.IL2CPP));
            Assert.That(development.StrippingLevel, Is.EqualTo(release.StrippingLevel));
            Assert.That(development.CodeGeneration, Is.EqualTo(Il2CppCodeGeneration.OptimizeSize));
            Assert.That(development.CompilerConfiguration, Is.EqualTo(Il2CppCompilerConfiguration.Release));
            Assert.That(development.Options & BuildOptions.Development, Is.EqualTo(BuildOptions.Development));
            Assert.That(release.Options & BuildOptions.Development, Is.EqualTo(BuildOptions.None));
            Assert.That(ClientBuildKindSettings.For(ClientBuildKind.Development), Is.SameAs(development));
            Assert.That(ClientBuildKindSettings.For(ClientBuildKind.Release), Is.SameAs(release));
        }

        // A build that tools/Build-Client.ps1 did not start, from the Build Profiles window or a
        // test player, bakes the local environment, the same file the Editor runs.
        [Test]
        public void ABuildThatNamesNoEnvironmentBakesTheLocalOne()
        {
            var local = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Environments", "local.json"));
            Assert.That(ClientEnvironment.TryParse(File.ReadAllText(local), ClientEnvironment.LocalName, out var expected, out _), Is.True);

            var info = ClientBuildFile.DefaultBuild(ClientBuildKind.Development);
            Assert.That(info.Environment.Name, Is.EqualTo(ClientEnvironment.LocalName));
            Assert.That(info.Environment.GameServiceBaseUrl, Is.EqualTo(expected.GameServiceBaseUrl));
            Assert.That(info.Stamp.Kind, Is.EqualTo(ClientBuildKind.Development));
            Assert.That(ClientBuildInfo.TryParse(info.ToJson(), out _, out var error), Is.True, error);
        }
    }
}
