using System.Collections.Generic;
using System.Linq;
using Mmorpg.Client.Worlds;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Mmorpg.Client.Editor
{
    // Shapes the terrains of a scene that carry a WorldBuilderTerrain: lowers each to the beds of
    // the water above it and paints what it lowered with its bed layer. It first takes back what
    // it changed before, from the terrain's state, so a moved lake leaves no hole behind and a
    // removed one leaves the ground as it was; a sample changed by hand since keeps that change.
    // It reads and writes only the part of the terrain that the water reaches now or reached
    // before, and only what changes.
    internal static class TerrainCarver
    {
        // Points per side of an alphamap cell that tell how much of it lies on a bed.
        private const int ShareSamples = 3;

        // A point lies on a bed where the bed lies at least this far below the ground (m).
        private const float BedMargin = 0.001f;

        // Heights and weights closer than this hold the same value, as the terrain keeps them.
        private const float HeightTolerance = 0.5f / 32766f;
        private const float WeightTolerance = 1.5f / 255f;

        // Carves and paints every terrain of the scene that the water shapes, and gives the ground
        // back to every terrain that it shaped before and that carries no WorldBuilderTerrain any
        // more, such as after an undo of adding one. Terrain data that another terrain uses too
        // stays as it is, since each would carve it at its own place.
        public static bool Build(Scene scene, IReadOnlyList<WaterBed> beds)
        {
            var changed = false;
            var shared = WorldBuilderAssets.SharedTerrainData(scene);
            foreach (var terrain in WorldBuilderAssets.TerrainsOf(scene))
            {
                if (!terrain.gameObject.activeInHierarchy || shared.Contains(terrain.terrainData))
                {
                    continue;
                }

                if (terrain.TryGetComponent<WorldBuilderTerrain>(out var shaped))
                {
                    if (shaped.enabled)
                    {
                        changed |= Build(terrain, shaped.BedLayer, beds);
                    }
                }
                else if (WorldBuilderTerrainState.Holds(terrain.terrainData))
                {
                    changed |= Restore(terrain);
                }
            }

            return changed;
        }

        // Takes back everything the World Builder changed in the terrain and forgets it.
        public static bool Restore(Terrain terrain)
        {
            return terrain != null && terrain.terrainData != null && Build(terrain, null, new WaterBed[0]);
        }

        private static bool Build(Terrain terrain, TerrainLayer bedLayer, IReadOnlyList<WaterBed> beds)
        {
            var data = terrain.terrainData;
            var origin = terrain.GetPosition();
            var area = new Rect(origin.x, origin.z, data.size.x, data.size.z);
            var reaching = beds.Where(bed => bed.Bounds.Overlaps(area)).ToList();
            var state = WorldBuilderTerrainState.Of(data);
            var heights = new HeightBlock(data, origin, reaching, state);
            var changed = heights.Carve(reaching, state);
            changed |= PaintBeds(data, origin, reaching, bedLayer, heights, state);
            if (changed)
            {
                EditorUtility.SetDirty(data);
            }

            changed |= state.Keep(data);
            return changed;
        }

        // The block of heightmap samples that the water reaches now or reached before, with the
        // ground without water: what the state remembers where the terrain still holds what the
        // World Builder wrote, and what the terrain holds elsewhere.
        private sealed class HeightBlock
        {
            private readonly TerrainData data;
            private readonly Vector3 origin;
            private readonly int resolution;
            private readonly float spacingX;
            private readonly float spacingZ;
            private readonly float heightScale;
            private readonly RectInt block;
            private readonly float[,] current;
            private readonly float[,] ground;

            // How far the beds lowered each sample of the block, row by row, in meters.
            private float[] depths;

            public HeightBlock(TerrainData data, Vector3 origin, List<WaterBed> beds, WorldBuilderTerrainState state)
            {
                this.data = data;
                this.origin = origin;
                resolution = data.heightmapResolution;
                spacingX = data.size.x / (resolution - 1);
                spacingZ = data.size.z / (resolution - 1);
                heightScale = data.size.y;
                IReadOnlyDictionary<int, (float Ground, float Carved)> remembered = state.HeightmapResolution == resolution
                    ? state.Heights
                    : new Dictionary<int, (float Ground, float Carved)>();
                if (state.HeightmapResolution != resolution && state.Heights.Count > 0)
                {
                    Debug.LogWarning("[WORLD BUILDER] The heightmap of " + data.name + " changed its resolution, so its water beds start from the ground it holds now.");
                }

                block = Union(beds.Select(bed => SamplesOf(bed.Bounds)).Concat(remembered.Keys.Select(SampleOf)));
                current = block.width > 0 ? data.GetHeights(block.x, block.y, block.width, block.height) : new float[0, 0];
                ground = (float[,])current.Clone();
                foreach (var sample in remembered)
                {
                    var x = (sample.Key % resolution) - block.x;
                    var z = (sample.Key / resolution) - block.y;
                    if (Mathf.Abs(current[z, x] - sample.Value.Carved) <= HeightTolerance)
                    {
                        ground[z, x] = sample.Value.Ground;
                    }
                }
            }

            public bool IsEmpty
            {
                get { return block.width == 0 || block.height == 0; }
            }

            // The area in world X and Z that the block's samples span.
            public Rect Area
            {
                get
                {
                    return new Rect(
                        origin.x + (block.x * spacingX),
                        origin.z + (block.y * spacingZ),
                        Mathf.Max(0, block.width - 1) * spacingX,
                        Mathf.Max(0, block.height - 1) * spacingZ);
                }
            }

            // Lowers the ground to the lowest bed at every sample, writes what changed, and
            // remembers every sample it lowered.
            public bool Carve(List<WaterBed> beds, WorldBuilderTerrainState state)
            {
                var target = (float[,])ground.Clone();
                var size = data.size;
                foreach (var bed in beds)
                {
                    var samples = SamplesOf(bed.Bounds);
                    for (var z = samples.yMin; z < samples.yMax; z++)
                    {
                        for (var x = samples.xMin; x < samples.xMax; x++)
                        {
                            var point = new Vector2(origin.x + (x * spacingX), origin.z + (z * spacingZ));
                            var local = new Vector2Int(x - block.x, z - block.y);
                            var height = bed.HeightAt(point, origin.y + (ground[local.y, local.x] * size.y));
                            var normalized = Mathf.Clamp01((height - origin.y) / size.y);
                            if (normalized < target[local.y, local.x])
                            {
                                target[local.y, local.x] = normalized;
                            }
                        }
                    }
                }

                var changed = Write(target);
                var written = changed ? data.GetHeights(block.x, block.y, block.width, block.height) : current;
                var lowered = new Dictionary<int, (float Ground, float Carved)>(state.Heights.Count);
                depths = new float[block.width * block.height];
                for (var z = 0; z < block.height; z++)
                {
                    for (var x = 0; x < block.width; x++)
                    {
                        if (written[z, x] < ground[z, x] - HeightTolerance)
                        {
                            lowered[((z + block.y) * resolution) + x + block.x] = (ground[z, x], written[z, x]);
                            depths[(z * block.width) + x] = (ground[z, x] - written[z, x]) * size.y;
                        }
                    }
                }

                state.SetHeights(resolution, lowered);
                return changed;
            }

            // The heightmap samples around a span of world X, or of world Z, within the terrain.
            public (int First, int Last) SamplesAround(float from, float to, bool alongX)
            {
                var start = alongX ? origin.x : origin.z;
                var spacing = alongX ? spacingX : spacingZ;
                return (
                    Mathf.Clamp(Mathf.FloorToInt((from - start) / spacing), 0, resolution - 1),
                    Mathf.Clamp(Mathf.CeilToInt((to - start) / spacing), 0, resolution - 1));
            }

            // How many of the samples in the columns and rows the beds lowered, out of how many
            // there are.
            public int LoweredAmong((int First, int Last) columns, (int First, int Last) rows, out int samples)
            {
                samples = (columns.Last - columns.First + 1) * (rows.Last - rows.First + 1);
                var lowered = 0;
                for (var z = rows.First - block.y; z <= rows.Last - block.y; z++)
                {
                    for (var x = columns.First - block.x; x <= columns.Last - block.x; x++)
                    {
                        if (x >= 0 && z >= 0 && x < block.width && z < block.height && depths[(z * block.width) + x] > 0f)
                        {
                            lowered++;
                        }
                    }
                }

                return lowered;
            }

            // The share of points across a rect in world X and Z where the lowest bed lies below
            // the ground. The beds decide those points themselves, so the paint's edge follows the
            // beds' edge rather than the heightmap's grid.
            public float BedShare(Rect rect, List<WaterBed> beds)
            {
                var inside = 0;
                for (var pointZ = 0; pointZ < ShareSamples; pointZ++)
                {
                    for (var pointX = 0; pointX < ShareSamples; pointX++)
                    {
                        var point = new Vector2(
                            rect.xMin + (((pointX + 0.5f) / ShareSamples) * rect.width),
                            rect.yMin + (((pointZ + 0.5f) / ShareSamples) * rect.height));
                        inside += OnBed(beds, point) ? 1 : 0;
                    }
                }

                return (float)inside / (ShareSamples * ShareSamples);
            }

            // Whether the lowest bed at a point lies below the ground there.
            private bool OnBed(List<WaterBed> beds, Vector2 point)
            {
                var ground = GroundAt(point);
                foreach (var bed in beds)
                {
                    if (bed.HeightAt(point, ground) < ground - BedMargin)
                    {
                        return true;
                    }
                }

                return false;
            }

            // The ground without water at a point, between its four heightmap samples (m).
            private float GroundAt(Vector2 point)
            {
                var u = Mathf.Clamp((point.x - origin.x) / spacingX, 0f, resolution - 1);
                var v = Mathf.Clamp((point.y - origin.z) / spacingZ, 0f, resolution - 1);
                var x = Mathf.Min((int)u, resolution - 2);
                var z = Mathf.Min((int)v, resolution - 2);
                var fx = u - x;
                var height = Mathf.Lerp(
                    Mathf.Lerp(GroundSample(x, z), GroundSample(x + 1, z), fx),
                    Mathf.Lerp(GroundSample(x, z + 1), GroundSample(x + 1, z + 1), fx),
                    v - z);
                return origin.y + (height * heightScale);
            }

            // The ground without water at a sample, normalized; outside the block the terrain
            // holds the ground.
            private float GroundSample(int x, int z)
            {
                var localX = x - block.x;
                var localZ = z - block.y;
                return localX >= 0 && localZ >= 0 && localX < block.width && localZ < block.height
                    ? ground[localZ, localX]
                    : data.GetHeight(x, z) / heightScale;
            }

            // Writes the smallest block of heights that holds every changed sample.
            private bool Write(float[,] target)
            {
                int firstX = block.width, lastX = -1, firstZ = block.height, lastZ = -1;
                for (var z = 0; z < block.height; z++)
                {
                    for (var x = 0; x < block.width; x++)
                    {
                        if (Mathf.Abs(current[z, x] - target[z, x]) > HeightTolerance)
                        {
                            firstX = Mathf.Min(firstX, x);
                            lastX = Mathf.Max(lastX, x);
                            firstZ = Mathf.Min(firstZ, z);
                            lastZ = Mathf.Max(lastZ, z);
                        }
                    }
                }

                if (lastX < 0)
                {
                    return false;
                }

                var changed = new float[lastZ - firstZ + 1, lastX - firstX + 1];
                for (var z = firstZ; z <= lastZ; z++)
                {
                    for (var x = firstX; x <= lastX; x++)
                    {
                        changed[z - firstZ, x - firstX] = target[z, x];
                    }
                }

                data.SetHeights(block.x + firstX, block.y + firstZ, changed);
                return true;
            }

            // The samples a rect in world X and Z covers, within the terrain.
            private RectInt SamplesOf(Rect bounds)
            {
                var firstX = Mathf.Clamp(Mathf.FloorToInt((bounds.xMin - origin.x) / spacingX), 0, resolution);
                var lastX = Mathf.Clamp(Mathf.CeilToInt((bounds.xMax - origin.x) / spacingX) + 1, 0, resolution);
                var firstZ = Mathf.Clamp(Mathf.FloorToInt((bounds.yMin - origin.z) / spacingZ), 0, resolution);
                var lastZ = Mathf.Clamp(Mathf.CeilToInt((bounds.yMax - origin.z) / spacingZ) + 1, 0, resolution);
                return new RectInt(firstX, firstZ, Mathf.Max(0, lastX - firstX), Mathf.Max(0, lastZ - firstZ));
            }

            private RectInt SampleOf(int index)
            {
                return new RectInt(index % resolution, index / resolution, 1, 1);
            }
        }

        // Each alphamap cell takes the bed layer by its share on a bed, so the bed blends into the
        // ground around it, over the weights the cell had before. Without a bed layer or a bed it
        // only takes back what it painted before.
        private static bool PaintBeds(TerrainData data, Vector3 origin, List<WaterBed> beds, TerrainLayer bedLayer, HeightBlock heights, WorldBuilderTerrainState state)
        {
            var bed = bedLayer != null && beds.Count > 0 ? LayerIndex(data, bedLayer) : -1;
            var resolution = data.alphamapResolution;
            var layers = data.alphamapLayers;
            IReadOnlyDictionary<int, (float[] Ground, float Bed)> remembered = state.AlphamapResolution == resolution && state.AlphamapLayers <= layers
                ? state.Alphas
                : new Dictionary<int, (float[] Ground, float Bed)>();
            var painted = new Dictionary<int, (float[] Ground, float Bed)>(remembered.Count);
            if (layers == 0 || (bed < 0 && remembered.Count == 0))
            {
                state.SetAlphas(resolution, layers, bed, painted);
                return false;
            }

            var size = data.size;
            var cellX = size.x / resolution;
            var cellZ = size.z / resolution;
            var reached = bed >= 0 && !heights.IsEmpty ? CellsOf(heights.Area, origin, cellX, cellZ, resolution) : new RectInt(0, 0, 0, 0);
            var block = Union(new[] { reached }
                .Concat(remembered.Keys.Select(index => new RectInt(index % resolution, index / resolution, 1, 1))));
            if (block.width == 0)
            {
                state.SetAlphas(resolution, layers, bed, painted);
                return false;
            }

            var current = data.GetAlphamaps(block.x, block.y, block.width, block.height);
            var before = (float[,,])current.Clone();
            foreach (var cell in remembered)
            {
                var x = (cell.Key % resolution) - block.x;
                var z = (cell.Key / resolution) - block.y;
                if (HoldsPainted(current, z, x, cell.Value, state.BedLayerIndex))
                {
                    for (var layer = 0; layer < layers; layer++)
                    {
                        before[z, x, layer] = layer < cell.Value.Ground.Length ? cell.Value.Ground[layer] : 0f;
                    }
                }
            }

            // A cell lies wholly on a bed where the beds lowered every heightmap sample around it,
            // and off every bed where they lowered none; only a cell that a bed's edge crosses is
            // looked at closely.
            var columns = new (int First, int Last)[reached.width];
            for (var x = 0; x < reached.width; x++)
            {
                var west = origin.x + ((reached.x + x) * cellX);
                columns[x] = heights.SamplesAround(west, west + cellX, true);
            }

            var rows = new (int First, int Last)[reached.height];
            for (var z = 0; z < reached.height; z++)
            {
                var south = origin.z + ((reached.y + z) * cellZ);
                rows[z] = heights.SamplesAround(south, south + cellZ, false);
            }

            var target = (float[,,])before.Clone();
            for (var z = reached.yMin; z < reached.yMax; z++)
            {
                for (var x = reached.xMin; x < reached.xMax; x++)
                {
                    var lowered = heights.LoweredAmong(columns[x - reached.x], rows[z - reached.y], out var samples);
                    if (lowered == 0)
                    {
                        continue;
                    }

                    var share = lowered == samples
                        ? 1f
                        : heights.BedShare(new Rect(origin.x + (x * cellX), origin.z + (z * cellZ), cellX, cellZ), beds);
                    if (share <= 0f)
                    {
                        continue;
                    }

                    var localX = x - block.x;
                    var localZ = z - block.y;
                    var weights = new float[layers];
                    for (var layer = 0; layer < layers; layer++)
                    {
                        weights[layer] = before[localZ, localX, layer];
                        target[localZ, localX, layer] = (weights[layer] * (1f - share)) + (layer == bed ? share : 0f);
                    }

                    painted.Add((z * resolution) + x, (weights, share));
                }
            }

            var changed = !SameWeights(current, target);
            if (changed)
            {
                data.SetAlphamaps(block.x, block.y, target);
            }

            state.SetAlphas(resolution, layers, bed, painted);
            return changed;
        }

        // The alphamap cells a rect in world X and Z covers, within the terrain.
        private static RectInt CellsOf(Rect bounds, Vector3 origin, float cellX, float cellZ, int resolution)
        {
            var firstX = Mathf.Clamp(Mathf.FloorToInt((bounds.xMin - origin.x) / cellX), 0, resolution);
            var lastX = Mathf.Clamp(Mathf.FloorToInt((bounds.xMax - origin.x) / cellX) + 1, 0, resolution);
            var firstZ = Mathf.Clamp(Mathf.FloorToInt((bounds.yMin - origin.z) / cellZ), 0, resolution);
            var lastZ = Mathf.Clamp(Mathf.FloorToInt((bounds.yMax - origin.z) / cellZ) + 1, 0, resolution);
            return new RectInt(firstX, firstZ, Mathf.Max(0, lastX - firstX), Mathf.Max(0, lastZ - firstZ));
        }

        // The smallest block that holds every rect, empty without any.
        private static RectInt Union(IEnumerable<RectInt> rects)
        {
            int xMin = int.MaxValue, yMin = int.MaxValue, xMax = int.MinValue, yMax = int.MinValue;
            foreach (var rect in rects)
            {
                if (rect.width <= 0 || rect.height <= 0)
                {
                    continue;
                }

                xMin = Mathf.Min(xMin, rect.xMin);
                yMin = Mathf.Min(yMin, rect.yMin);
                xMax = Mathf.Max(xMax, rect.xMax);
                yMax = Mathf.Max(yMax, rect.yMax);
            }

            return xMax < xMin ? new RectInt(0, 0, 0, 0) : new RectInt(xMin, yMin, xMax - xMin, yMax - yMin);
        }

        // The terrain layer's index, adding it to the terrain's layers when it is not one of them.
        private static int LayerIndex(TerrainData data, TerrainLayer layer)
        {
            var layers = data.terrainLayers;
            var index = System.Array.IndexOf(layers, layer);
            if (index >= 0)
            {
                return index;
            }

            data.terrainLayers = layers.Concat(new[] { layer }).ToArray();
            return layers.Length;
        }

        // Whether a cell still holds the bed painted over its ground weights.
        private static bool HoldsPainted(float[,,] alphamaps, int z, int x, (float[] Ground, float Bed) cell, int bedLayer)
        {
            var layers = alphamaps.GetLength(2);
            for (var layer = 0; layer < layers; layer++)
            {
                var weight = layer < cell.Ground.Length ? cell.Ground[layer] : 0f;
                var painted = (weight * (1f - cell.Bed)) + (layer == bedLayer ? cell.Bed : 0f);
                if (Mathf.Abs(alphamaps[z, x, layer] - painted) > WeightTolerance)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool SameWeights(float[,,] first, float[,,] second)
        {
            int height = first.GetLength(0), width = first.GetLength(1), layers = first.GetLength(2);
            for (var z = 0; z < height; z++)
            {
                for (var x = 0; x < width; x++)
                {
                    for (var layer = 0; layer < layers; layer++)
                    {
                        if (Mathf.Abs(first[z, x, layer] - second[z, x, layer]) > WeightTolerance)
                        {
                            return false;
                        }
                    }
                }
            }

            return true;
        }
    }
}
