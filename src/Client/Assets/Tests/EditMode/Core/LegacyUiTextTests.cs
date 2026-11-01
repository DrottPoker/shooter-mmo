using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Mmorpg.WorldData.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Assemblies;
using UnityEngine.UI;

namespace Mmorpg.Client.Tests.EditMode
{
    // All UI text is TextMesh Pro, because the legacy uGUI Text blurs when its canvas scales: no
    // client type holds a legacy Text, InputField, or font, no client code creates or finds one,
    // and no scene or prefab contains one.
    public sealed class LegacyUiTextTests
    {
        private static readonly Type[] LegacyTypes = { typeof(Text), typeof(InputField), typeof(Font) };

        private static readonly Regex LegacyCode = new Regex(
            @"(AddComponent|GetComponent|GetComponents|GetComponentInChildren|GetComponentsInChildren)<(UnityEngine\.UI\.)?(Text|InputField)>"
                + @"|typeof\((UnityEngine\.UI\.)?(Text|InputField)\)"
                + @"|LegacyRuntime\.ttf",
            RegexOptions.Compiled);

        [Test]
        public void NoClientTypeHoldsALegacyTextInputFieldOrFont()
        {
            var flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public
                | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            var assemblies = CurrentAssemblies.GetLoadedAssemblies()
                .Where(assembly => assembly.GetName().Name.StartsWith("Mmorpg.Client", StringComparison.Ordinal))
                .ToArray();
            Assert.That(assemblies, Is.Not.Empty);
            var holders = assemblies
                .SelectMany(assembly => assembly.GetTypes())
                .SelectMany(type => type.GetFields(flags)
                    .Select(field => (Member: type.FullName + "." + field.Name, Type: field.FieldType))
                    .Concat(type.GetProperties(flags)
                        .Select(property => (Member: type.FullName + "." + property.Name, Type: property.PropertyType))))
                .Where(member => MentionsLegacyType(member.Type))
                .Select(member => member.Member)
                .ToArray();

            Assert.That(holders, Is.Empty);
        }

        [Test]
        public void NoClientCodeCreatesOrFindsALegacyText()
        {
            var assets = Path.Combine(RepositoryLayout.FindRoot(), "src", "Client", "Assets");
            var callers = new[] { "Scripts", "Editor", "Tests" }
                .SelectMany(folder => Directory.GetFiles(Path.Combine(assets, folder), "*.cs", SearchOption.AllDirectories))
                .Where(path => !string.Equals(Path.GetFileName(path), nameof(LegacyUiTextTests) + ".cs", StringComparison.Ordinal)
                    && LegacyCode.IsMatch(File.ReadAllText(path)))
                .ToArray();

            Assert.That(callers, Is.Empty);
        }

        [Test]
        public void NoSceneOrPrefabContainsALegacyTextOrInputField()
        {
            var guids = new[] { ScriptGuid<Text>(), ScriptGuid<InputField>() };
            var assets = Path.Combine(RepositoryLayout.FindRoot(), "src", "Client", "Assets");
            var containers = Directory.GetFiles(assets, "*.unity", SearchOption.AllDirectories)
                .Concat(Directory.GetFiles(assets, "*.prefab", SearchOption.AllDirectories))
                .Where(path =>
                {
                    var content = File.ReadAllText(path);
                    return guids.Any(guid => content.Contains("guid: " + guid));
                })
                .ToArray();

            Assert.That(containers, Is.Empty);
        }

        private static bool MentionsLegacyType(Type type)
        {
            if (type.IsArray || type.IsByRef || type.IsPointer)
            {
                return MentionsLegacyType(type.GetElementType());
            }

            return LegacyTypes.Any(legacy => legacy.IsAssignableFrom(type))
                || (type.IsGenericType && type.GetGenericArguments().Any(MentionsLegacyType));
        }

        // The GUID of the script asset a scene or prefab names a component by.
        private static string ScriptGuid<T>()
            where T : MonoBehaviour
        {
            var holder = new GameObject("LegacyUiTextProbe", typeof(RectTransform));
            try
            {
                var script = MonoScript.FromMonoBehaviour(holder.AddComponent<T>());
                Assert.That(
                    AssetDatabase.TryGetGUIDAndLocalFileIdentifier(script, out var guid, out long _),
                    Is.True,
                    typeof(T).Name);
                return guid;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(holder);
            }
        }
    }
}
