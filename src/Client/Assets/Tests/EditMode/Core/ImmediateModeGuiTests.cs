using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Mmorpg.WorldData.Editor;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Assemblies;

namespace Mmorpg.Client.Tests.EditMode
{
    // The game's interface is uGUI with TextMesh Pro on the UI kit, so no runtime script of the
    // client draws with IMGUI or the legacy uGUI Text: none defines OnGUI or uses GUILayout,
    // GUI., or UnityEngine.UI.Text. Editor tools may use IMGUI; they live outside Assets/Scripts.
    public sealed class ImmediateModeGuiTests
    {
        private static readonly Regex ImmediateModeCode = new Regex(
            @"\bvoid\s+OnGUI\s*\(|\bGUILayout\b|(?<![\w.])GUI\.|\bUnityEngine\.UI\.Text\b(?!\w)",
            RegexOptions.Compiled);

        // The scripts that may still do so, each with its reason. None does today.
        private static readonly (string Script, string Reason)[] Exceptions = Array.Empty<(string, string)>();

        [Test]
        public void NoRuntimeScriptDrawsWithImmediateModeGuiOrTheLegacyText()
        {
            var scripts = Path.Combine(RepositoryLayout.FindRoot(), "src", "Client", "Assets", "Scripts");
            var uses = new List<string>();
            foreach (var path in Directory.GetFiles(scripts, "*.cs", SearchOption.AllDirectories))
            {
                var script = Path.GetRelativePath(scripts, path).Replace('\\', '/');
                if (Exceptions.Any(exception => exception.Script == script))
                {
                    continue;
                }

                var lines = File.ReadAllLines(path);
                for (var index = 0; index < lines.Length; index++)
                {
                    var match = ImmediateModeCode.Match(WithoutComment(lines[index]));
                    if (match.Success)
                    {
                        uses.Add(script + ":" + (index + 1) + " " + match.Value.Trim());
                    }
                }
            }

            Assert.That(uses, Is.Empty, "Build the interface on the UI kit with TextMesh Pro.");
        }

        // A runtime type that defines OnGUI however it is written, also in a partial class.
        [Test]
        public void NoClientRuntimeTypeDefinesOnGui()
        {
            var flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public
                | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            var assemblies = CurrentAssemblies.GetLoadedAssemblies()
                .Where(assembly =>
                {
                    var name = assembly.GetName().Name;
                    return name.StartsWith("Mmorpg.Client.", StringComparison.Ordinal)
                        && name != "Mmorpg.Client.Editor"
                        && !name.EndsWith("Tests", StringComparison.Ordinal);
                })
                .ToArray();
            Assert.That(assemblies.Select(assembly => assembly.GetName().Name), Does.Contain("Mmorpg.Client.Ui"));
            var definers = assemblies
                .SelectMany(assembly => assembly.GetTypes())
                .Where(type => typeof(MonoBehaviour).IsAssignableFrom(type)
                    && type.GetMethod("OnGUI", flags, null, Type.EmptyTypes, null) != null
                    && Exceptions.All(exception => !exception.Script.EndsWith("/" + type.Name + ".cs", StringComparison.Ordinal)))
                .Select(type => type.FullName)
                .ToArray();

            Assert.That(definers, Is.Empty);
        }

        // The pattern finds what it guards against and leaves the uGUI kit alone.
        [Test]
        public void ThePatternFindsImmediateModeCode()
        {
            foreach (var code in new[]
                     {
                         "private void OnGUI()",
                         "GUILayout.Label(text);",
                         "if (GUI.Button(rect, label))",
                         "var label = new UnityEngine.UI.Text();"
                     })
            {
                Assert.That(ImmediateModeCode.IsMatch(code), Is.True, code);
            }

            foreach (var code in new[]
                     {
                         "UiFactory.Button(theme, row, label, onClick);",
                         "var text = target.AddComponent<TextMeshProUGUI>();",
                         "GUIUtility.systemCopyBuffer = value;",
                         "var label = UiGUI.Create();",
                         "using UnityEngine.UI;",
                         "TemporaryUiKit.TextPrimary"
                     })
            {
                Assert.That(ImmediateModeCode.IsMatch(code), Is.False, code);
            }
        }

        private static string WithoutComment(string line)
        {
            var comment = line.IndexOf("//", StringComparison.Ordinal);
            return comment < 0 ? line : line.Substring(0, comment);
        }
    }
}
