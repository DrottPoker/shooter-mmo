using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mmorpg.Client.Core;
using Mmorpg.Client.Worlds;
using Mmorpg.WorldData.Content;
using Mmorpg.WorldData.Items.Presentation;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEngine;

namespace Mmorpg.Client.Editor
{
    // The client's Addressables layout, defined in code so it can be reapplied and verified.
    // Content folders are folder entries whose address is their path below the content root,
    // so new files inside them become addressable without editing settings. World scenes are
    // explicit entries addressed by scene name, one bundle each, and stay out of Build Profiles.
    public static class ClientAddressablesLayout
    {
        public const string ApplyMenuPath = "MMORPG/Tools/Addressables/Apply Client Layout";
        public const string ClientContentGroup = "Client Content";
        public const string ItemIconsGroup = "Item Icons";
        public const string WorldActorPrefabsGroup = "World Actor Prefabs";
        public const string WorldCollisionGroup = "World Collision";
        public const string WorldScenesGroup = "World Scenes";
        public const string WorldActorPrefabFolder = "Assets/Prefabs/WorldActors";
        public const string WorldActorPrefabsAddress = "WorldActorPrefabs";
        public const string WorldCollisionFolder =
            "Packages/com.mmorpg.world-data/Generated/" + ContentLayout.CollisionDirectoryName;

        // The client ships no remote content updates, so the content state of a build is a
        // local build artifact and stays out of Assets.
        public const string ContentStateBuildPath = "Library/com.unity.addressables/ContentState";

        // Scenes built into the player, in load order. Every other scene is a World scene.
        public static readonly IReadOnlyList<string> BuiltInScenes = new[]
        {
            "Assets/Scenes/" + SceneNames.LoginMenu + ".unity",
            "Assets/Scenes/" + SceneNames.CharacterSelect + ".unity"
        };

        private static readonly GroupLayout[] Groups =
        {
            new GroupLayout(ClientContentGroup, BundledAssetGroupSchema.BundlePackingMode.PackTogether),
            new GroupLayout(ItemIconsGroup, BundledAssetGroupSchema.BundlePackingMode.PackTogether),
            new GroupLayout(WorldActorPrefabsGroup, BundledAssetGroupSchema.BundlePackingMode.PackTogether),
            new GroupLayout(WorldCollisionGroup, BundledAssetGroupSchema.BundlePackingMode.PackTogether),
            new GroupLayout(WorldScenesGroup, BundledAssetGroupSchema.BundlePackingMode.PackSeparately)
        };

        private static readonly FolderLayout[] Folders =
        {
            ContentFolder("Config", ClientContentGroup),
            ContentFolder("Worlds", ClientContentGroup),
            ContentFolder("Items/Presentation", ClientContentGroup),
            ContentFolder("WorldActors", ClientContentGroup),
            ContentFolder(
                ItemPresentationCatalogFormat.IconAddressPrefix.TrimEnd('/'),
                ItemIconsGroup),
            new FolderLayout(WorldActorPrefabFolder, WorldActorPrefabsAddress, WorldActorPrefabsGroup),
            new FolderLayout(
                WorldCollisionFolder,
                ClientAddresses.WorldCollisionPrefix.TrimEnd('/'),
                WorldCollisionGroup)
        };

        [MenuItem(ApplyMenuPath)]
        public static void ApplyFromMenu()
        {
            Apply();
            var problems = Validate();
            if (problems.Count == 0)
            {
                Debug.Log("[ADDRESSABLES] The client layout is applied and valid.");
                return;
            }

            Debug.LogError("[ADDRESSABLES] The client layout is still invalid:\n" + string.Join("\n", problems));
        }

        // Creates or corrects the settings, groups, entries, and Build Profiles scene list.
        public static void Apply()
        {
            var settings = AddressableAssetSettingsDefaultObject.GetSettings(true);
            settings.BuildAddressablesWithPlayerBuild =
                AddressableAssetSettings.PlayerBuildOption.BuildWithPlayer;
            settings.BuildRemoteCatalog = false;
            settings.ContentStateBuildPath = ContentStateBuildPath;

            foreach (var layout in Groups)
            {
                var group = settings.FindGroup(layout.Name)
                    ?? settings.CreateGroup(
                        layout.Name,
                        false,
                        false,
                        true,
                        null,
                        typeof(BundledAssetGroupSchema),
                        typeof(ContentUpdateGroupSchema));
                var schema = group.GetSchema<BundledAssetGroupSchema>();
                schema.IncludeInBuild = true;
                schema.BundleMode = layout.Packing;
                schema.BuildPath.SetVariableByName(settings, AddressableAssetSettings.kLocalBuildPath);
                schema.LoadPath.SetVariableByName(settings, AddressableAssetSettings.kLocalLoadPath);
                EditorUtility.SetDirty(schema);
            }

            settings.DefaultGroup = settings.FindGroup(ClientContentGroup);
            foreach (var group in settings.groups.ToArray())
            {
                if (group != null
                    && Groups.All(layout => layout.Name != group.Name)
                    && group.entries.Count == 0)
                {
                    settings.RemoveGroup(group);
                }
            }

            foreach (var folder in Folders)
            {
                var guid = AssetDatabase.AssetPathToGUID(folder.Path);
                if (string.IsNullOrEmpty(guid))
                {
                    throw new InvalidOperationException("Addressables content folder is missing: " + folder.Path);
                }

                settings.CreateOrMoveEntry(guid, settings.FindGroup(folder.Group))
                    .SetAddress(folder.Address);
            }

            var worldScenes = ResolveCatalogScenes(out var catalogProblems);
            if (catalogProblems.Count > 0)
            {
                throw new InvalidOperationException(string.Join("\n", catalogProblems));
            }

            var scenesGroup = settings.FindGroup(WorldScenesGroup);
            foreach (var scene in worldScenes)
            {
                settings.CreateOrMoveEntry(AssetDatabase.AssetPathToGUID(scene.Value), scenesGroup)
                    .SetAddress(scene.Key);
            }

            foreach (var entry in scenesGroup.entries.ToArray())
            {
                if (!worldScenes.ContainsKey(entry.address))
                {
                    settings.RemoveAssetEntry(entry.guid);
                }
            }

            EditorBuildSettings.scenes = BuiltInScenes
                .Select(path => new EditorBuildSettingsScene(path, true))
                .ToArray();
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
        }

        // Lists every difference between the committed settings and the layout.
        public static List<string> Validate()
        {
            var problems = new List<string>();
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
            {
                problems.Add("The project has no Addressables settings.");
                return problems;
            }

            if (settings.BuildAddressablesWithPlayerBuild
                != AddressableAssetSettings.PlayerBuildOption.BuildWithPlayer)
            {
                problems.Add("Player builds must build Addressables content.");
            }

            if (settings.BuildRemoteCatalog)
            {
                problems.Add("The client has no remote content catalog.");
            }

            if (settings.ContentStateBuildPath != ContentStateBuildPath)
            {
                problems.Add("The Addressables content state must be written to " + ContentStateBuildPath + ".");
            }

            var groupNames = settings.groups.Where(group => group != null).Select(group => group.Name).ToArray();
            foreach (var unexpected in groupNames.Where(name => Groups.All(layout => layout.Name != name)))
            {
                problems.Add("Unexpected Addressables group '" + unexpected + "'.");
            }

            if (settings.DefaultGroup == null || settings.DefaultGroup.Name != ClientContentGroup)
            {
                problems.Add("The default Addressables group must be '" + ClientContentGroup + "'.");
            }

            foreach (var layout in Groups)
            {
                var schema = settings.FindGroup(layout.Name)?.GetSchema<BundledAssetGroupSchema>();
                if (schema == null)
                {
                    problems.Add("Addressables group '" + layout.Name + "' is missing.");
                    continue;
                }

                if (!schema.IncludeInBuild
                    || schema.BundleMode != layout.Packing
                    || schema.BuildPath.GetName(settings) != AddressableAssetSettings.kLocalBuildPath
                    || schema.LoadPath.GetName(settings) != AddressableAssetSettings.kLocalLoadPath)
                {
                    problems.Add("Addressables group '" + layout.Name
                        + "' must be a local build group packed " + layout.Packing + ".");
                }
            }

            foreach (var folder in Folders)
            {
                var entry = settings.FindAssetEntry(AssetDatabase.AssetPathToGUID(folder.Path));
                if (entry == null
                    || !AssetDatabase.IsValidFolder(entry.AssetPath)
                    || entry.address != folder.Address
                    || entry.parentGroup == null
                    || entry.parentGroup.Name != folder.Group)
                {
                    problems.Add("Folder '" + folder.Path + "' must be an Addressables folder entry with address '"
                        + folder.Address + "' in group '" + folder.Group + "'.");
                }
            }

            var worldScenes = ResolveCatalogScenes(out var catalogProblems);
            problems.AddRange(catalogProblems);
            var scenesGroup = settings.FindGroup(WorldScenesGroup);
            var sceneEntries = scenesGroup == null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : scenesGroup.entries.ToDictionary(entry => entry.address, entry => entry.AssetPath, StringComparer.Ordinal);
            foreach (var scene in worldScenes)
            {
                if (!sceneEntries.TryGetValue(scene.Key, out var path) || path != scene.Value)
                {
                    problems.Add("World scene '" + scene.Value + "' must be an entry with address '"
                        + scene.Key + "' in group '" + WorldScenesGroup + "'.");
                }
            }

            foreach (var extra in sceneEntries.Keys.Where(address => !worldScenes.ContainsKey(address)))
            {
                problems.Add("Group '" + WorldScenesGroup + "' contains '" + extra
                    + "', which no catalog World uses.");
            }

            var buildScenes = EditorBuildSettings.scenes.Select(scene => scene.path).ToArray();
            if (!buildScenes.SequenceEqual(BuiltInScenes)
                || EditorBuildSettings.scenes.Any(scene => !scene.enabled))
            {
                problems.Add("Build Profiles must list exactly " + string.Join(", ", BuiltInScenes)
                    + ". World scenes load through Addressables.");
            }

            return problems;
        }

        // Maps every World scene address to its scene path, from the world scene catalog.
        public static Dictionary<string, string> ResolveCatalogScenes(out List<string> problems)
        {
            problems = new List<string>();
            var scenes = new Dictionary<string, string>(StringComparer.Ordinal);
            var catalogPath = ClientAddresses.ContentRoot + ClientAddresses.WorldSceneCatalog;
            var catalogAsset = AssetDatabase.LoadAssetAtPath<TextAsset>(catalogPath);
            if (catalogAsset == null)
            {
                problems.Add("The world scene catalog is missing at " + catalogPath + ".");
                return scenes;
            }

            if (!WorldSceneCatalog.TryParse(catalogAsset.text, out var catalog, out var error))
            {
                problems.Add(error);
                return scenes;
            }

            foreach (var sceneName in catalog.entries.Select(entry => entry.sceneName).Distinct(StringComparer.Ordinal))
            {
                var paths = AssetDatabase.FindAssets("t:SceneAsset " + sceneName, new[] { "Assets" })
                    .Select(AssetDatabase.GUIDToAssetPath)
                    .Where(path => string.Equals(
                        Path.GetFileNameWithoutExtension(path),
                        sceneName,
                        StringComparison.Ordinal))
                    .ToArray();
                if (paths.Length != 1)
                {
                    problems.Add("World scene '" + sceneName + "' must exist exactly once below Assets.");
                    continue;
                }

                if (BuiltInScenes.Contains(paths[0]))
                {
                    problems.Add("World scene '" + sceneName + "' must not be a built-in scene.");
                    continue;
                }

                scenes.Add(sceneName, paths[0]);
            }

            return scenes;
        }

        private static FolderLayout ContentFolder(string relativePath, string group)
        {
            return new FolderLayout(ClientAddresses.ContentRoot + relativePath, relativePath, group);
        }

        private readonly struct GroupLayout
        {
            public GroupLayout(string name, BundledAssetGroupSchema.BundlePackingMode packing)
            {
                Name = name;
                Packing = packing;
            }

            public string Name { get; }

            public BundledAssetGroupSchema.BundlePackingMode Packing { get; }
        }

        private readonly struct FolderLayout
        {
            public FolderLayout(string path, string address, string group)
            {
                Path = path;
                Address = address;
                Group = group;
            }

            public string Path { get; }

            public string Address { get; }

            public string Group { get; }
        }
    }
}
