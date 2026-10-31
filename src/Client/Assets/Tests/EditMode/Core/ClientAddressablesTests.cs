using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Mmorpg.Client.Core;
using Mmorpg.Client.Editor;
using Mmorpg.Client.WorldActors;
using Mmorpg.GameSimulation;
using Mmorpg.WorldData.Content;
using Mmorpg.WorldData.Editor;
using Mmorpg.WorldData.Items.Presentation;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;

namespace Mmorpg.Client.Tests.EditMode
{
    // The client loads all content through Addressables. These tests pin the committed
    // settings to the layout in code and check that every address the runtime builds exists.
    public sealed class ClientAddressablesTests
    {
        private static readonly Regex ResourcesLoadCall = new Regex(
            @"\bResources\s*\.\s*(Load|LoadAll|LoadAsync|UnloadAsset)\b",
            RegexOptions.Compiled);

        [Test]
        public void AddressablesSettingsMatchTheClientLayout()
        {
            Assert.That(ClientAddressablesLayout.Validate(), Is.Empty);
        }

        [Test]
        public void EveryFixedClientAddressResolvesToItsContentAsset()
        {
            var addresses = GatherAddresses();
            foreach (var address in new[]
                     {
                         ClientAddresses.ClientConfig,
                         ClientAddresses.WorldSceneCatalog,
                         ClientAddresses.ItemPresentationCatalog,
                         ClientAddresses.WorldActorPresentationRegistry
                     })
            {
                Assert.That(addresses.TryGetValue(address, out var path), Is.True, address);
                Assert.That(path, Is.EqualTo(ClientAddresses.ContentRoot + address), address);
            }
        }

        [Test]
        public void EveryGeneratedCollisionFileHasTheAddressTheRuntimeBuilds()
        {
            var addresses = GatherAddresses();
            var collisionRoot = ContentLayout.GeneratedCollisionRoot(
                RepositoryLayout.GeneratedRoot(RepositoryLayout.FindRoot()));
            var worldIds = Directory.GetDirectories(collisionRoot).Select(Path.GetFileName).ToArray();
            Assert.That(worldIds, Is.Not.Empty);
            foreach (var worldId in worldIds)
            {
                var directory = ClientAddresses.WorldCollisionPrefix + worldId + "/";
                var manifestAddress = directory + ContentLayout.CollisionManifestFileName;
                Assert.That(addresses.ContainsKey(manifestAddress), Is.True, manifestAddress);
                var manifest = JsonUtility.FromJson<CollisionWorldManifest>(File.ReadAllText(
                    Path.Combine(collisionRoot, worldId, ContentLayout.CollisionManifestFileName)));
                foreach (var chunk in manifest.Chunks)
                {
                    var chunkAddress = directory + chunk.ResourceName + CollisionDataFormat.ChunkFileExtension;
                    Assert.That(addresses.ContainsKey(chunkAddress), Is.True, chunkAddress);
                }
            }
        }

        [Test]
        public void EveryPresentationIconIsAnAddressableSprite()
        {
            var addresses = GatherAddresses();
            var catalog = ItemPresentationCatalogJson.Deserialize(File.ReadAllText(
                ClientAddresses.ContentRoot + ClientAddresses.ItemPresentationCatalog));
            foreach (var entry in catalog.entries.Where(entry => !string.IsNullOrEmpty(entry.iconAddress)))
            {
                Assert.That(addresses.TryGetValue(entry.iconAddress, out var path), Is.True, entry.definitionId);
                Assert.That(AssetDatabase.LoadAssetAtPath<Sprite>(path), Is.Not.Null, entry.definitionId);
            }
        }

        [Test]
        public void EveryRegistryPrefabIsAnAddressableEntry()
        {
            var addressablePaths = new HashSet<string>(GatherAddresses().Values, StringComparer.Ordinal);
            var registry = AssetDatabase.LoadAssetAtPath<WorldActorPresentationRegistry>(
                ClientAddresses.ContentRoot + ClientAddresses.WorldActorPresentationRegistry);
            var entries = new SerializedObject(registry).FindProperty("entries");
            Assert.That(entries.arraySize, Is.GreaterThan(0));
            for (var index = 0; index < entries.arraySize; index++)
            {
                var entry = entries.GetArrayElementAtIndex(index);
                var archetype = entry.FindPropertyRelative("presentationArchetypeId").stringValue;
                Assert.That(registry.TryResolve(archetype, out var reference), Is.True, archetype);
                Assert.That(
                    addressablePaths.Contains(AssetDatabase.GUIDToAssetPath(reference.AssetGUID)),
                    Is.True,
                    archetype);
            }
        }

        [Test]
        public void RuntimeLoadsTheConfigurationThroughAddressables()
        {
            Assert.That(
                ClientAssets.TryReadText(ClientAddresses.WorldSceneCatalog, out var text, out var error),
                Is.True,
                error);
            Assert.That(text, Does.Contain("\"entries\""));
            Assert.That(ClientAssets.Exists("Missing/address.json", typeof(TextAsset)), Is.False);
            Assert.That(
                ClientAssets.TryReadText("Missing/address.json", out _, out var missingError),
                Is.False);
            Assert.That(missingError, Does.Contain("Missing/address.json"));
        }

        [Test]
        public void ClientCodeAndContentNeverUseUnityResources()
        {
            var repositoryRoot = RepositoryLayout.FindRoot();
            var roots = new[]
            {
                Path.Combine(repositoryRoot, "src", "Client", "Assets"),
                Path.Combine(repositoryRoot, "src", "Shared")
            };
            foreach (var root in roots)
            {
                var resourceFolders = Directory
                    .GetDirectories(root, "Resources", SearchOption.AllDirectories)
                    .Where(path => !IsBuildOutput(path))
                    .ToArray();
                Assert.That(resourceFolders, Is.Empty, root);

                var callers = Directory
                    .GetFiles(root, "*.cs", SearchOption.AllDirectories)
                    .Where(path => !IsBuildOutput(path)
                        && !string.Equals(Path.GetFileName(path), nameof(ClientAddressablesTests) + ".cs", StringComparison.Ordinal)
                        && ResourcesLoadCall.IsMatch(File.ReadAllText(path)))
                    .ToArray();
                Assert.That(callers, Is.Empty, root);
            }
        }

        // Every Addressables address, including assets inside folder entries, mapped to its path.
        private static Dictionary<string, string> GatherAddresses()
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            Assert.That(settings, Is.Not.Null);
            var entries = new List<AddressableAssetEntry>();
            settings.GetAllAssets(entries, false);
            var addresses = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                Assert.That(addresses.ContainsKey(entry.address), Is.False, "Duplicate address " + entry.address);
                addresses.Add(entry.address, entry.AssetPath);
            }

            return addresses;
        }

        private static bool IsBuildOutput(string path)
        {
            var normalized = path.Replace('\\', '/');
            return normalized.Contains("/bin/") || normalized.Contains("/obj/");
        }
    }
}
