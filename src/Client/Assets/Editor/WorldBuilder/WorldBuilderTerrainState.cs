using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Mmorpg.Client.Editor
{
    // What the World Builder changed in a terrain, so it can take that back before it shapes the
    // terrain again: for every heightmap sample it lowered, the height before and the height it
    // wrote, and for every alphamap cell it painted, the weights before and how much of the bed
    // it painted. A sample that no longer holds what it wrote was changed by hand since and keeps
    // that change. Terrain data that is an asset keeps its state in a hidden part of that asset,
    // so the state is saved, moved, and copied together with the terrain and never out of step
    // with it; other terrain data keeps it in memory.
    internal sealed class WorldBuilderTerrainState
    {
        private const int Magic = 0x53544257;
        private const int Version = 1;

        private static readonly Dictionary<TerrainData, WorldBuilderTerrainState> InMemory =
            new Dictionary<TerrainData, WorldBuilderTerrainState>();

        // The parts of terrain assets that keep their states, as found in this session.
        private static readonly Dictionary<TerrainData, WorldBuilderTerrainStateAsset> Kept =
            new Dictionary<TerrainData, WorldBuilderTerrainStateAsset>();

        private Dictionary<int, (float Ground, float Carved)> heights = new Dictionary<int, (float, float)>();
        private Dictionary<int, (float[] Ground, float Bed)> alphas = new Dictionary<int, (float[], float)>();

        // Whether the state changed since it was last kept with its terrain.
        private bool changed;

        public int HeightmapResolution { get; private set; }

        // Samples by index z * resolution + x, with the heights normalized as the terrain keeps them.
        public IReadOnlyDictionary<int, (float Ground, float Carved)> Heights
        {
            get { return heights; }
        }

        public int AlphamapResolution { get; private set; }

        public int AlphamapLayers { get; private set; }

        // The layer the beds were painted with.
        public int BedLayerIndex { get; private set; } = -1;

        // Cells by index z * resolution + x: the weights of every layer before, and the bed's share.
        public IReadOnlyDictionary<int, (float[] Ground, float Bed)> Alphas
        {
            get { return alphas; }
        }

        public bool IsEmpty
        {
            get { return heights.Count == 0 && alphas.Count == 0; }
        }

        // The terrain's state, empty while the World Builder changed nothing in it.
        public static WorldBuilderTerrainState Of(TerrainData data)
        {
            // Terrain data that was destroyed has nothing left to take back.
            foreach (var gone in InMemory.Keys.Where(key => key == null).ToList())
            {
                InMemory.Remove(gone);
            }

            InMemory.TryGetValue(data, out var inMemory);
            if (!AssetDatabase.Contains(data))
            {
                return inMemory ?? new WorldBuilderTerrainState();
            }

            var asset = AssetOf(data);
            if (asset != null)
            {
                return asset.State;
            }

            // Terrain data saved as an asset since the water shaped it, such as with CreateAsset,
            // takes its state along into a hidden part of the asset.
            if (inMemory != null)
            {
                InMemory.Remove(data);
                inMemory.changed = true;
                inMemory.Keep(data);
                return inMemory;
            }

            return new WorldBuilderTerrainState();
        }

        // Whether the World Builder changed anything in the terrain that it can take back.
        public static bool Holds(TerrainData data)
        {
            return !Of(data).IsEmpty;
        }

        // Forgets what the World Builder changed in the terrain, such as when a builder writes its
        // whole ground anew.
        public static void Forget(TerrainData data)
        {
            var state = Of(data);
            state.SetHeights(0, new Dictionary<int, (float, float)>());
            state.SetAlphas(0, 0, -1, new Dictionary<int, (float[], float)>());
            state.Keep(data);
        }

        // Remembers these heights instead, and keeps the dictionary. Returns whether they differ
        // from what it remembered.
        public bool SetHeights(int resolution, Dictionary<int, (float Ground, float Carved)> remembered)
        {
            var same = remembered.Count == heights.Count && (heights.Count == 0 || resolution == HeightmapResolution);
            foreach (var sample in remembered)
            {
                if (!same)
                {
                    break;
                }

                same = heights.TryGetValue(sample.Key, out var kept) && kept == sample.Value;
            }

            if (same)
            {
                return false;
            }

            HeightmapResolution = resolution;
            heights = remembered;
            changed = true;
            return true;
        }

        // Remembers these cells instead, and keeps the dictionary. Returns whether they differ
        // from what it remembered.
        public bool SetAlphas(int resolution, int layers, int bedLayerIndex, Dictionary<int, (float[] Ground, float Bed)> remembered)
        {
            var same = remembered.Count == alphas.Count
                && (alphas.Count == 0 || (resolution == AlphamapResolution && layers == AlphamapLayers && bedLayerIndex == BedLayerIndex));
            foreach (var cell in remembered)
            {
                if (!same)
                {
                    break;
                }

                same = alphas.TryGetValue(cell.Key, out var kept) && kept.Bed == cell.Value.Bed && SameWeights(kept.Ground, cell.Value.Ground);
            }

            if (same)
            {
                return false;
            }

            AlphamapResolution = resolution;
            AlphamapLayers = layers;
            BedLayerIndex = bedLayerIndex;
            alphas = remembered;
            changed = true;
            return true;
        }

        // Keeps the state with the terrain data once it changed, in a hidden part of the data's
        // asset. The part stays once it is made, also while the state is empty, so the asset
        // keeps the same parts when the water goes and comes back and changes only with the
        // state. Returns whether the state changed.
        public bool Keep(TerrainData data)
        {
            if (!changed)
            {
                return false;
            }

            changed = false;
            if (!AssetDatabase.Contains(data))
            {
                if (IsEmpty)
                {
                    InMemory.Remove(data);
                }
                else
                {
                    InMemory[data] = this;
                }

                return true;
            }

            var asset = AssetOf(data);
            if (asset == null && !IsEmpty)
            {
                asset = WorldBuilderTerrainStateAsset.Create();
                AssetDatabase.AddObjectToAsset(asset, data);
                Kept[data] = asset;
            }

            if (asset != null)
            {
                asset.Changed(this);
            }

            return true;
        }

        // In index order, so the same state always encodes the same.
        public byte[] Encode()
        {
            using (var memory = new MemoryStream())
            {
                using (var zip = new GZipStream(memory, CompressionMode.Compress))
                using (var writer = new BinaryWriter(zip))
                {
                    writer.Write(Magic);
                    writer.Write(Version);
                    writer.Write(HeightmapResolution);
                    writer.Write(heights.Count);
                    foreach (var index in Sorted(heights.Keys))
                    {
                        var sample = heights[index];
                        writer.Write(index);
                        writer.Write(sample.Ground);
                        writer.Write(sample.Carved);
                    }

                    writer.Write(AlphamapResolution);
                    writer.Write(AlphamapLayers);
                    writer.Write(BedLayerIndex);
                    writer.Write(alphas.Count);
                    foreach (var index in Sorted(alphas.Keys))
                    {
                        var cell = alphas[index];
                        writer.Write(index);
                        foreach (var weight in cell.Ground)
                        {
                            writer.Write(weight);
                        }

                        writer.Write(cell.Bed);
                    }
                }

                return memory.ToArray();
            }
        }

        public static WorldBuilderTerrainState Decode(byte[] encoded, Object owner)
        {
            var state = new WorldBuilderTerrainState();
            if (encoded == null || encoded.Length == 0)
            {
                return state;
            }

            using (var memory = new MemoryStream(encoded))
            using (var zip = new GZipStream(memory, CompressionMode.Decompress))
            using (var reader = new BinaryReader(zip))
            {
                if (reader.ReadInt32() != Magic || reader.ReadInt32() != Version)
                {
                    Debug.LogWarning("[WORLD BUILDER] The World Builder state of " + AssetDatabase.GetAssetPath(owner) + " is not one this version reads; the terrain starts from what it holds.", owner);
                    return state;
                }

                state.HeightmapResolution = reader.ReadInt32();
                for (var count = reader.ReadInt32(); count > 0; count--)
                {
                    state.heights[reader.ReadInt32()] = (reader.ReadSingle(), reader.ReadSingle());
                }

                state.AlphamapResolution = reader.ReadInt32();
                state.AlphamapLayers = reader.ReadInt32();
                state.BedLayerIndex = reader.ReadInt32();
                for (var count = reader.ReadInt32(); count > 0; count--)
                {
                    var index = reader.ReadInt32();
                    var ground = new float[state.AlphamapLayers];
                    for (var layer = 0; layer < ground.Length; layer++)
                    {
                        ground[layer] = reader.ReadSingle();
                    }

                    state.alphas[index] = (ground, reader.ReadSingle());
                }
            }

            return state;
        }

        private static WorldBuilderTerrainStateAsset AssetOf(TerrainData data)
        {
            foreach (var gone in Kept.Keys.Where(key => key == null).ToList())
            {
                Kept.Remove(gone);
            }

            // A part that was destroyed, such as by a reimport of its asset, is looked up again.
            if (Kept.TryGetValue(data, out var known) && known != null)
            {
                return known;
            }

            var found = AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(data))
                .OfType<WorldBuilderTerrainStateAsset>()
                .FirstOrDefault();
            if (found != null)
            {
                Kept[data] = found;
            }
            else
            {
                Kept.Remove(data);
            }

            return found;
        }

        private static bool SameWeights(float[] first, float[] second)
        {
            if (first.Length != second.Length)
            {
                return false;
            }

            for (var layer = 0; layer < first.Length; layer++)
            {
                if (first[layer] != second[layer])
                {
                    return false;
                }
            }

            return true;
        }

        private static List<int> Sorted(IEnumerable<int> keys)
        {
            var sorted = new List<int>(keys);
            sorted.Sort();
            return sorted;
        }
    }
}
