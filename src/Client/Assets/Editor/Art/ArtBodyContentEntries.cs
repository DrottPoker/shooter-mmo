using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;

namespace Mmorpg.Client.Editor
{
    // The Addressables entries of body content, which ArtAppearanceCatalogs writes: every content
    // asset of a garment or an attachment and every image of an option or a paint layer is an entry
    // of the Body Content group, packed separately, so each loads and unloads as its own bundle. An
    // asset that two bundles would both hold, such as a garment texture's material that both bodies'
    // garments draw with, a template's model that several garments share, or the garment shader, is
    // an entry of the Body Content Shared group, also packed separately, so no bundle holds a copy of
    // another's asset; the bundles of the World scenes and the other groups count too. See
    // docs/UNITY_CLIENT_ARCHITECTURE.md, Content Loading With Addressables.
    public static class ArtBodyContentEntries
    {
        // Paths that are no asset a bundle holds.
        private static readonly string[] Ignored = { ".cs", ".dll", ".asmdef", ".asmref", ".hlsl", ".cginc", ".glslinc" };

        // Sets the entries of Body Content to exactly these, by address and asset path, and those of
        // Body Content Shared to every asset two bundles share; returns how many entries it added,
        // moved, readdressed, or removed. Changes nothing when the entries are as they should be.
        public static int Apply(IReadOnlyDictionary<string, string> entries)
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
            {
                throw new InvalidOperationException("The project has no Addressables settings.");
            }

            var contentGroup = ClientAddressablesLayout.EnsureGroup(settings, ClientAddressablesLayout.BodyContentGroup);
            var sharedGroup = ClientAddressablesLayout.EnsureGroup(settings, ClientAddressablesLayout.BodyContentSharedGroup);
            var changes = SetEntries(settings, contentGroup, entries.ToDictionary(
                pair => AssetDatabase.AssetPathToGUID(pair.Value),
                pair => pair.Key));
            var shared = SharedAssets(settings);
            changes += SetEntries(settings, sharedGroup, shared.ToDictionary(AssetDatabase.AssetPathToGUID, path => path));
            if (changes > 0)
            {
                settings.SetDirty(AddressableAssetSettings.ModificationEvent.BatchModification, null, true, true);
                AssetDatabase.SaveAssets();
            }

            return changes;
        }

        // Every difference between the entries of the two groups and what Apply writes for these
        // content entries.
        public static List<string> Problems(IReadOnlyDictionary<string, string> entries)
        {
            var problems = new List<string>();
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            var contentGroup = settings != null ? settings.FindGroup(ClientAddressablesLayout.BodyContentGroup) : null;
            var sharedGroup = settings != null ? settings.FindGroup(ClientAddressablesLayout.BodyContentSharedGroup) : null;
            if (contentGroup == null || sharedGroup == null)
            {
                problems.Add("The groups " + ClientAddressablesLayout.BodyContentGroup + " and "
                    + ClientAddressablesLayout.BodyContentSharedGroup + " are missing.");
                return problems;
            }

            var actual = contentGroup.entries.ToDictionary(entry => entry.address, entry => entry.AssetPath, StringComparer.Ordinal);
            foreach (var pair in entries)
            {
                if (!actual.TryGetValue(pair.Key, out var path) || path != pair.Value)
                {
                    problems.Add(pair.Value + " must be an entry of " + contentGroup.Name + " with the address " + pair.Key + ".");
                }
            }

            problems.AddRange(actual.Keys.Where(address => !entries.ContainsKey(address))
                .Select(address => contentGroup.Name + " holds " + address + ", which no catalog names."));
            var shared = new HashSet<string>(SharedAssets(settings), StringComparer.Ordinal);
            var sharedEntries = new HashSet<string>(sharedGroup.entries.Select(entry => entry.AssetPath), StringComparer.Ordinal);
            problems.AddRange(shared.Where(path => !sharedEntries.Contains(path))
                .Select(path => path + " is shared by two bundles and must be an entry of " + sharedGroup.Name + "."));
            problems.AddRange(sharedEntries.Where(path => !shared.Contains(path))
                .Select(path => sharedGroup.Name + " holds " + path + ", which no two bundles share."));
            return problems;
        }

        // The assets that two bundles would both hold although neither is an entry, where at least
        // one of them is a bundle of body content, by bundle: the assets and the bundles.
        public static Dictionary<string, List<string>> Duplicates()
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            var bundles = BundlesOf(settings, null);
            var owners = OwnersOf(bundles, new Dictionary<string, string[]>(StringComparer.Ordinal));
            var body = new HashSet<string>(bundles.Where(bundle => bundle.IsBody).Select(bundle => bundle.Id), StringComparer.Ordinal);
            return owners
                .Where(pair => pair.Value.Count > 1 && pair.Value.Any(body.Contains))
                .ToDictionary(pair => pair.Key, pair => pair.Value.OrderBy(id => id, StringComparer.Ordinal).ToList());
        }

        // The assets that two bundles would both hold if the shared group held nothing, in path
        // order. Of the assets two bundles share, those that no other such asset holds become
        // entries first, such as a material before its texture, and the search goes on with them as
        // bundles of their own until no asset is shared, so an asset only one shared entry holds
        // stays in that entry's bundle.
        public static List<string> SharedAssets(AddressableAssetSettings settings)
        {
            var shared = new SortedSet<string>(StringComparer.Ordinal);
            var dependencies = new Dictionary<string, string[]>(StringComparer.Ordinal);
            while (true)
            {
                var bundles = BundlesOf(settings, shared);
                var body = new HashSet<string>(bundles.Where(bundle => bundle.IsBody).Select(bundle => bundle.Id), StringComparer.Ordinal);
                var found = new HashSet<string>(OwnersOf(bundles, dependencies)
                    .Where(pair => pair.Value.Count > 1 && pair.Value.Any(body.Contains))
                    .Select(pair => pair.Key)
                    .Where(path => !shared.Contains(path)), StringComparer.Ordinal);
                if (found.Count == 0)
                {
                    return shared.ToList();
                }

                var held = new HashSet<string>(StringComparer.Ordinal);
                foreach (var path in found)
                {
                    var open = new Stack<string>(DirectDependencies(path, dependencies));
                    var visited = new HashSet<string>(StringComparer.Ordinal);
                    while (open.Count > 0)
                    {
                        var dependency = open.Pop();
                        if (!visited.Add(dependency))
                        {
                            continue;
                        }

                        held.Add(dependency);
                        foreach (var next in DirectDependencies(dependency, dependencies))
                        {
                            open.Push(next);
                        }
                    }
                }

                // Assets that hold each other all become entries.
                var top = found.Where(path => !held.Contains(path)).ToArray();
                shared.UnionWith(top.Length > 0 ? top : found.ToArray());
            }
        }

        private static int SetEntries(AddressableAssetSettings settings, AddressableAssetGroup group, Dictionary<string, string> expected)
        {
            var changes = 0;
            foreach (var pair in expected)
            {
                var entry = settings.FindAssetEntry(pair.Key);
                if (entry == null || entry.parentGroup != group)
                {
                    entry = settings.CreateOrMoveEntry(pair.Key, group, false, false);
                    changes++;
                }

                if (entry.address != pair.Value)
                {
                    entry.SetAddress(pair.Value, false);
                    changes++;
                }
            }

            foreach (var entry in group.entries.ToArray())
            {
                if (!expected.ContainsKey(entry.guid))
                {
                    group.RemoveAssetEntry(entry, false);
                    changes++;
                }
            }

            return changes;
        }

        // The bundles a build makes: one per entry of a group packed separately and one per group
        // packed together, each with the assets it holds as entries. The body content's entries and
        // the shared ones, given or as the settings hold them, are bundles of their own.
        private static List<Bundle> BundlesOf(AddressableAssetSettings settings, ICollection<string> shared)
        {
            var bundles = new List<Bundle>();
            foreach (var group in settings.groups.Where(group => group != null))
            {
                var isBodyContent = group.Name == ClientAddressablesLayout.BodyContentGroup;
                var isShared = group.Name == ClientAddressablesLayout.BodyContentSharedGroup;
                if (isShared && shared != null)
                {
                    continue;
                }

                var schema = group.GetSchema<BundledAssetGroupSchema>();
                if (schema == null || !schema.IncludeInBuild)
                {
                    continue;
                }

                var together = schema.BundleMode == BundledAssetGroupSchema.BundlePackingMode.PackTogether;
                Bundle whole = null;
                foreach (var entry in group.entries)
                {
                    var assets = new List<AddressableAssetEntry>();
                    entry.GatherAllAssets(assets, true, true, false);
                    var paths = assets.Select(asset => asset.AssetPath).Where(IsAsset).ToList();
                    if (together)
                    {
                        whole = whole ?? new Bundle(group.Name, isBodyContent || isShared);
                        whole.Assets.AddRange(paths);
                    }
                    else
                    {
                        var bundle = new Bundle(entry.AssetPath, isBodyContent || isShared);
                        bundle.Assets.AddRange(paths);
                        bundles.Add(bundle);
                    }
                }

                if (whole != null)
                {
                    bundles.Add(whole);
                }
            }

            if (shared != null)
            {
                foreach (var path in shared)
                {
                    var bundle = new Bundle(path, true);
                    bundle.Assets.Add(path);
                    bundles.Add(bundle);
                }
            }

            return bundles;
        }

        // Every asset the bundles hold without its being an entry, with the bundles that hold it.
        private static Dictionary<string, HashSet<string>> OwnersOf(List<Bundle> bundles, Dictionary<string, string[]> dependencies)
        {
            var entries = new HashSet<string>(bundles.SelectMany(bundle => bundle.Assets), StringComparer.Ordinal);
            var owners = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var bundle in bundles)
            {
                var visited = new HashSet<string>(StringComparer.Ordinal);
                var open = new Stack<string>(bundle.Assets);
                while (open.Count > 0)
                {
                    var path = open.Pop();
                    foreach (var dependency in DirectDependencies(path, dependencies))
                    {
                        if (entries.Contains(dependency) || !visited.Add(dependency))
                        {
                            continue;
                        }

                        if (!owners.TryGetValue(dependency, out var holders))
                        {
                            holders = new HashSet<string>(StringComparer.Ordinal);
                            owners.Add(dependency, holders);
                        }

                        holders.Add(bundle.Id);
                        open.Push(dependency);
                    }
                }
            }

            return owners;
        }

        private static string[] DirectDependencies(string path, Dictionary<string, string[]> dependencies)
        {
            if (!dependencies.TryGetValue(path, out var found))
            {
                found = AssetDatabase.GetDependencies(path, false)
                    .Where(dependency => dependency != path && IsAsset(dependency))
                    .ToArray();
                dependencies.Add(path, found);
            }

            return found;
        }

        private static bool IsAsset(string path)
        {
            return (path.StartsWith("Assets/", StringComparison.Ordinal) || path.StartsWith("Packages/", StringComparison.Ordinal))
                && !AssetDatabase.IsValidFolder(path)
                && !Ignored.Any(extension => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
        }

        private sealed class Bundle
        {
            public Bundle(string id, bool isBody)
            {
                Id = id;
                IsBody = isBody;
            }

            public string Id { get; }

            public bool IsBody { get; }

            public List<string> Assets { get; } = new List<string>();
        }
    }
}
