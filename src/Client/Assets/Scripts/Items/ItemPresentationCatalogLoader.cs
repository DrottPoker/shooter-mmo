using System;
using System.Collections.Generic;
using System.Linq;
using Mmorpg.Client.Core;
using Mmorpg.WorldData.Items.Presentation;
using UnityEngine;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace Mmorpg.Client.Items
{
    // Loads the bundled item presentation catalog through Addressables once per gameplay
    // revision, and keeps every icon it resolves loaded for the lifetime of the process.
    public static class ItemPresentationCatalogLoader
    {
        private static readonly Dictionary<string, AsyncOperationHandle<Sprite>> IconLoads =
            new Dictionary<string, AsyncOperationHandle<Sprite>>(StringComparer.Ordinal);
        private static readonly HashSet<string> MissingIcons =
            new HashSet<string>(StringComparer.Ordinal);
        private static string cachedKey;
        private static ItemPresentationCatalogIndex cachedIndex;

        // Static state survives entering Play Mode when domain reload is disabled. Handles from
        // the previous Play Mode session are no longer valid, so they are dropped, not released.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            cachedKey = null;
            cachedIndex = null;
            IconLoads.Clear();
            MissingIcons.Clear();
        }

        public static bool TryLoadOnce(
            string expectedSourceCatalogRevision,
            IEnumerable<string> expectedDefinitionIds,
            out ItemPresentationCatalogIndex index,
            out string error)
        {
            var expectedIds = expectedDefinitionIds == null
                ? Array.Empty<string>()
                : expectedDefinitionIds.OrderBy(value => value, StringComparer.Ordinal).ToArray();
            var cacheKey = expectedSourceCatalogRevision + "|" + string.Join("|", expectedIds);
            if (cachedIndex != null && string.Equals(cachedKey, cacheKey, StringComparison.Ordinal))
            {
                index = cachedIndex;
                error = string.Empty;
                return true;
            }

            index = null;
            if (!ClientAssets.TryReadText(
                    ClientAddresses.ItemPresentationCatalog,
                    out var json,
                    out var readError))
            {
                error = "Item presentation catalog is missing. " + readError;
                return false;
            }

            ItemPresentationCatalogDocument document;
            try
            {
                document = ItemPresentationCatalogJson.Deserialize(json);
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }

            if (!ItemPresentationCatalogIndex.TryCreate(
                document,
                expectedSourceCatalogRevision,
                expectedIds,
                out index,
                out error))
            {
                return false;
            }

            cachedKey = cacheKey;
            cachedIndex = index;
            return true;
        }

        // Returns null for an entry without an icon. A missing icon is reported once.
        public static Sprite LoadIcon(string iconAddress)
        {
            if (string.IsNullOrWhiteSpace(iconAddress) || MissingIcons.Contains(iconAddress))
            {
                return null;
            }

            if (IconLoads.TryGetValue(iconAddress, out var loaded))
            {
                return loaded.Result;
            }

            if (!ClientAssets.TryLoadNow<Sprite>(iconAddress, out var load, out var error))
            {
                MissingIcons.Add(iconAddress);
                ClientLog.Warning(ClientLogCategory.Client, "Item icon is unavailable: " + error);
                return null;
            }

            IconLoads.Add(iconAddress, load);
            return load.Result;
        }

        public static void ResetCache()
        {
            foreach (var load in IconLoads.Values)
            {
                ClientAssets.Release(load);
            }

            ResetStaticState();
        }
    }
}
