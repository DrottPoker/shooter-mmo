using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using UnityEngine.Assemblies;

namespace Mmorpg.Client.Tests.EditMode
{
    // Pins the client assembly graph. Each arrow is an explicit asmdef reference, so a new
    // dependency between areas must be added here on purpose and reviewed. The tests read the
    // declared references because Unity adds UnityEngine.UI to every compiled assembly.
    public sealed class ClientAssemblyArchitectureTests
    {
        private const string ClientPrefix = "Mmorpg.Client.";
        private const string ProjectPrefix = "Mmorpg.";

        // Runtime areas from the bottom up. A client assembly may only reference areas that
        // come before it, so the graph has no cycles, Ui is the top feature area, and only the
        // App composition root sits above it.
        private static readonly string[] RuntimeLayers =
        {
            "Mmorpg.Client.Cloth",
            "Mmorpg.Client.Api",
            "Mmorpg.Client.Core",
            "Mmorpg.Client.Worlds",
            "Mmorpg.Client.Networking",
            "Mmorpg.Client.Items",
            "Mmorpg.Client.Gameplay",
            "Mmorpg.Client.WorldActors",
            "Mmorpg.Client.Combat",
            "Mmorpg.Client.Ui",
            "Mmorpg.Client.App"
        };

        private static readonly Dictionary<string, string[]> AllowedReferences =
            new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["Mmorpg.Client.Cloth"] = new[]
                {
                    "Unity.Burst",
                    "Unity.Collections",
                    "Unity.Mathematics"
                },
                ["Mmorpg.Client.Api"] = Array.Empty<string>(),
                ["Mmorpg.Client.Core"] = new[]
                {
                    "Mmorpg.Client.Api",
                    "Unity.Addressables",
                    "Unity.ResourceManager"
                },
                ["Mmorpg.Client.Worlds"] = new[]
                {
                    "Mmorpg.Client.Core",
                    "Mmorpg.GameSimulation",
                    "Mmorpg.WorldData",
                    "Unity.Addressables",
                    "Unity.ResourceManager"
                },
                ["Mmorpg.Client.Networking"] = new[]
                {
                    "Mmorpg.Client.Api",
                    "Mmorpg.Client.Core",
                    "Mmorpg.Client.Worlds",
                    "LiteNetLib",
                    "Mmorpg.GameProtocol",
                    "Mmorpg.GameSimulation"
                },
                ["Mmorpg.Client.Items"] = new[]
                {
                    "Mmorpg.Client.Api",
                    "Mmorpg.Client.Core",
                    "Mmorpg.Client.Networking",
                    "Mmorpg.GameProtocol",
                    "Mmorpg.WorldData",
                    "Mmorpg.WorldData.Client",
                    "Unity.ResourceManager"
                },
                ["Mmorpg.Client.Gameplay"] = new[]
                {
                    "Mmorpg.Client.Api",
                    "Mmorpg.Client.Core",
                    "Mmorpg.Client.Worlds",
                    "Mmorpg.Client.Networking",
                    "Mmorpg.Client.Cloth",
                    "Unity.Collections",
                    "Unity.Mathematics",
                    "Unity.InputSystem",
                    "Mmorpg.GameProtocol",
                    "Mmorpg.GameSimulation",
                    "Mmorpg.WorldData",
                    "Mmorpg.WorldData.Client"
                },
                ["Mmorpg.Client.WorldActors"] = new[]
                {
                    "Mmorpg.Client.Api",
                    "Mmorpg.Client.Core",
                    "Mmorpg.Client.Networking",
                    "Mmorpg.Client.Items",
                    "Mmorpg.Client.Gameplay",
                    "Mmorpg.GameProtocol",
                    "Mmorpg.WorldData",
                    "Unity.Addressables",
                    "Unity.ResourceManager"
                },
                ["Mmorpg.Client.Combat"] = new[]
                {
                    "Mmorpg.Client.Core",
                    "Mmorpg.Client.Networking",
                    "Mmorpg.Client.Items",
                    "Mmorpg.Client.Gameplay",
                    "Mmorpg.Client.WorldActors",
                    "Mmorpg.GameProtocol",
                    "Mmorpg.GameSimulation",
                    "Mmorpg.WorldData"
                },
                ["Mmorpg.Client.Ui"] = new[]
                {
                    "Mmorpg.Client.Api",
                    "Mmorpg.Client.Core",
                    "Mmorpg.Client.Worlds",
                    "Mmorpg.Client.Networking",
                    "Mmorpg.Client.Items",
                    "Mmorpg.Client.Gameplay",
                    "Mmorpg.Client.WorldActors",
                    "Mmorpg.Client.Combat",
                    "Unity.InputSystem",
                    "UnityEngine.UI",
                    "Unity.TextMeshPro",
                    "Mmorpg.GameProtocol",
                    "Mmorpg.GameSimulation",
                    "Mmorpg.WorldData",
                    "Mmorpg.WorldData.Client",
                    "Unity.ResourceManager"
                },
                ["Mmorpg.Client.App"] = new[]
                {
                    "Mmorpg.Client.Api",
                    "Mmorpg.Client.Core",
                    "Mmorpg.Client.Worlds",
                    "Mmorpg.Client.Networking",
                    "Mmorpg.Client.Items",
                    "Mmorpg.Client.Gameplay",
                    "Mmorpg.Client.WorldActors",
                    "Mmorpg.Client.Combat",
                    "Mmorpg.Client.Ui",
                    "Unity.ResourceManager",
                    "Mmorpg.WorldData.Client"
                },
                ["Mmorpg.Client.Editor"] = new[]
                {
                    "Mmorpg.Client.Core",
                    "Mmorpg.Client.Gameplay",
                    "Mmorpg.Client.Cloth",
                    "Unity.Mathematics",
                    "Mmorpg.Client.Worlds",
                    "Mmorpg.GameSimulation",
                    "Mmorpg.WorldData",
                    "Mmorpg.WorldData.Client",
                    "Mmorpg.WorldData.Editor",
                    "Mmorpg.WorldData.Actors.Editor",
                    "Unity.Addressables.Editor",
                    "UnityEngine.UI"
                }
            };

        [Test]
        public void ClientAssembliesAreExactlyTheKnownAreas()
        {
            var runtime = ClientAssemblies(AssembliesType.PlayerWithoutTestAssemblies)
                .Select(assembly => assembly.name);
            var all = ClientAssemblies(AssembliesType.Editor)
                .Select(assembly => assembly.name);

            Assert.That(runtime, Is.EquivalentTo(RuntimeLayers));
            Assert.That(all, Is.EquivalentTo(AllowedReferences.Keys));
        }

        [Test]
        public void EveryClientAssemblyReferencesExactlyItsAllowedAssemblies()
        {
            foreach (var assembly in ClientAssemblies(AssembliesType.Editor))
            {
                Assert.That(
                    DeclaredReferences(assembly.name),
                    Is.EquivalentTo(AllowedReferences[assembly.name]),
                    assembly.name);
            }
        }

        [Test]
        public void RuntimeAssembliesReferenceOnlyLowerLayers()
        {
            foreach (var assembly in ClientAssemblies(AssembliesType.PlayerWithoutTestAssemblies))
            {
                var layer = Array.IndexOf(RuntimeLayers, assembly.name);
                foreach (var reference in DeclaredReferences(assembly.name)
                             .Where(reference => reference.StartsWith(ClientPrefix, StringComparison.Ordinal)))
                {
                    Assert.That(
                        Array.IndexOf(RuntimeLayers, reference),
                        Is.InRange(0, layer - 1),
                        assembly.name + " must not reference " + reference + ".");
                }
            }
        }

        [Test]
        public void EveryClientTypeLivesInTheNamespaceOfItsAssembly()
        {
            var loaded = CurrentAssemblies.GetLoadedAssemblies()
                .Where(assembly => RuntimeLayers.Contains(assembly.GetName().Name))
                .ToArray();
            Assert.That(
                loaded.Select(assembly => assembly.GetName().Name),
                Is.EquivalentTo(RuntimeLayers));

            foreach (var assembly in loaded)
            {
                var expected = assembly.GetName().Name;
                var misplaced = assembly.GetTypes()
                    .Where(type => !type.IsNested
                        && !type.IsDefined(typeof(CompilerGeneratedAttribute), false)
                        && !type.Name.StartsWith("UnitySourceGenerated", StringComparison.Ordinal)
                        && !type.Name.StartsWith("__JobReflectionRegistrationOutput__", StringComparison.Ordinal)
                        && !type.Name.StartsWith("<", StringComparison.Ordinal)
                        && type.Namespace != expected)
                    .Select(type => type.FullName)
                    .ToArray();
                Assert.That(misplaced, Is.Empty, expected);
            }
        }

        // Unity compiles each assembly with the csc.rsp next to its asmdef, so a warning fails the
        // Editor and the CI Unity gate as TreatWarningsAsErrors fails the .NET build. Every script
        // lives in an asmdef, because the predefined Assembly-CSharp assemblies have no csc.rsp.
        [Test]
        public void EveryProjectAssemblyTreatsWarningsAsErrors()
        {
            var assemblies = CompilationPipeline.GetAssemblies(AssembliesType.Editor);
            Assert.That(
                assemblies.Select(assembly => assembly.name)
                    .Where(name => name.StartsWith("Assembly-CSharp", StringComparison.Ordinal)),
                Is.Empty);

            var project = assemblies
                .Where(assembly => assembly.name.StartsWith(ProjectPrefix, StringComparison.Ordinal))
                .ToArray();
            Assert.That(project, Is.Not.Empty);
            foreach (var assembly in project)
            {
                var definition = CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(assembly.name);
                Assert.That(definition, Is.Not.Null.And.Not.Empty, assembly.name);
                var responseFile = Path.Combine(
                    Path.GetDirectoryName(FileUtil.GetPhysicalPath(definition)),
                    "csc.rsp");
                Assert.That(File.Exists(responseFile), Is.True, assembly.name + " has no " + responseFile);
                Assert.That(
                    File.ReadAllLines(responseFile).Select(line => line.Trim()),
                    Does.Contain("-warnaserror+"),
                    responseFile);
            }
        }

        private static IEnumerable<Assembly> ClientAssemblies(AssembliesType type)
        {
            return CompilationPipeline.GetAssemblies(type)
                .Where(assembly => assembly.name.StartsWith(ClientPrefix, StringComparison.Ordinal)
                    && !assembly.name.EndsWith("Tests", StringComparison.Ordinal));
        }

        private static string[] DeclaredReferences(string assemblyName)
        {
            var path = CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(assemblyName);
            Assert.That(path, Is.Not.Null.And.Not.Empty, assemblyName);
            var definition = JsonUtility.FromJson<AssemblyDefinitionReferences>(File.ReadAllText(path));
            return definition.references
                .Select(reference => reference.StartsWith("GUID:", StringComparison.Ordinal)
                    ? Path.GetFileNameWithoutExtension(
                        AssetDatabase.GUIDToAssetPath(reference.Substring("GUID:".Length)))
                    : reference)
                .ToArray();
        }

        [Serializable]
        private sealed class AssemblyDefinitionReferences
        {
            public string[] references = Array.Empty<string>();
        }
    }
}
