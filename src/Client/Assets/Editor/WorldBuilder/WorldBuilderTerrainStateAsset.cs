using UnityEditor;
using UnityEngine;

namespace Mmorpg.Client.Editor
{
    // The hidden part of a terrain data asset that keeps the World Builder's state of the terrain,
    // compressed, so the state goes wherever the terrain data goes: it is saved, moved, and copied
    // with it. Builds leave it out.
    internal sealed class WorldBuilderTerrainStateAsset : ScriptableObject, ISerializationCallbackReceiver
    {
        [SerializeField]
        private byte[] encoded = new byte[0];

        [System.NonSerialized]
        private WorldBuilderTerrainState state;

        // Whether the state changed since it was encoded.
        [System.NonSerialized]
        private bool changed;

        public WorldBuilderTerrainState State
        {
            get
            {
                if (state == null)
                {
                    state = WorldBuilderTerrainState.Decode(encoded, this);
                }

                return state;
            }
        }

        public static WorldBuilderTerrainStateAsset Create()
        {
            var asset = CreateInstance<WorldBuilderTerrainStateAsset>();
            asset.name = "WorldBuilderState";
            asset.hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSaveInBuild;
            return asset;
        }

        // The state changed, so it is encoded again once Unity saves the asset.
        public void Changed(WorldBuilderTerrainState changedState)
        {
            state = changedState;
            changed = true;
            EditorUtility.SetDirty(this);
        }

        public void OnBeforeSerialize()
        {
            if (changed && state != null)
            {
                encoded = state.Encode();
                changed = false;
            }
        }

        // What Unity read, such as the asset as saved once its unsaved changes are thrown away,
        // replaces what was decoded before.
        public void OnAfterDeserialize()
        {
            state = null;
            changed = false;
        }
    }
}
