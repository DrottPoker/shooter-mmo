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

        // The sea's shore lies 60 m from the corner; the floor falls 0.15 m per meter. Its surface
        // reaches 4 m under the beach, and the river ends 2.3 m beyond the shore.
        private const float SeaShoreDistance = 60f;
        private const float SeaBeachWidth = 10f;
        private const float SeaFloorSlope = 0.15f;
        private const float SeaWaterRadius = 62f;
        private const float SeaSurfaceReach = 4f;
        private const float OuterSeaExtent = 400f;

        // The river is 8 m wide, 2 m deep, and 0.8 m deep along the ford.
        private const float RiverHalfWidth = 4f;
        private const float RiverBankWidth = 3f;
        private const float RiverDepth = 2f;
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
        private const float SpringBubbleLength = 1.2f;
        private const float WaterfallSpeed = 1.6f;
        private const float PlungePoolRadius = 3.2f;
        private const float PlungePoolDepth = 1f;
        private const float Gravity = 9.81f;

        // The stream's surface reaches this far under the channel's walls and past the lip, where
        // it keeps this far inside the channel; the waterfall's column fades in over the stream
        // along this much of it before the lip.
        private const float StreamUnderWalls = 0.1f;
        private const float StreamPastLip = 0.05f;
        private const float StreamInsideFall = 0.02f;
        private const float WaterfallApproachLength = 0.6f;

        private static float? riverFallStart;
        private static float? riverMouth;

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

            // The water is World Builder shapes, which make every surface, the sea beyond the
            // World, and the waterfall from a few values each, once all of them stand.
            var water = builder.Group(waterside, "Water");
            WaterAssets.CreateOrUpdate();
            var lake = BuildLake(water);
            BuildRiver(water);
            BuildSea(water, palette);

            BuildWatersideBridge(builder, waterside);
            BuildWaterfall(builder, waterside, water, lake);
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
            WorldBuilder.Rebuild(builder.Scene);
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

        // Level with the lake until the river leaves it, then falling evenly to the sea's level,
        // which it reaches at its mouth, where its surface ends inside the sea's.
        private static float RiverSurfaceAt(float along)
        {
            var amount = Mathf.Clamp01((along - RiverFallStart) / (RiverMouth - RiverFallStart));
            return LakeSurface + ((SeaSurface - LakeSurface) * amount);
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

        private static float RiverMouth
        {
            get
            {
                if (!riverMouth.HasValue)
                {
                    riverMouth = RiverDistanceWhere(point => Vector2.Distance(point, SeaCorner) <= SeaWaterRadius + 0.3f);
                }

                return riverMouth.Value;
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

        // The lake's shore is the circle where its floor meets its surface, and the surface reaches
        // under the bank to the water radius.
        private static LakeShape BuildLake(Transform water)
        {
            var lake = WaterShape<LakeShape>(
                water,
                "Lake",
                LakeBodyId,
                WaterBodyKind.Lake,
                "M_DevelopmentWorld2_Lake",
                new Vector3(LakeCenter.x, LakeSurface, LakeCenter.y));
            const int segments = 48;
            var serialized = new SerializedObject(lake);
            var shore = serialized.FindProperty("shore");
            shore.arraySize = segments;
            for (var segment = 0; segment < segments; segment++)
            {
                var angle = segment * Mathf.PI * 2f / segments;
                shore.GetArrayElementAtIndex(segment).vector2Value =
                    new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * LakeShoreRadius;
            }

            serialized.FindProperty("cellSize").floatValue = 1f;
            serialized.FindProperty("bankReach").floatValue = LakeWaterRadius - LakeShoreRadius;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return lake;
        }

        // The river from just inside the lake to just inside the sea along its center line, 8 m
        // wide, with a point wherever the line turns and where the ford begins and ends, over
        // which it runs faster, shallower, and foaming.
        private static void BuildRiver(Transform water)
        {
            var river = WaterShape<RiverShape>(
                water,
                "River",
                RiverBodyId,
                WaterBodyKind.River,
                "M_DevelopmentWorld2_River",
                Vector3.zero);
            var distances = new List<float> { RiverFallStart, RiverMouth };
            var corner = 0f;
            for (var index = 1; index < RiverPoints.Length - 1; index++)
            {
                corner += Vector2.Distance(RiverPoints[index - 1], RiverPoints[index]);
                distances.Add(corner);
            }

            foreach (var offset in new[] { -FordHalfLength - FordTransition, -FordHalfLength, FordHalfLength, FordHalfLength + FordTransition })
            {
                distances.Add(FordDistance + offset);
            }

            distances.RemoveAll(along => along < RiverFallStart || along > RiverMouth);
            distances.Sort();
            var points = new List<RiverPoint>();
            foreach (var along in distances)
            {
                var point = RiverPointAt(along, out _);
                points.Add(new RiverPoint(
                    new Vector3(point.x, RiverSurfaceAt(along), point.y),
                    RiverHalfWidth * 2f,
                    RiverDepthAt(along),
                    Mathf.Lerp(0.8f, 1.4f, FordAmount(along)),
                    0.55f * FordAmount(along)));
            }

            SetRiver(river, points, 1f, 0.7f, RiverWaterHalfWidth - RiverHalfWidth);
        }

        // The quarter disc of sea inside the World's corner: its shore lies 60 m from the corner,
        // and its surface reaches 4 m under the beach. It runs on beyond the World to the west and
        // the north over a sand seabed at the World's ground.
        private static void BuildSea(Transform water, DevelopmentWorldPalette palette)
        {
            var sea = WaterShape<SeaShape>(
                water,
                "Sea",
                SeaBodyId,
                WaterBodyKind.Sea,
                "M_DevelopmentWorld2_Sea",
                new Vector3(SeaCorner.x, SeaSurface, SeaCorner.y));
            const int segments = 24;
            var serialized = new SerializedObject(sea);
            var outline = serialized.FindProperty("outline");
            outline.arraySize = segments + 2;
            outline.GetArrayElementAtIndex(0).vector2Value = Vector2.zero;
            for (var segment = 0; segment <= segments; segment++)
            {
                var angle = -segment * Mathf.PI * 0.5f / segments;
                outline.GetArrayElementAtIndex(segment + 1).vector2Value =
                    new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * SeaShoreDistance;
            }

            serialized.FindProperty("cellSize").floatValue = 2f;
            serialized.FindProperty("bankReach").floatValue = SeaSurfaceReach;
            serialized.FindProperty("waves").floatValue = 1f;
            serialized.FindProperty("calmDistance").floatValue = 12f;
            serialized.FindProperty("horizon").intValue = (int)(HorizonSides.West | HorizonSides.North);
            serialized.FindProperty("horizonExtent").floatValue = OuterSeaExtent;
            serialized.FindProperty("seabedMaterial").objectReferenceValue = palette.Sand;
            serialized.FindProperty("seabedDepth").floatValue = SeaSurface;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        // 0 away from the ford, rising to 1 over its transition, where the river runs shallow.
        private static float FordAmount(float along)
        {
            return 1f - Smooth01((Mathf.Abs(along - FordDistance) - FordHalfLength) / FordTransition);
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

        // A water body with its shape, in the shared water material, without shadows, keeping its
        // surface mesh asset next to the terrain data.
        private static T WaterShape<T>(
            Transform parent,
            string name,
            string bodyId,
            WaterBodyKind kind,
            string meshName,
            Vector3 position)
            where T : WaterSurfaceShape
        {
            var body = new GameObject(name);
            body.transform.SetParent(parent, false);
            body.transform.position = position;
            body.AddComponent<MeshFilter>().sharedMesh = MeshAsset(meshName);
            WaterRenderer(body, WaterAssets.LoadMaterial(WaterAssets.SurfaceMaterialPath));
            var serialized = new SerializedObject(body.AddComponent<WaterBody>());
            serialized.FindProperty("bodyId").stringValue = bodyId;
            serialized.FindProperty("kind").enumValueIndex = (int)kind;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return body.AddComponent<T>();
        }

        private static void SetRiver(RiverShape river, IReadOnlyList<RiverPoint> points, float rowSpacing, float edgeSlowdown, float bankReach)
        {
            var serialized = new SerializedObject(river);
            var list = serialized.FindProperty("points");
            list.arraySize = points.Count;
            for (var index = 0; index < points.Count; index++)
            {
                var element = list.GetArrayElementAtIndex(index);
                element.FindPropertyRelative("Position").vector3Value = points[index].Position;
                element.FindPropertyRelative("Width").floatValue = points[index].Width;
                element.FindPropertyRelative("Depth").floatValue = points[index].Depth;
                element.FindPropertyRelative("Speed").floatValue = points[index].Speed;
                element.FindPropertyRelative("Foam").floatValue = points[index].Foam;
            }

            serialized.FindProperty("curved").boolValue = false;
            serialized.FindProperty("rowSpacing").floatValue = rowSpacing;
            serialized.FindProperty("edgeSlowdown").floatValue = edgeSlowdown;
            serialized.FindProperty("bankReach").floatValue = bankReach;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static MeshRenderer WaterRenderer(GameObject owner, Material material)
        {
            var renderer = owner.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return renderer;
        }

        // A mesh asset next to the terrain data, which the World Builder fills.
        private static Mesh MeshAsset(string name)
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

            return mesh;
        }

        // The outcrop's rocks with a ramp onto them from behind, the stream along their top, and
        // the waterfall that falls from it into the lake.
        private static void BuildWaterfall(DevelopmentWorldBuilder builder, Transform waterside, Transform water, LakeShape lake)
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

            var stream = BuildStream(water);
            var sheet = new GameObject("WaterfallSheet");
            sheet.transform.SetParent(water, false);
            sheet.AddComponent<MeshFilter>().sharedMesh = MeshAsset("M_DevelopmentWorld2_WaterfallSheet");
            WaterRenderer(sheet, WaterAssets.LoadMaterial(WaterAssets.WaterfallMaterialPath));
            var waterfall = new SerializedObject(sheet.AddComponent<WaterfallShape>());
            waterfall.FindProperty("river").objectReferenceValue = stream;
            waterfall.FindProperty("landing").objectReferenceValue = lake.Body;
            waterfall.FindProperty("speed").floatValue = WaterfallSpeed;
            waterfall.FindProperty("approach").floatValue = WaterfallApproachLength;
            waterfall.FindProperty("pastLip").floatValue = StreamPastLip;
            waterfall.FindProperty("insideFall").floatValue = StreamInsideFall;
            waterfall.ApplyModifiedPropertiesWithoutUndo();

            builder.Sign(
                waterside,
                "WaterfallSign",
                GroundPoint(lip + (OutcropDirection * (OutcropLength + 6f)) - (OutcropAcross * 5f)),
                Yaw(OutcropAcross),
                "WATERFALL\nWade into the pool,\nramp up to the stream");
        }

        // The stream from inside the spring's boulder to the lip, 0.3 m deep. It bubbles at the
        // spring, runs faster toward the lip, and reaches 0.1 m under both rock walls.
        private static RiverShape BuildStream(Transform water)
        {
            var stream = WaterShape<RiverShape>(
                water,
                "Stream",
                StreamBodyId,
                WaterBodyKind.River,
                "M_DevelopmentWorld2_Stream",
                Vector3.zero);
            var length = OutcropLength - SpringLength + 0.1f;
            var points = new List<RiverPoint>();
            foreach (var back in new[] { length, length - SpringBubbleLength, 0f })
            {
                var center = WaterfallLip + (OutcropDirection * back);
                points.Add(new RiverPoint(
                    new Vector3(center.x, StreamSurface, center.y),
                    StreamHalfWidth * 2f,
                    StreamSurface - StreamBedTop,
                    Mathf.Lerp(1.8f, 0.5f, Smooth01(back / length)),
                    back >= length ? 0.6f : 0f));
            }

            SetRiver(stream, points, 0.5f, 0.5f, StreamUnderWalls);
            return stream;
        }

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
