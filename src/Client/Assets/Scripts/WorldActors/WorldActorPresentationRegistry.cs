using System;
using UnityEngine;
using UnityEngine.AddressableAssets;

namespace Mmorpg.Client.WorldActors
{
    [Serializable]
    public sealed class WorldActorPresentationEntry
    {
        public string presentationArchetypeId;

        // An Addressables prefab, loaded only when an actor of this archetype is presented.
        public AssetReferenceGameObject prefab = new AssetReferenceGameObject(string.Empty);
    }

    [CreateAssetMenu(
        fileName = "WorldActorPresentationRegistry",
        menuName = "MMORPG/World Actor Presentation Registry")]
    public sealed class WorldActorPresentationRegistry : ScriptableObject
    {
        [SerializeField]
        private WorldActorPresentationEntry[] entries =
            Array.Empty<WorldActorPresentationEntry>();

        public bool TryResolve(string presentationArchetypeId, out AssetReferenceGameObject prefab)
        {
            for (var index = 0; index < entries.Length; index++)
            {
                var entry = entries[index];
                if (entry != null
                    && entry.prefab != null
                    && entry.prefab.RuntimeKeyIsValid()
                    && string.Equals(
                        entry.presentationArchetypeId,
                        presentationArchetypeId,
                        StringComparison.Ordinal))
                {
                    prefab = entry.prefab;
                    return true;
                }
            }

            prefab = null;
            return false;
        }
    }
}
