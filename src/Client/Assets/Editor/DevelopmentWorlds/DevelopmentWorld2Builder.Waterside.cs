using System.Collections.Generic;
using Mmorpg.Client.Worlds;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Mmorpg.Client.Editor
{
    // The northwest waterside, the water test area: a plateau with a lake, a river from the
    // lake to the sea with a ford and a bridge, and the sea in the World's northwest corner,
    // where deep water instead of the boundary walls ends the World. Every height is a pure
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
        private const int WatersideAlphamapResolution = 128;
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

        private static void BuildWaterside(DevelopmentWorldBuilder builder)
        {
            var waterside = builder.Group(builder.Environment, "Waterside");
            var palette = builder.Palette;
            var terrainObject = Terrain.CreateTerrainGameObject(CreateOrUpdateWatersideTerrainData());
            terrainObject.name = "WatersideTerrain";
            terrainObject.transform.SetParent(waterside, false);
            terrainObject.transform.localPosition = WatersideOrigin;
            if (GraphicsSettings.currentRenderPipeline != null)
            {
                terrainObject.GetComponent<Terrain>().materialTemplate =
                    GraphicsSettings.currentRenderPipeline.defaultTerrainMaterial;
            }

            var water = builder.Group(waterside, "Water");
            WaterBodyObject(water, "Lake", LakeBodyId, WaterBodyKind.Lake, BuildLakeSurface(), palette.Water);
            WaterBodyObject(water, "River", RiverBodyId, WaterBodyKind.River, BuildRiverSurface(), palette.Water);
            WaterBodyObject(water, "Sea", SeaBodyId, WaterBodyKind.Sea, BuildSeaSurface(), palette.Water);

            // The sea beyond the World only shows; nothing reaches it, so it needs no collision.
            var beyond = new GameObject("SeaBeyondTheWorld");
            beyond.transform.SetParent(water, false);
            beyond.AddComponent<MeshFilter>().sharedMesh = BuildOuterSeaSurface();
            ConfigureWaterRenderer(beyond.AddComponent<MeshRenderer>(), palette.Water);
            builder.Box(water, "WestSeabed", new Vector3(SeaCorner.x - (OuterSeaExtent * 0.5f), -0.25f, SeaCorner.y),
                new Vector3(OuterSeaExtent, 0.5f, OuterSeaExtent * 2f), palette.Sand, false);
            builder.Box(water, "NorthSeabed", new Vector3(SeaCorner.x + (OuterSeaExtent * 0.5f), -0.25f, SeaCorner.y + (OuterSeaExtent * 0.5f)),
                new Vector3(OuterSeaExtent, 0.5f, OuterSeaExtent), palette.Sand, false);

            BuildWatersideBridge(builder, waterside);
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

        private static float LakeFloor(float x, float z)
        {
            var radius = Vector2.Distance(new Vector2(x, z), LakeCenter);
            if (radius >= LakeShoreRadius + LakeBankWidth)
            {
                return float.MaxValue;
            }

            if (radius >= LakeShoreRadius)
            {
                return LakeSurface + ((WatersidePlateauHeight - LakeSurface) * (radius - LakeShoreRadius) / LakeBankWidth);
            }

            return Mathf.Max(LakeSurface - LakeMaximumDepth, LakeSurface - (LakeFloorSlope * (LakeShoreRadius - radius)));
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
        // water, and grass elsewhere.
        private static float[,,] CreateWatersideAlphamaps(TerrainData terrainData)
        {
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
                    WatersideHeight(
                        WatersideOrigin.x + ((x + 0.5f) * cell),
                        WatersideOrigin.z + ((z + 0.5f) * cell),
                        out var waterBed);
                    var layer = steepness >= RockSteepnessDegrees ? 1 : waterBed ? 2 : 0;
                    alphamaps[z, x, layer] = 1f;
                }
            }

            return alphamaps;
        }

        private static void WaterBodyObject(
            Transform parent,
            string name,
            string bodyId,
            WaterBodyKind kind,
            Mesh surface,
            Material material)
        {
            var body = new GameObject(name);
            body.transform.SetParent(parent, false);
            body.AddComponent<MeshFilter>().sharedMesh = surface;
            ConfigureWaterRenderer(body.AddComponent<MeshRenderer>(), material);
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

        // A disc that reaches under the bank all around the lake.
        private static Mesh BuildLakeSurface()
        {
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            AddFan(vertices, triangles, LakeCenter, LakeSurface, LakeWaterRadius, 0f, 360f, 48);
            return SaveMesh("M_DevelopmentWorld2_Lake", vertices, triangles);
        }

        // The quarter disc of sea inside the World's corner.
        private static Mesh BuildSeaSurface()
        {
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            AddFan(vertices, triangles, SeaCorner, SeaSurface, SeaWaterRadius, -90f, 0f, 24);
            return SaveMesh("M_DevelopmentWorld2_Sea", vertices, triangles);
        }

        // The sea west and north of the World, out to the horizon.
        private static Mesh BuildOuterSeaSurface()
        {
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            AddQuad(vertices, triangles, SeaCorner.x - OuterSeaExtent, SeaCorner.x, SeaCorner.y - OuterSeaExtent, SeaCorner.y + OuterSeaExtent);
            AddQuad(vertices, triangles, SeaCorner.x, SeaCorner.x + OuterSeaExtent, SeaCorner.y, SeaCorner.y + OuterSeaExtent);
            return SaveMesh("M_DevelopmentWorld2_SeaBeyondTheWorld", vertices, triangles);
        }

        // A ribbon along the river that reaches under both banks and falls with the river.
        // It starts just inside the lake's disc and ends just outside the sea's, so the
        // surfaces barely overlap.
        private static Mesh BuildRiverSurface()
        {
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            var start = RiverFallStart;
            var end = RiverDistanceWhere(point => Vector2.Distance(point, SeaCorner) <= SeaWaterRadius + 0.3f);
            var steps = Mathf.CeilToInt(end - start);
            for (var step = 0; step <= steps; step++)
            {
                var along = Mathf.Lerp(start, end, (float)step / steps);
                var point = RiverPointAt(along, out _);
                RiverPointAt(along - 0.5f, out var before);
                RiverPointAt(along + 0.5f, out var after);
                var direction = (before + after).normalized;
                var across = new Vector2(-direction.y, direction.x) * RiverWaterHalfWidth;
                var height = RiverSurfaceAt(along);
                vertices.Add(new Vector3(point.x + across.x, height, point.y + across.y));
                vertices.Add(new Vector3(point.x - across.x, height, point.y - across.y));
            }

            for (var first = 0; first + 3 < vertices.Count; first += 2)
            {
                AddTriangleFacingUp(vertices, triangles, first, first + 1, first + 2);
                AddTriangleFacingUp(vertices, triangles, first + 1, first + 3, first + 2);
            }

            return SaveMesh("M_DevelopmentWorld2_River", vertices, triangles);
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

        private static void AddFan(
            List<Vector3> vertices,
            List<int> triangles,
            Vector2 center,
            float height,
            float radius,
            float startDegrees,
            float endDegrees,
            int segments)
        {
            var centerIndex = vertices.Count;
            vertices.Add(new Vector3(center.x, height, center.y));
            for (var segment = 0; segment <= segments; segment++)
            {
                var angle = Mathf.Lerp(startDegrees, endDegrees, (float)segment / segments) * Mathf.Deg2Rad;
                vertices.Add(new Vector3(center.x + (radius * Mathf.Cos(angle)), height, center.y + (radius * Mathf.Sin(angle))));
            }

            for (var segment = 0; segment < segments; segment++)
            {
                AddTriangleFacingUp(vertices, triangles, centerIndex, centerIndex + 1 + segment, centerIndex + 2 + segment);
            }
        }

        private static void AddQuad(List<Vector3> vertices, List<int> triangles, float minimumX, float maximumX, float minimumZ, float maximumZ)
        {
            var first = vertices.Count;
            vertices.Add(new Vector3(minimumX, SeaSurface, minimumZ));
            vertices.Add(new Vector3(minimumX, SeaSurface, maximumZ));
            vertices.Add(new Vector3(maximumX, SeaSurface, maximumZ));
            vertices.Add(new Vector3(maximumX, SeaSurface, minimumZ));
            AddTriangleFacingUp(vertices, triangles, first, first + 1, first + 2);
            AddTriangleFacingUp(vertices, triangles, first, first + 2, first + 3);
        }

        // Unity's front faces run clockwise seen from their front, so a triangle faces up
        // when its edge cross product points up.
        private static void AddTriangleFacingUp(List<Vector3> vertices, List<int> triangles, int a, int b, int c)
        {
            var up = Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]).y >= 0f;
            triangles.Add(a);
            triangles.Add(up ? b : c);
            triangles.Add(up ? c : b);
        }

        private static Mesh SaveMesh(string name, List<Vector3> vertices, List<int> triangles)
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
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            EditorUtility.SetDirty(mesh);
            return mesh;
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
