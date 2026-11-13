using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Mmorpg.Client.Gameplay;
using Mmorpg.Client.Items;
using Mmorpg.Client.Ui;
using Mmorpg.Client.Worlds;
using Mmorpg.WorldData.Editor;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Assemblies;

namespace Mmorpg.Client.Tests.EditMode
{
    // The bootstrap passes every service and the local player explicitly, so no runtime code
    // searches the scene or asks for the main camera, and no type keeps state in statics, outside
    // the places listed here, each with its reason.
    public sealed class ClientServiceAccessTests
    {
        private static readonly Regex SceneSearch = new Regex(
            @"\b(FindAnyObjectByType|FindFirstObjectByType|FindObjectsByType|FindObjectOfType|FindObjectsOfType"
                + @"|FindWithTag|FindGameObjectWithTag|FindGameObjectsWithTag)\b|\bGameObject\.Find\b|\bCamera\.main\b",
            RegexOptions.Compiled);

        // Each allowed search: the script below Assets/Scripts, the code that searches, and why.
        private static readonly (string Script, string Code, string Reason)[] AllowedSearches =
        {
            (
                "App/ClientBootstrap.cs",
                "FindAnyObjectByType<ClientBootstrap>()",
                "The bootstrap creates itself after the first scene loads, once."),
            (
                "App/ClientBootstrap.cs",
                "FindAnyObjectByType<T>()",
                "Composing a scene keeps a component that the scene already has."),
            (
                "Ui/TemporaryUiKit.cs",
                "FindAnyObjectByType<EventSystem>()",
                "uGUI needs one EventSystem in the open scene, which the UI kit adds when the scene has none."),
            (
                "Gameplay/Cloth/ClothChainScheduler.cs",
                "Camera.main",
                "The cloth scheduler serves every body, also in tools and tests without the bootstrap, and measures "
                    + "from the camera that renders the game.")
        };

        // Every type that keeps state in statics. Each resets it at subsystem registration,
        // because Enter Play Mode Options may keep statics from the last play.
        private static readonly Type[] TypesWithStaticState =
        {
            typeof(WorldSceneCatalog),
            typeof(ItemPresentationCatalogLoader),
            typeof(WorldLabels),
            typeof(ClothChainScheduler),
            typeof(ClothChainSolver)
        };

        // Static fields whose type could hold state but that hold none, or must outlive a play.
        private static readonly (string Field, string Reason)[] AllowedStaticFields =
        {
            ("Mmorpg.Client.Combat.CombatantViewLocator.NoRemotePlayers", "An empty dictionary that nothing adds to."),
            ("Mmorpg.Client.Ui.EquipmentPaperDollLayout.HiddenSlots", "A constant set of slot ids."),
            (
                "Mmorpg.Client.Worlds.WorldBuilderShape.Changed",
                "An Editor event whose listeners live as long as the Editor's domain, so a play keeps them.")
        };

        // Collections that a static readonly field can still change.
        private static readonly Type[] MutableCollections =
        {
            typeof(List<>),
            typeof(Dictionary<,>),
            typeof(HashSet<>),
            typeof(Queue<>),
            typeof(Stack<>),
            typeof(LinkedList<>),
            typeof(SortedDictionary<,>),
            typeof(SortedList<,>),
            typeof(SortedSet<>)
        };

        [Test]
        public void NoRuntimeCodeSearchesTheSceneOrAsksForTheMainCamera()
        {
            var scripts = Path.Combine(RepositoryLayout.FindRoot(), "src", "Client", "Assets", "Scripts");
            var uses = new Dictionary<(string Script, string Code, string Reason), int>();
            var unlisted = new List<string>();
            foreach (var path in Directory.GetFiles(scripts, "*.cs", SearchOption.AllDirectories))
            {
                var script = Path.GetRelativePath(scripts, path).Replace('\\', '/');
                var lines = File.ReadAllLines(path);
                for (var index = 0; index < lines.Length; index++)
                {
                    var code = WithoutComment(lines[index]);
                    foreach (Match match in SceneSearch.Matches(code))
                    {
                        var allowed = AllowedSearches.FirstOrDefault(entry =>
                            entry.Script == script && code.Contains(entry.Code, StringComparison.Ordinal));
                        if (allowed.Script == null)
                        {
                            unlisted.Add(script + ":" + (index + 1) + " " + match.Value);
                            continue;
                        }

                        uses[allowed] = uses.TryGetValue(allowed, out var count) ? count + 1 : 1;
                    }
                }
            }

            Assert.That(unlisted, Is.Empty, "Pass the service or the LocalPlayerSource through Initialize.");
            Assert.That(
                AllowedSearches
                    .Where(entry => !uses.TryGetValue(entry, out var count) || count != 1)
                    .Select(entry => entry.Script + " " + entry.Code),
                Is.Empty,
                "Each allowed search stands exactly once.");
        }

        [Test]
        public void OnlyTheListedTypesKeepStateInStatics()
        {
            var holders = ClientRuntimeTypes()
                .SelectMany(type => type
                    .GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .Where(field => HoldsState(field)
                        && AllowedStaticFields.All(allowed => allowed.Field != type.FullName + "." + field.Name))
                    .Select(field => (Type: type, Field: field.Name)))
                .ToArray();

            Assert.That(
                holders.Where(holder => !TypesWithStaticState.Contains(holder.Type))
                    .Select(holder => holder.Type.FullName + "." + holder.Field),
                Is.Empty,
                "Pass the state through Initialize, or list the type here and reset it at subsystem registration.");
            Assert.That(
                TypesWithStaticState.Where(type => holders.All(holder => holder.Type != type)).Select(type => type.Name),
                Is.Empty,
                "A listed type keeps no state in statics any more.");
        }

        [Test]
        public void EveryTypeWithStaticStateResetsBeforeTheFirstSceneLoads()
        {
            foreach (var type in TypesWithStaticState)
            {
                var reset = type
                    .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    .SingleOrDefault(method => method
                        .GetCustomAttributes<RuntimeInitializeOnLoadMethodAttribute>()
                        .Any(attribute =>
                            attribute.loadType == RuntimeInitializeLoadType.SubsystemRegistration));
                Assert.That(reset, Is.Not.Null, type.Name);
            }
        }

        private static IEnumerable<Type> ClientRuntimeTypes()
        {
            var assemblies = CurrentAssemblies.GetLoadedAssemblies()
                .Where(assembly =>
                {
                    var name = assembly.GetName().Name;
                    return name.StartsWith("Mmorpg.Client.", StringComparison.Ordinal)
                        && name != "Mmorpg.Client.Editor"
                        && !name.EndsWith("Tests", StringComparison.Ordinal);
                })
                .ToArray();
            Assert.That(assemblies.Select(assembly => assembly.GetName().Name), Does.Contain("Mmorpg.Client.App"));

            // The compiler's own types, such as cached lambdas, are no state of the client.
            return assemblies
                .SelectMany(assembly => assembly.GetTypes())
                .Where(type => !IsCompilerGenerated(type));
        }

        private static bool IsCompilerGenerated(Type type)
        {
            for (var current = type; current != null; current = current.DeclaringType)
            {
                if (current.IsDefined(typeof(CompilerGeneratedAttribute), false))
                {
                    return true;
                }
            }

            return false;
        }

        // A field that can be assigned, such as the field of a static property or event, or a
        // readonly collection that can still change.
        private static bool HoldsState(FieldInfo field)
        {
            if (field.IsLiteral)
            {
                return false;
            }

            if (!field.IsInitOnly)
            {
                return true;
            }

            var type = field.FieldType;
            return type.IsGenericType && MutableCollections.Contains(type.GetGenericTypeDefinition());
        }

        private static string WithoutComment(string line)
        {
            var comment = line.IndexOf("//", StringComparison.Ordinal);
            return comment < 0 ? line : line.Substring(0, comment);
        }
    }
}
