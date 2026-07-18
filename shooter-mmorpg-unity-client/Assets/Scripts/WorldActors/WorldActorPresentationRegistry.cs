using System;
using UnityEngine;

namespace ShooterMmo.WorldActors
{
    [Serializable]
    public sealed class WorldActorPresentationEntry
    {
        public string presentationArchetypeId;
        public GameObject prefab;
    }

    [CreateAssetMenu(
        fileName = "WorldActorPresentationRegistry",
        menuName = "Shooter MMO/World Actor Presentation Registry")]
    public sealed class WorldActorPresentationRegistry : ScriptableObject
    {
        [SerializeField]
        private WorldActorPresentationEntry[] entries =
            Array.Empty<WorldActorPresentationEntry>();

        public bool TryResolve(string presentationArchetypeId, out GameObject prefab)
        {
            for (var index = 0; index < entries.Length; index++)
            {
                var entry = entries[index];
                if (entry != null
                    && entry.prefab != null
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
