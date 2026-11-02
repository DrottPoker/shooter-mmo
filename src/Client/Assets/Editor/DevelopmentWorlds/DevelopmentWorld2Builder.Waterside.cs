using System.Collections.Generic;
using Mmorpg.Client.Worlds;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Mmorpg.Client.Editor
{
    // The northwest waterside, the water test area: a plateau with a lake, a waterfall that
    // falls into the lake from a rock outcrop, a river from the lake to the sea with a ford and
    // a bridge, and the sea in the World's northwest corner, where deep water instead of the
    // boundary walls ends the World. Every height is a pure
    // function of the position, so every rebuild produces the same terrain and water.
    public static partial class DevelopmentWorld2Builder
    {
        public static readonly Vector3 WatersideOrigin = new Vector3(-192f, 0f, 64f);
        public static readonly Vector3 WatersideSize = new Vector3(128f, 8f, 128f);

        public const float WatersidePlateauHeight = 4f;
        public const float LakeSurface = 3.6f;
        public const float SeaSurface = 3f;

        // The boundary walls stop out in the deep sea.
        public const float NorthWallStartX = -146f;
        public const float WestWallEndZ = 146f;

        public const string LakeBodyId = "waterside-lake";
        public const string RiverBodyId = "waterside-river";
        public const string SeaBodyId = "waterside-sea";

        // World X and Z of the lake's center and of the corner the sea surrounds.
        public static readonly Vector2 LakeCenter = new Vector2(-108f, 112f);
        public static readonly Vector2 SeaCorner = new Vector2(-192f, 192f);

        private const int WatersideHeightmapResolution = 129;
        private const int WatersideAlphamapResolution = 256;
        private const float WatersideBaseHeight = 0.02f;
        private const float WatersideRampLength = 16f;
        private const float FloorHeightMinimum = 0.1f;

        // The lake's floor falls 0.3 m per meter from its shore, down to 3 m deep.
        private const float LakeShoreRadius = 13f;
        private const float LakeBankWidth = 2f;
        private const float LakeFloorSlope = 0.3f;
        private const float LakeMaximumDepth = 3f;
        private const float LakeWaterRadius = 14.5f;

        // The sea's shore lies 60 m from the corner; the floor falls 0.15 m per meter.
        private const float SeaShoreDistance = 60f;
        private const float SeaBeachWidth = 10f;
        private const float SeaFloorSlope = 0.15f;
        private const float SeaWaterRadius = 62f;
        private const float OuterSeaExtent = 400f;

        // The river is 8 m wide, 2 m deep, and 0.8 m deep along the ford.
        private const float RiverHalfWidth = 4f;
        private const float RiverBankWidth = 3f;
        private const float RiverDepth = 2f;
        private const float RiverEndSurface = 3.02f;
        private const float RiverWaterHalfWidth = 5.5f;
        private const float FordDepth = 0.8f;
        private const float FordDistance = 20f;
        private const float FordHalfLength = 3f;
        private const float FordTransition = 3f;
        private const float BridgeDistance = 36f;
        private const float BridgeDeckTop = 4.2f;

        // A rock outcrop on the lake's north shore whose top lies 4 m above the lake, east of
        // where the river leaves it, with its face toward the afternoon sun. A stream runs in a
        // channel along its top from a spring under a boulder and falls over its lip into a pool
        // in the lake, shallow enough to wade under the fall.
        public const string StreamBodyId = "waterside-stream";
        private static readonly Vector2 OutcropDirection = new Vector2(0.34202014f, 0.93969262f);
        private const float OutcropLipDistance = 13.3f;
        private const float OutcropLength = 9f;
        private const float OutcropHalfWidth = 5f;
        private const float OutcropTop = 7.6f;
        private const float OutcropBottom = 2.4f;
        private const float StreamHalfWidth = 1.5f;
        private const float StreamBedTop = 7.2f;
        private const float StreamSurface = 7.5f;
        private const float SpringLength = 1.2f;
        private const float WaterfallSpeed = 1.6f;
        private const float PlungePoolRadius = 3.2f;
        private const float PlungePoolDepth = 1f;
        private const float Gravity = 9.81f;

        private static float? riverFallStart;

        // The river's center line in World X and Z, from inside the lake to the sea's shore.
        private static readonly Vector2[] RiverPoints =
        {
            new Vector2(-113f, 119f),
            new Vector2(-124f, 130f),
            new Vector2(-134f, 138f),
            new Vector2(-144f, 146f),
            new Vector2(-149f, 150f)
        };

        // The ford and the bridge: their place on the river and the direction across it.
        public static Vector2 FordCenter
        {
            get { return RiverPointAt(FordDistance, out _); }
        }

        public static Vector2 BridgeCenter
        {
            get { return RiverPointAt(BridgeDistance, out _); }
        }

        public static Vector2 BridgeAcross
        {
            get
            {
                RiverPointAt(BridgeDistance, out var direction);
                return new Vector2(direction.y, -direction.x);
            }
        }

        // The middle of the outcrop's lip, and the direction across the outcrop.
        public static Vector2 WaterfallLip
        {
            get { return LakeCenter + (OutcropDirection * OutcropLipDistance); }
        }

        private static Vector2 OutcropAcross
        {
            get { return new Vector2(-OutcropDirection.y, OutcropDirection.x); }
        }

        // The water leaves the lip at the waterfall's speed and lands this far out, where the
        // pool lies.
        private static float WaterfallSeconds
        {
            get { return Mathf.Sqrt(2f * (StreamSurface - LakeSurface) / Gravity); }
        }

        public static Vector2 PlungePoolCenter
        {
            get { return WaterfallLip - (OutcropDirection * (WaterfallSpeed * WaterfallSeconds)); }
        }

        private static void BuildWaterside(DevelopmentWorldBuilder builder)
        {
            var waterside = builder.Group(builder.Environment, "Waterside");
            var palette = builder.Palette;
            var terrainObject = Terrain.CreateTerrainGameObject(CreateOrUpdateWatersideTerrainData());
            terrainObject.name = "WatersideTerrain";
            terrainObject.transform.SetParent(waterside, false);
            terrainObject.transform.localPosition = WatersideOrigin;
            var terrain = terrainObject.GetComponent<Terrain>();

            // The shorelines follow the drawn terrain, so it keeps every height sample near
            // the camera instead of the default simplification.
            terrain.heightmapPixelError = 1f;
            if (GraphicsSettings.currentRenderPipeline != null)
            {
                terrain.materialTemplate = GraphicsSettings.currentRenderPipeline.defaultTerrainMaterial;
            }

            var water = builder.Group(waterside, "Water");
            WaterAssets.CreateOrUpdate();
            WaterBodyObject(water, "Lake", LakeBodyId, WaterBodyKind.Lake, BuildLakeSurface());
            WaterBodyObject(water, "River", RiverBodyId, WaterBodyKind.River, BuildRiverSurface());
            WaterBodyObject(water, "Sea", SeaBodyId, WaterBodyKind.Sea, BuildSeaSurface());

            // The sea beyond the World only shows; nothing reaches it, so it needs no collision.
            var beyond = new GameObject("SeaBeyondTheWorld");
            beyond.transform.SetParent(water, false);
            beyond.AddComponent<MeshFilter>().sharedMesh = BuildOuterSeaSurface();
            ConfigureWaterRenderer(beyond.AddComponent<MeshRenderer>(), WaterAssets.LoadMaterial(WaterBodyKind.Sea));
            builder.Box(water, "WestSeabed", new Vector3(SeaCorner.x - (OuterSeaExtent * 0.5f), -0.25f, SeaCorner.y),
                new Vector3(OuterSeaExtent, 0.5f, OuterSeaExtent * 2f), palette.Sand, false);
            builder.Box(water, "NorthSeabed", new Vector3(SeaCorner.x + (OuterSeaExtent * 0.5f), -0.25f, SeaCorner.y + (OuterSeaExtent * 0.5f)),
                new Vector3(OuterSeaExtent, 0.5f, OuterSeaExtent), palette.Sand, false);

            BuildWatersideBridge(builder, waterside);
            BuildWaterfall(builder, waterside, water);
            builder.Sign(waterside, "WatersideSign", new Vector3(-60f, 0f, 60f), -45f, "WATERSIDE\nLake, river, ford,\nbridge, sea");
            builder.Sign(waterside, "LakeSign", GroundPoint(LakeCenter + new Vector2(0f, -17f)), 0f,
                "LAKE\nWade from 0.5 m deep,\ndeep water stops at 1.2 m");
            RiverPointAt(FordDistance, out var fordDirection);
            var fordAcross = new Vector2(fordDirection.y, -fordDirection.x);
            builder.Sign(waterside, "FordSign", GroundPoint(FordCenter - (fordAcross * 9f)), Yaw(fordDirection),
                "FORD\n0.8 m deep,\nwade across");
            RiverPointAt(BridgeDistance, out var bridgeDirection);
            builder.Sign(waterside, "BridgeSign", GroundPoint(BridgeCenter - (BridgeAcross * 10f) - (bridgeDirection * 4f)), Yaw(bridgeDirection),
                "BRIDGE\nOver 2 m deep water");
            builder.Sign(waterside, "SeaSign", GroundPoint(new Vector2(-130f, 157f)), -45f,
                "SEA\nDeep water is the\nedge of the World");
        }

        // The ground of the waterside: a plateau that ramps down to the World's ground at its
        // east and south edges, cut by the lake, the river, and the sea.
        private static float WatersideHeight(float x, float z, out bool waterBed)
        {
            var u = x - WatersideOrigin.x;
            var v = z - WatersideOrigin.z;
            var ramp = Mathf.Clamp01(Mathf.Min(WatersideSize.x - u, v) / WatersideRampLength);
            var plateau = WatersideBaseHeight + ((WatersidePlateauHeight - WatersideBaseHeight) * ramp);
            var water = Mathf.Min(LakeFloor(x, z), Mathf.Min(RiverFloor(x, z), SeaFloor(x, z)));
            waterBed = water < plateau;
            return Mathf.Max(WatersideBaseHeight, Mathf.Min(plateau, water));
        }

        // The lake's floor, with the pool the waterfall falls into, which reaches under the
        // outcrop's lip.
        private static float LakeFloor(float x, float z)
        {
            var point = new Vector2(x, z);
            var radius = Vector2.Distance(point, LakeCenter);
            if (radius >= LakeShoreRadius + LakeBankWidth)
            {
                return float.MaxValue;
            }

            var floor = radius >= LakeShoreRadius
                ? LakeSurface + ((WatersidePlateauHeight - LakeSurface) * (radius - LakeShoreRadius) / LakeBankWidth)
                : Mathf.Max(LakeSurface - LakeMaximumDepth, LakeSurface - (LakeFloorSlope * (LakeShoreRadius - radius)));
            var poolDistance = Vector2.Distance(point, PlungePoolCenter);
            if (poolDistance >= PlungePoolRadius)
            {
                return floor;
            }

            return Mathf.Min(floor, LakeSurface - (PlungePoolDepth * (1f - Smooth01(poolDistance / PlungePoolRadius))));
        }

        private static float SeaFloor(float x, float z)
        {
            var distance = Vector2.Distance(new Vector2(x, z), SeaCorner);
            if (distance >= SeaShoreDistance + SeaBeachWidth)
            {
                return float.MaxValue;
            }

            if (distance >= SeaShoreDistance)
            {
                return SeaSurface + ((WatersidePlateauHeight - SeaSurface) * (distance - SeaShoreDistance) / SeaBeachWidth);
            }

            return Mathf.Max(FloorHeightMinimum, SeaSurface - (SeaFloorSlope * (SeaShoreDistance - distance)));
        }

        // A smooth channel whose banks rise to the plateau over 3 m.
        private static float RiverFloor(float x, float z)
        {
            var distance = DistanceToRiver(new Vector2(x, z), out var along);
            if (distance >= RiverHalfWidth + RiverBankWidth)
            {
                return float.MaxValue;
            }

            var surface = RiverSurfaceAt(along);
            if (distance >= RiverHalfWidth)
            {
                return surface + ((WatersidePlateauHeight - surface) * (distance - RiverHalfWidth) / RiverBankWidth);
            }

            return surface - (RiverDepthAt(along) * (1f - Smooth01(distance / RiverHalfWidth)));
        }

        // Level with the lake until the river leaves it, then falling evenly to the sea.
        private static float RiverSurfaceAt(float along)
        {
            var length = RiverLength();
            var amount = Mathf.Clamp01((along - RiverFallStart) / (length - RiverFallStart));
            return LakeSurface + ((RiverEndSurface - LakeSurface) * amount);
        }

        private static float RiverFallStart
        {
            get
            {
                if (!riverFallStart.HasValue)
                {
                    riverFallStart = RiverDistanceWhere(point => Vector2.Distance(point, LakeCenter) >= LakeWaterRadius - 0.6f);
                }

                return riverFallStart.Value;
            }
        }

        private static float RiverDepthAt(float along)
        {
            var offset = Mathf.Abs(along - FordDistance) - FordHalfLength;
            return Mathf.Lerp(FordDepth, RiverDepth, Smooth01(offset / FordTransition));
        }

        private static float Smooth01(float value)
        {
            var clamped = Mathf.Clamp01(value);
            return clamped * clamped * (3f - (2f * clamped));
        }

        private static float RiverLength()
        {
            var length = 0f;
            for (var index = 1; index < RiverPoints.Length; index++)
            {
                length += Vector2.Distance(RiverPoints[index - 1], RiverPoints[index]);
            }

            return length;
        }

        // The distance to the river's center line and how far along it the closest point lies.
        private static float DistanceToRiver(Vector2 point, out float along)
        {
            var best = float.MaxValue;
            along = 0f;
            var start = 0f;
            for (var index = 1; index < RiverPoints.Length; index++)
            {
                var from = RiverPoints[index - 1];
                var segment = RiverPoints[index] - from;
                var length = segment.magnitude;
                var amount = Mathf.Clamp01(Vector2.Dot(point - from, segment) / (length * length));
                var distance = Vector2.Distance(point, from + (segment * amount));
                if (distance < best)
                {
                    best = distance;
                    along = start + (amount * length);
                }

                start += length;
            }

            return best;
        }

        private static Vector2 RiverPointAt(float along, out Vector2 direction)
        {
            var start = 0f;
            for (var index = 1; index < RiverPoints.Length; index++)
            {
                var segment = RiverPoints[index] - RiverPoints[index - 1];
                var length = segment.magnitude;
                direction = segment / length;
                if (along <= start + length || index == RiverPoints.Length - 1)
                {
                    return RiverPoints[index - 1] + (direction * (along - start));
                }

                start += length;
            }

            direction = Vector2.up;
            return RiverPoints[0];
        }

        private static Vector3 GroundPoint(Vector2 point)
        {
            return new Vector3(point.x, WatersideHeight(point.x, point.y, out _), point.y);
        }

        // The yaw that turns local +X along the direction.
        private static float Yaw(Vector2 direction)
        {
            return Mathf.Atan2(-direction.y, direction.x) * Mathf.Rad2Deg;
        }

        private static TerrainData CreateOrUpdateWatersideTerrainData()
        {
            var path = TerrainDirectory + "/TD_DevelopmentWorld2_Waterside.asset";
            var terrainData = AssetDatabase.LoadAssetAtPath<TerrainData>(path);
            if (terrainData == null)
            {
                terrainData = new TerrainData
                {
                    name = "TD_DevelopmentWorld2_Waterside"
                };
                AssetDatabase.CreateAsset(terrainData, path);
            }

            terrainData.heightmapResolution = WatersideHeightmapResolution;
            terrainData.alphamapResolution = WatersideAlphamapResolution;
            terrainData.size = WatersideSize;

            var heights = new float[WatersideHeightmapResolution, WatersideHeightmapResolution];
            var spacing = WatersideSize.x / (WatersideHeightmapResolution - 1);
            for (var z = 0; z < WatersideHeightmapResolution; z++)
            {
                for (var x = 0; x < WatersideHeightmapResolution; x++)
                {
                    heights[z, x] = WatersideHeight(
                        WatersideOrigin.x + (x * spacing),
                        WatersideOrigin.z + (z * spacing),
                        out _) / WatersideSize.y;
                }
            }

            terrainData.SetHeights(0, 0, heights);
            terrainData.terrainLayers = new[]
            {
                CreateOrUpdateTerrainLayer("TL_DevelopmentWorld2_Grass", new Color(0.36f, 0.47f, 0.33f)),
                CreateOrUpdateTerrainLayer("TL_DevelopmentWorld2_Rock", new Color(0.45f, 0.42f, 0.38f)),
                CreateOrUpdateTerrainLayer("TL_DevelopmentWorld2_Sand", new Color(0.76f, 0.69f, 0.50f))
            };
            terrainData.SetAlphamaps(0, 0, CreateWatersideAlphamaps(terrainData));
            EditorUtility.SetDirty(terrainData);
            return terrainData;
        }

        // Rock where the ground is too steep to climb, sand on the beds and banks of the
        // water, and grass elsewhere. Each cell takes the share of its samples that lie on a
        // water bed, so the sand's edge blends into the grass instead of stepping from cell to
        // cell.
        private static float[,,] CreateWatersideAlphamaps(TerrainData terrainData)
        {
            const int samplesPerSide = 4;
            var resolution = terrainData.alphamapResolution;
            var cell = WatersideSize.x / resolution;
            var alphamaps = new float[resolution, resolution, 3];
            for (var z = 0; z < resolution; z++)
            {
                for (var x = 0; x < resolution; x++)
                {
                    var steepness = terrainData.GetSteepness(
                        (x + 0.5f) / resolution,
                        (z + 0.5f) / resolution);
                    var rock = steepness >= RockSteepnessDegrees ? 1f : 0f;
                    var bedSamples = 0;
                    for (var sampleZ = 0; sampleZ < samplesPerSide; sampleZ++)
                    {
                        for (var sampleX = 0; sampleX < samplesPerSide; sampleX++)
                        {
                            WatersideHeight(
                                WatersideOrigin.x + ((x + ((sampleX + 0.5f) / samplesPerSide)) * cell),
                                WatersideOrigin.z + ((z + ((sampleZ + 0.5f) / samplesPerSide)) * cell),
                                out var waterBed);
                            bedSamples += waterBed ? 1 : 0;
                        }
                    }

                    var sand = (float)bedSamples / (samplesPerSide * samplesPerSide);
                    alphamaps[z, x, 0] = (1f - rock) * (1f - sand);
                    alphamaps[z, x, 1] = rock;
                    alphamaps[z, x, 2] = (1f - rock) * sand;
                }
            }

            return alphamaps;
        }

        private static void WaterBodyObject(
            Transform parent,
            string name,
            string bodyId,
            WaterBodyKind kind,
            Mesh surface)
        {
            var body = new GameObject(name);
            body.transform.SetParent(parent, false);
            body.AddComponent<MeshFilter>().sharedMesh = surface;
            ConfigureWaterRenderer(body.AddComponent<MeshRenderer>(), WaterAssets.LoadMaterial(kind));
            var serialized = new SerializedObject(body.AddComponent<WaterBody>());
            serialized.FindProperty("bodyId").stringValue = bodyId;
            serialized.FindProperty("kind").enumValueIndex = (int)kind;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void ConfigureWaterRenderer(MeshRenderer renderer, Material material)
        {
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }

        // A disc of rings 1 m apart that reaches under the bank all around the lake. Lakes do
        // not flow, the material's drift moves their ripples, but where the waterfall lands the
        // water churns and spreads out from the pool.
        private static Mesh BuildLakeSurface()
        {
            var surface = new WaterSurfaceMesh();
            const int segments = 48;
            var rings = Mathf.CeilToInt(LakeWaterRadius);
            AddLakeVertex(surface, LakeCenter);
            for (var ring = 1; ring <= rings; ring++)
            {
                var radius = Mathf.Min(ring, LakeWaterRadius);
                for (var segment = 0; segment < segments; segment++)
                {
                    var angle = segment * Mathf.PI * 2f / segments;
                    AddLakeVertex(surface, LakeCenter + (new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius));
                }
            }

            for (var segment = 0; segment < segments; segment++)
            {
                var next = (segment + 1) % segments;
                surface.Triangle(0, 1 + segment, 1 + next);
                for (var ring = 1; ring < rings; ring++)
                {
                    var inner = 1 + ((ring - 1) * segments);
                    var outer = inner + segments;
                    surface.Triangle(inner + segment, outer + segment, outer + next);
                    surface.Triangle(inner + segment, outer + next, inner + next);
                }
            }

            return surface.Save("M_DevelopmentWorld2_Lake");
        }

        // Foam churns within 1 m of where the waterfall lands and fades out 4 m away, and the
        // water spreads out from there.
        private static void AddLakeVertex(WaterSurfaceMesh surface, Vector2 point)
        {
            var fromPool = point - PlungePoolCenter;
            var distance = fromPool.magnitude;
            var churn = 1f - Smooth01((distance - 1f) / 3f);
            var spread = distance > 0.01f
                ? fromPool * (0.7f * churn * Smooth01(distance) / distance)
                : Vector2.zero;
            surface.Add(new Vector3(point.x, LakeSurface, point.y), spread, churn, 0f);
        }

        // The quarter disc of sea inside the World's corner, as a grid of 2 m cells whose
        // vertices the waves lift. The waves calm down toward the World's edge, where the flat
        // sea beyond the World meets this one, and the jagged rim lies under the beach.
        private static Mesh BuildSeaSurface()
        {
            var surface = new WaterSurfaceMesh();
            const float cell = 2f;
            var cells = Mathf.CeilToInt(SeaWaterRadius / cell);
            var indices = new int[cells + 1, cells + 1];
            for (var row = 0; row <= cells; row++)
            {
                for (var column = 0; column <= cells; column++)
                {
                    var x = SeaCorner.x + (column * cell);
                    var z = SeaCorner.y - (row * cell);
                    var waves = Mathf.Clamp01(Mathf.Min(x - SeaCorner.x, SeaCorner.y - z) / 12f);
                    indices[row, column] = surface.Add(new Vector3(x, SeaSurface, z), Vector2.zero, 0f, waves);
                }
            }

            for (var row = 0; row < cells; row++)
            {
                for (var column = 0; column < cells; column++)
                {
                    var centerX = (column + 0.5f) * cell;
                    var centerZ = (row + 0.5f) * cell;
                    if (Mathf.Sqrt((centerX * centerX) + (centerZ * centerZ)) > SeaWaterRadius + cell)
                    {
                        continue;
                    }

                    surface.Triangle(indices[row, column], indices[row + 1, column], indices[row + 1, column + 1]);
                    surface.Triangle(indices[row, column], indices[row + 1, column + 1], indices[row, column + 1]);
                }
            }

            return surface.Save("M_DevelopmentWorld2_Sea");
        }

        // The calm sea west and north of the World, out to the horizon.
        private static Mesh BuildOuterSeaSurface()
        {
            var surface = new WaterSurfaceMesh();
            surface.Quad(SeaCorner.x - OuterSeaExtent, SeaCorner.x, SeaCorner.y - OuterSeaExtent, SeaCorner.y + OuterSeaExtent, SeaSurface);
            surface.Quad(SeaCorner.x, SeaCorner.x + OuterSeaExtent, SeaCorner.y, SeaCorner.y + OuterSeaExtent, SeaSurface);
            return surface.Save("M_DevelopmentWorld2_SeaBeyondTheWorld");
        }

        // A ribbon along the river that reaches under both banks and falls with the river, five
        // vertices across, so the water runs fastest in the middle. It starts just inside the
        // lake's disc and ends just outside the sea's, so the surfaces barely overlap, and its
        // flow starts still at the lake, so the ripples meet the lake's without a seam.
        private static Mesh BuildRiverSurface()
        {
            var surface = new WaterSurfaceMesh();
            var start = RiverFallStart;
            var end = RiverDistanceWhere(point => Vector2.Distance(point, SeaCorner) <= SeaWaterRadius + 0.3f);
            var steps = Mathf.CeilToInt(end - start);
            const int columns = 5;
            for (var step = 0; step <= steps; step++)
            {
                var along = Mathf.Lerp(start, end, (float)step / steps);
                var point = RiverPointAt(along, out _);
                RiverPointAt(along - 0.5f, out var before);
                RiverPointAt(along + 0.5f, out var after);
                var direction = (before + after).normalized;
                var across = new Vector2(-direction.y, direction.x);
                var height = RiverSurfaceAt(along);
                for (var column = 0; column < columns; column++)
                {
                    var side = ((2f * column) / (columns - 1)) - 1f;
                    var offset = across * (side * RiverWaterHalfWidth);
                    var profile = 1f - (0.7f * side * side);
                    surface.Add(
                        new Vector3(point.x + offset.x, height, point.y + offset.y),
                        direction * (RiverFlowSpeedAt(along, start, end) * profile),
                        RiverTurbulenceAt(along) * profile,
                        0f);
                }
            }

            for (var step = 0; step < steps; step++)
            {
                for (var column = 0; column < columns - 1; column++)
                {
                    var first = (step * columns) + column;
                    surface.Triangle(first, first + 1, first + columns + 1);
                    surface.Triangle(first, first + columns + 1, first + columns);
                }
            }

            return surface.Save("M_DevelopmentWorld2_River");
        }

        // About 0.8 m/s in the deep river and 1.4 m/s over the shallow ford, still where the
        // river leaves the lake and slowing where it meets the sea.
        private static float RiverFlowSpeedAt(float along, float start, float end)
        {
            var fordAmount = 1f - Smooth01((Mathf.Abs(along - FordDistance) - FordHalfLength) / FordTransition);
            var speed = Mathf.Lerp(0.8f, 1.4f, fordAmount);
            var fromLake = Smooth01((along - start) / 6f);
            var toSea = Mathf.Lerp(0.4f, 1f, Smooth01((end - along) / 5f));
            return speed * fromLake * toSea;
        }

        // Foam over the ford, where the fast water runs shallow.
        private static float RiverTurbulenceAt(float along)
        {
            return 0.55f * (1f - Smooth01((Mathf.Abs(along - FordDistance) - FordHalfLength) / FordTransition));
        }

        // The first distance along the river, in 5 cm steps, whose point meets the condition.
        private static float RiverDistanceWhere(System.Func<Vector2, bool> condition)
        {
            var length = RiverLength();
            for (var along = 0f; along < length; along += 0.05f)
            {
                if (condition(RiverPointAt(along, out _)))
                {
                    return along;
                }
            }

            return length;
        }

        // The vertices of a water surface with what the water shader reads from them: the flow
        // in m/s in the second texture coordinate, and in the vertex color red the fast water
        // that foams and green how much the waves lift the vertex.
        private sealed class WaterSurfaceMesh
        {
            private readonly List<Vector3> vertices = new List<Vector3>();
            private readonly List<Vector2> flows = new List<Vector2>();
            private readonly List<Color> colors = new List<Color>();
            private readonly List<int> triangles = new List<int>();

            public int Add(Vector3 position, Vector2 flow, float turbulence, float waves)
            {
                vertices.Add(position);
                flows.Add(flow);
                colors.Add(new Color(turbulence, waves, 0f, 1f));
                return vertices.Count - 1;
            }

            // Unity's front faces run clockwise seen from their front, so a triangle faces up
            // when its edge cross product points up.
            public void Triangle(int a, int b, int c)
            {
                var up = Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]).y >= 0f;
                triangles.Add(a);
                triangles.Add(up ? b : c);
                triangles.Add(up ? c : b);
            }

            public void Quad(float minimumX, float maximumX, float minimumZ, float maximumZ, float height)
            {
                var first = Add(new Vector3(minimumX, height, minimumZ), Vector2.zero, 0f, 0f);
                Add(new Vector3(minimumX, height, maximumZ), Vector2.zero, 0f, 0f);
                Add(new Vector3(maximumX, height, maximumZ), Vector2.zero, 0f, 0f);
                Add(new Vector3(maximumX, height, minimumZ), Vector2.zero, 0f, 0f);
                Triangle(first, first + 1, first + 2);
                Triangle(first, first + 2, first + 3);
            }

            public Mesh Save(string name)
            {
                return SaveMesh(name, mesh =>
                {
                    mesh.SetVertices(vertices);
                    mesh.SetUVs(1, flows);
                    mesh.SetColors(colors);
                    mesh.SetTriangles(triangles, 0);
                });
            }
        }

        // A mesh asset next to the terrain data, filled anew on every build, with normals from
        // its triangles unless the fill gives its own.
        private static Mesh SaveMesh(string name, System.Action<Mesh> fill)
        {
            var path = TerrainDirectory + "/" + name + ".asset";
            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (mesh == null)
            {
                mesh = new Mesh
                {
                    name = name
                };
                AssetDatabase.CreateAsset(mesh, path);
            }

            mesh.Clear();
            fill(mesh);
            if (!mesh.HasVertexAttribute(VertexAttribute.Normal))
            {
                mesh.RecalculateNormals();
            }

            mesh.RecalculateBounds();
            EditorUtility.SetDirty(mesh);
            return mesh;
        }

        // The outcrop's rocks with a ramp onto them from behind, the stream along their top, the
        // falling sheet, and the mist and droplets where the water lands.
        private static void BuildWaterfall(DevelopmentWorldBuilder builder, Transform waterside, Transform water)
        {
            var palette = builder.Palette;
            var lip = WaterfallLip;

            // Local +Z points from the outcrop toward the lake and local +X across it, with the
            // lip at the origin.
            var outcrop = builder.Group(waterside, "Waterfall", new Vector3(lip.x, 0f, lip.y), Yaw(OutcropAcross));
            var rockWidth = OutcropHalfWidth - StreamHalfWidth;
            var rockHeight = OutcropTop - OutcropBottom;
            foreach (var side in new[] { -1f, 1f })
            {
                builder.Box(
                    outcrop,
                    side < 0f ? "LeftRock" : "RightRock",
                    new Vector3(side * (StreamHalfWidth + (rockWidth * 0.5f)), OutcropBottom + (rockHeight * 0.5f), -OutcropLength * 0.5f),
                    new Vector3(rockWidth, rockHeight, OutcropLength),
                    palette.Rock);
            }

            var bedLength = OutcropLength - SpringLength;
            builder.Box(
                outcrop,
                "StreamBed",
                new Vector3(0f, (OutcropBottom + StreamBedTop) * 0.5f, -bedLength * 0.5f),
                new Vector3(StreamHalfWidth * 2f, StreamBedTop - OutcropBottom, bedLength),
                palette.Rock);
            var boulderTop = OutcropTop + 0.6f;
            builder.Box(
                outcrop,
                "SpringBoulder",
                new Vector3(0f, (OutcropBottom + boulderTop) * 0.5f, -OutcropLength + (SpringLength * 0.5f)),
                new Vector3(StreamHalfWidth * 2f, boulderTop - OutcropBottom, SpringLength),
                palette.Rock);
            var path = builder.Group(outcrop, "Path", new Vector3(0f, WatersidePlateauHeight, 0f));
            builder.Ramp(
                path,
                "Ramp",
                StreamHalfWidth + (rockWidth * 0.5f),
                rockWidth,
                -OutcropLength,
                OutcropTop - WatersidePlateauHeight,
                25f,
                palette.Traversal);

            WaterBodyObject(water, "Stream", StreamBodyId, WaterBodyKind.River, BuildStreamSurface());
            var sheet = new GameObject("WaterfallSheet");
            sheet.transform.SetParent(water, false);
            sheet.AddComponent<MeshFilter>().sharedMesh = BuildWaterfallSheet();
            ConfigureWaterRenderer(sheet.AddComponent<MeshRenderer>(), WaterAssets.LoadMaterial(WaterAssets.WaterfallMaterialPath));
            BuildWaterfallSpray(water, new Vector3(PlungePoolCenter.x, LakeSurface, PlungePoolCenter.y));

            builder.Sign(
                waterside,
                "WaterfallSign",
                GroundPoint(lip + (OutcropDirection * (OutcropLength + 6f)) - (OutcropAcross * 5f)),
                Yaw(OutcropAcross),
                "WATERFALL\nWade into the pool,\nramp up to the stream");
        }

        // The stream from inside the spring's boulder to just past the lip, five vertices across
        // that reach under both rock walls. It runs faster toward the lip, bubbles at the
        // spring, and foams where it tips over.
        private static Mesh BuildStreamSurface()
        {
            var surface = new WaterSurfaceMesh();
            const int columns = 5;
            var length = OutcropLength - SpringLength + 0.1f;
            var steps = Mathf.CeilToInt(length / 0.5f);
            var downstream = -OutcropDirection;
            for (var step = 0; step <= steps; step++)
            {
                // How far back from the lip, from 5 cm past it to inside the boulder.
                var back = Mathf.Lerp(-0.05f, length, (float)step / steps);
                var speed = Mathf.Lerp(1.8f, 0.5f, Smooth01(back / length));
                var turbulence = Mathf.Max(
                    0.6f * (1f - Smooth01((length - back) / 1.2f)),
                    0.8f * (1f - Smooth01(back / 1.5f)));
                var center = WaterfallLip + (OutcropDirection * back);
                for (var column = 0; column < columns; column++)
                {
                    var side = ((2f * column) / (columns - 1)) - 1f;
                    var point = center + (OutcropAcross * (side * (StreamHalfWidth + 0.1f)));
                    var profile = 1f - (0.5f * side * side);
                    surface.Add(new Vector3(point.x, StreamSurface, point.y), downstream * (speed * profile), turbulence * profile, 0f);
                }
            }

            for (var step = 0; step < steps; step++)
            {
                for (var column = 0; column < columns - 1; column++)
                {
                    var first = (step * columns) + column;
                    surface.Triangle(first, first + 1, first + columns + 1);
                    surface.Triangle(first, first + columns + 1, first + columns);
                }
            }

            return surface.Save("M_DevelopmentWorld2_Stream");
        }

        // The falling sheet from just behind the lip down to the lake's surface, along the path
        // the water takes when it leaves the lip at the waterfall's speed, widening a little as
        // it falls. The first texture coordinate holds meters across the sheet and the seconds
        // the water has fallen, the second meters to the nearer side and how far down the fall
        // the vertex lies. The normals face the lake and the tangents run across.
        private static Mesh BuildWaterfallSheet()
        {
            const int rows = 20;
            const int columns = 9;
            var top = StreamSurface - 0.02f;
            var seconds = Mathf.Sqrt(2f * (top - LakeSurface) / Gravity);
            var across3 = new Vector3(OutcropAcross.x, 0f, OutcropAcross.y);
            var towardLake = new Vector3(-OutcropDirection.x, 0f, -OutcropDirection.y);
            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var tangents = new List<Vector4>();
            var sheet = new List<Vector2>();
            var edges = new List<Vector2>();
            var triangles = new List<int>();
            for (var row = 0; row <= rows; row++)
            {
                var fall = (float)row / rows;
                var time = seconds * fall;
                var flat = WaterfallLip - (OutcropDirection * ((WaterfallSpeed * time) - 0.05f));
                var center = new Vector3(flat.x, top - (0.5f * Gravity * time * time), flat.y);

                // The water moves along the fall; the sheet's normal is square to that and across.
                var velocity = (towardLake * WaterfallSpeed) + (Vector3.down * (Gravity * time));
                var normal = Vector3.Cross(across3, velocity).normalized;
                if (Vector3.Dot(normal, towardLake) < 0f)
                {
                    normal = -normal;
                }

                var halfWidth = Mathf.Lerp(StreamHalfWidth - 0.05f, StreamHalfWidth + 0.3f, fall);
                for (var column = 0; column < columns; column++)
                {
                    var across = (((2f * column) / (columns - 1)) - 1f) * halfWidth;
                    vertices.Add(center + (across3 * across));
                    normals.Add(normal);
                    tangents.Add(new Vector4(across3.x, across3.y, across3.z, 1f));
                    sheet.Add(new Vector2(across, time));
                    edges.Add(new Vector2(halfWidth - Mathf.Abs(across), fall));
                }
            }

            for (var row = 0; row < rows; row++)
            {
                for (var column = 0; column < columns - 1; column++)
                {
                    var first = (row * columns) + column;
                    triangles.Add(first);
                    triangles.Add(first + columns);
                    triangles.Add(first + columns + 1);
                    triangles.Add(first);
                    triangles.Add(first + columns + 1);
                    triangles.Add(first + 1);
                }
            }

            return SaveMesh("M_DevelopmentWorld2_WaterfallSheet", mesh =>
            {
                mesh.SetVertices(vertices);
                mesh.SetNormals(normals);
                mesh.SetTangents(tangents);
                mesh.SetUVs(0, sheet);
                mesh.SetUVs(1, edges);
                mesh.SetTriangles(triangles, 0);
            });
        }

        // Where the waterfall lands, white water boils up along the sheet, mist rises and drifts,
        // and droplets fly.
        private static void BuildWaterfallSpray(Transform parent, Vector3 foot)
        {
            var material = WaterAssets.LoadMaterial(WaterAssets.SprayMaterialPath);
            var boil = WaterfallParticles(parent, "WaterfallBoil", foot, material, 90, 30f, 0.75f);
            var boilMain = boil.main;
            boilMain.startLifetime = new ParticleSystem.MinMaxCurve(0.5f, 0.9f);
            boilMain.startSpeed = new ParticleSystem.MinMaxCurve(0.5f, 1.3f);
            boilMain.startSize = new ParticleSystem.MinMaxCurve(0.45f, 1f);
            boilMain.gravityModifier = 0.4f;
            var boilShape = boil.shape;
            boilShape.shapeType = ParticleSystemShapeType.Box;
            boilShape.scale = new Vector3((StreamHalfWidth + 0.3f) * 2f, 0.3f, 0.05f);
            var boilSize = boil.sizeOverLifetime;
            boilSize.enabled = true;
            boilSize.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0.7f, 1f, 1.3f));

            var mist = WaterfallParticles(parent, "WaterfallMist", foot, material, 100, 18f, 0.2f);
            var mistMain = mist.main;
            mistMain.startLifetime = new ParticleSystem.MinMaxCurve(1.4f, 2.2f);
            mistMain.startSpeed = new ParticleSystem.MinMaxCurve(0.3f, 0.8f);
            mistMain.startSize = new ParticleSystem.MinMaxCurve(0.8f, 1.6f);
            mistMain.gravityModifier = -0.01f;
            var mistShape = mist.shape;
            mistShape.shapeType = ParticleSystemShapeType.Hemisphere;
            mistShape.radius = StreamHalfWidth;
            var mistSize = mist.sizeOverLifetime;
            mistSize.enabled = true;
            mistSize.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0.6f, 1f, 1.5f));

            var splash = WaterfallParticles(parent, "WaterfallSplash", foot, material, 200, 60f, 0.85f);
            var splashMain = splash.main;
            splashMain.startLifetime = new ParticleSystem.MinMaxCurve(0.5f, 0.9f);
            splashMain.startSpeed = new ParticleSystem.MinMaxCurve(2f, 3.6f);
            splashMain.startSize = new ParticleSystem.MinMaxCurve(0.06f, 0.16f);
            splashMain.gravityModifier = 1f;
            var splashShape = splash.shape;
            splashShape.shapeType = ParticleSystemShapeType.Cone;
            splashShape.angle = 30f;
            splashShape.radius = StreamHalfWidth;
        }

        // A looping emitter of soft white particles that fade in and out, whose shape points
        // straight up, with its width across the sheet.
        private static ParticleSystem WaterfallParticles(
            Transform parent,
            string name,
            Vector3 position,
            Material material,
            int maximumParticles,
            float rate,
            float alpha)
        {
            var host = new GameObject(name);
            host.transform.SetParent(parent, false);
            host.transform.position = position;
            host.transform.rotation = Quaternion.LookRotation(Vector3.up, new Vector3(-OutcropDirection.x, 0f, -OutcropDirection.y));
            var system = host.AddComponent<ParticleSystem>();
            var main = system.main;
            main.loop = true;
            main.prewarm = true;
            main.playOnAwake = true;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = maximumParticles;
            main.startColor = Color.white;
            var emission = system.emission;
            emission.rateOverTime = rate;
            var gradient = new Gradient();
            gradient.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(alpha, 0.2f), new GradientAlphaKey(0f, 1f) });
            var color = system.colorOverLifetime;
            color.enabled = true;
            color.color = new ParticleSystem.MinMaxGradient(gradient);
            var renderer = host.GetComponent<ParticleSystemRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return system;
        }

        // A deck 1 m above the river's deep middle, from plateau to plateau, which the
        // deep water edges pass under.
        private static void BuildWatersideBridge(DevelopmentWorldBuilder builder, Transform waterside)
        {
            var bridge = builder.Group(waterside, "Bridge");
            var length = ((RiverHalfWidth + RiverBankWidth) * 2f) + 2f;
            builder.Box(
                bridge,
                "Deck",
                new Vector3(BridgeCenter.x, BridgeDeckTop - 0.2f, BridgeCenter.y),
                new Vector3(length, 0.4f, 4f),
                builder.Palette.Traversal,
                true,
                Yaw(BridgeAcross));
        }
    }
}
