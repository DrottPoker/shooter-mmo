using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Mmorpg.Client.Editor
{
    // The northeast highlands test terrain: rolling hills, a peak that only its east face
    // climbs, and an arched tunnel through the foot of the peak. Heights are a pure
    // function of the local position, so every rebuild produces the same terrain.
    public static partial class DevelopmentWorld2EnvironmentBuilder
    {
        private const string TerrainDirectory = "Assets/Art/Terrain/DevelopmentWorld2";
        private const int HighlandsHeightmapResolution = 129;
        private const int HighlandsAlphamapResolution = 128;
        private const float HighlandsBaseHeight = 0.02f;
        private const float RockSteepnessDegrees = 40f;

        // The tunnel runs along +Z through the peak, in highlands local meters.
        private const float TunnelCenterX = 50f;
        private const float TunnelHalfWidth = 3f;
        private const float TunnelWallHeight = 3.5f;
        private const float TunnelStartZ = 74f;
        private const float TunnelEndZ = 110f;
        private const int TunnelArchSegments = 12;
        private const float PortalDepth = 8f;
        private const float PortalTop = 11f;
        private const int SouthPortalFirstHoleZ = 76;
        private const int NorthPortalFirstHoleZ = 103;
        private const int PortalHoleRows = 5;

        private static readonly Vector3 HighlandsOrigin = new Vector3(96f, 0f, 96f);
        private static readonly Vector3 HighlandsSize = new Vector3(128f, 40f, 128f);

        private static void BuildHighlands(Transform environment, EnvironmentPalette palette)
        {
            var highlands = CreateGroup(environment, "NortheastHighlands");
            EnsureDirectory("Assets/Art", "Terrain");
            EnsureDirectory("Assets/Art/Terrain", "DevelopmentWorld2");

            var terrainData = CreateOrUpdateTerrainData();
            var terrainObject = Terrain.CreateTerrainGameObject(terrainData);
            terrainObject.name = "HighlandsTerrain";
            terrainObject.transform.SetParent(highlands, false);
            terrainObject.transform.localPosition = HighlandsOrigin;
            var terrain = terrainObject.GetComponent<Terrain>();
            if (GraphicsSettings.currentRenderPipeline != null)
            {
                terrain.materialTemplate = GraphicsSettings.currentRenderPipeline.defaultTerrainMaterial;
            }

            BuildTunnel(highlands, palette);
            BuildPortal(highlands, palette, "SouthTunnelPortal", TunnelStartZ);
            BuildPortal(highlands, palette, "NorthTunnelPortal", TunnelEndZ - PortalDepth);
        }

        private static TerrainData CreateOrUpdateTerrainData()
        {
            var path = TerrainDirectory + "/TD_DevelopmentWorld2_Highlands.asset";
            var terrainData = AssetDatabase.LoadAssetAtPath<TerrainData>(path);
            if (terrainData == null)
            {
                terrainData = new TerrainData
                {
                    name = "TD_DevelopmentWorld2_Highlands"
                };
                AssetDatabase.CreateAsset(terrainData, path);
            }

            terrainData.heightmapResolution = HighlandsHeightmapResolution;
            terrainData.alphamapResolution = HighlandsAlphamapResolution;
            terrainData.size = HighlandsSize;

            var heights = new float[HighlandsHeightmapResolution, HighlandsHeightmapResolution];
            var spacing = HighlandsSize.x / (HighlandsHeightmapResolution - 1);
            for (var z = 0; z < HighlandsHeightmapResolution; z++)
            {
                for (var x = 0; x < HighlandsHeightmapResolution; x++)
                {
                    heights[z, x] = HighlandsHeight(x * spacing, z * spacing) / HighlandsSize.y;
                }
            }

            terrainData.SetHeights(0, 0, heights);
            terrainData.SetHoles(0, 0, CreateHighlandsSolidCells());
            terrainData.terrainLayers = new[]
            {
                CreateOrUpdateTerrainLayer("TL_DevelopmentWorld2_Grass", new Color(0.27f, 0.42f, 0.22f)),
                CreateOrUpdateTerrainLayer("TL_DevelopmentWorld2_Rock", new Color(0.45f, 0.43f, 0.40f))
            };
            terrainData.SetAlphamaps(0, 0, CreateHighlandsAlphamaps(terrainData));
            EditorUtility.SetDirty(terrainData);
            return terrainData;
        }

        // Rolling hills, and a 20 m peak with 31 degree faces. Cliffs of about 61 degrees
        // cut the north, south, and west faces, so only the east face is walkable.
        private static float HighlandsHeight(float x, float z)
        {
            var hills = Mathf.Max(
                Mathf.Max(Hill(x, z, 22f, 28f, 18f, 3.5f), Hill(x, z, 66f, 38f, 22f, 4f)),
                Mathf.Max(Hill(x, z, 104f, 24f, 16f, 3f), Hill(x, z, 108f, 116f, 12f, 2.5f)));
            var peak = 20f - (0.6f * Mathf.Max(Mathf.Abs(x - 58f), Mathf.Abs(z - 92f)));
            var southCliff = 14f + (1.8f * (z - 84f));
            var northCliff = 14f + (1.8f * (100f - z));
            var westCliff = 14f + (1.8f * (x - 44f));
            var mountain = Mathf.Min(Mathf.Min(peak, westCliff), Mathf.Min(southCliff, northCliff));
            return HighlandsBaseHeight + Mathf.Max(0f, Mathf.Max(hills, mountain));
        }

        // A smooth bell whose steepest slope is height * pi / (2 * radius), below 19 degrees
        // for every authored hill.
        private static float Hill(float x, float z, float centerX, float centerZ, float radius, float height)
        {
            var offsetX = x - centerX;
            var offsetZ = z - centerZ;
            var distance = Mathf.Sqrt((offsetX * offsetX) + (offsetZ * offsetZ)) / radius;
            return distance >= 1f ? 0f : height * 0.5f * (1f + Mathf.Cos(Mathf.PI * distance));
        }

        // Holes open the cliff where each tunnel portal enters the peak.
        private static bool[,] CreateHighlandsSolidCells()
        {
            var cells = HighlandsHeightmapResolution - 1;
            var solid = new bool[cells, cells];
            for (var z = 0; z < cells; z++)
            {
                for (var x = 0; x < cells; x++)
                {
                    solid[z, x] = !IsPortalHole(x, z);
                }
            }

            return solid;
        }

        private static bool IsPortalHole(int cellX, int cellZ)
        {
            var insideTunnel = cellX >= TunnelCenterX - TunnelHalfWidth
                && cellX < TunnelCenterX + TunnelHalfWidth;
            var southPortal = cellZ >= SouthPortalFirstHoleZ
                && cellZ < SouthPortalFirstHoleZ + PortalHoleRows;
            var northPortal = cellZ >= NorthPortalFirstHoleZ
                && cellZ < NorthPortalFirstHoleZ + PortalHoleRows;
            return insideTunnel && (southPortal || northPortal);
        }

        // Grass where the ground is walkable and rock where it is too steep to climb.
        private static float[,,] CreateHighlandsAlphamaps(TerrainData terrainData)
        {
            var resolution = terrainData.alphamapResolution;
            var alphamaps = new float[resolution, resolution, 2];
            for (var z = 0; z < resolution; z++)
            {
                for (var x = 0; x < resolution; x++)
                {
                    var steepness = terrainData.GetSteepness(
                        (x + 0.5f) / resolution,
                        (z + 0.5f) / resolution);
                    var rock = steepness >= RockSteepnessDegrees ? 1f : 0f;
                    alphamaps[z, x, 0] = 1f - rock;
                    alphamaps[z, x, 1] = rock;
                }
            }

            return alphamaps;
        }

        private static TerrainLayer CreateOrUpdateTerrainLayer(string name, Color color)
        {
            var texturePath = TerrainDirectory + "/" + name + "_Albedo.asset";
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
            if (texture == null)
            {
                texture = new Texture2D(4, 4, TextureFormat.RGBA32, false)
                {
                    name = name + "_Albedo"
                };
                AssetDatabase.CreateAsset(texture, texturePath);
            }

            // URP terrain reads smoothness from the albedo alpha, so zero keeps it matte.
            var pixels = new Color[16];
            for (var index = 0; index < pixels.Length; index++)
            {
                pixels[index] = new Color(color.r, color.g, color.b, 0f);
            }

            texture.SetPixels(pixels);
            texture.Apply();
            EditorUtility.SetDirty(texture);

            var layerPath = TerrainDirectory + "/" + name + ".terrainlayer";
            var layer = AssetDatabase.LoadAssetAtPath<TerrainLayer>(layerPath);
            if (layer == null)
            {
                layer = new TerrainLayer
                {
                    name = name
                };
                AssetDatabase.CreateAsset(layer, layerPath);
            }

            layer.diffuseTexture = texture;
            layer.tileSize = new Vector2(8f, 8f);
            layer.smoothness = 0f;
            layer.metallic = 0f;
            EditorUtility.SetDirty(layer);
            return layer;
        }

        // Inward-facing walls and an arched ceiling. The ground box is the floor, and the
        // walls reach below it so the tunnel has no gap at its feet.
        private static void BuildTunnel(Transform highlands, EnvironmentPalette palette)
        {
            var path = TerrainDirectory + "/M_DevelopmentWorld2_TunnelArch.asset";
            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (mesh == null)
            {
                mesh = new Mesh
                {
                    name = "M_DevelopmentWorld2_TunnelArch"
                };
                AssetDatabase.CreateAsset(mesh, path);
            }

            var profile = new List<Vector2>
            {
                new Vector2(-TunnelHalfWidth, -0.1f),
                new Vector2(-TunnelHalfWidth, TunnelWallHeight)
            };
            for (var segment = 1; segment < TunnelArchSegments; segment++)
            {
                var angle = Mathf.PI * (1f - ((float)segment / TunnelArchSegments));
                profile.Add(new Vector2(
                    TunnelHalfWidth * Mathf.Cos(angle),
                    TunnelWallHeight + (TunnelHalfWidth * Mathf.Sin(angle))));
            }

            profile.Add(new Vector2(TunnelHalfWidth, TunnelWallHeight));
            profile.Add(new Vector2(TunnelHalfWidth, -0.1f));

            // Each profile step becomes a flat quad from the south to the north end.
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            var startZ = HighlandsOrigin.z + TunnelStartZ;
            var endZ = HighlandsOrigin.z + TunnelEndZ;
            var centerX = HighlandsOrigin.x + TunnelCenterX;
            for (var index = 0; index < profile.Count - 1; index++)
            {
                var first = vertices.Count;
                vertices.Add(new Vector3(centerX + profile[index].x, profile[index].y, startZ));
                vertices.Add(new Vector3(centerX + profile[index + 1].x, profile[index + 1].y, startZ));
                vertices.Add(new Vector3(centerX + profile[index + 1].x, profile[index + 1].y, endZ));
                vertices.Add(new Vector3(centerX + profile[index].x, profile[index].y, endZ));
                triangles.AddRange(new[] { first, first + 1, first + 2, first, first + 2, first + 3 });
            }

            mesh.Clear();
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            EditorUtility.SetDirty(mesh);

            var tunnel = new GameObject("TunnelArch")
            {
                isStatic = true
            };
            tunnel.transform.SetParent(highlands, false);
            tunnel.AddComponent<MeshFilter>().sharedMesh = mesh;
            tunnel.AddComponent<MeshRenderer>().sharedMaterial = palette.Wilderness;
            tunnel.AddComponent<MeshCollider>().sharedMesh = mesh;
        }

        // Two wing walls and a crown frame each portal and seal the cliff hole around the
        // arch, so the inside of the peak cannot be reached.
        private static void BuildPortal(
            Transform highlands,
            EnvironmentPalette palette,
            string name,
            float startZ)
        {
            var portal = CreateGroup(highlands, name);
            var centerX = HighlandsOrigin.x + TunnelCenterX;
            var centerZ = HighlandsOrigin.z + startZ + (PortalDepth * 0.5f);
            var archTop = TunnelWallHeight + TunnelHalfWidth;
            CreateBox(portal, "WestWing", new Vector3(centerX - TunnelHalfWidth - 0.5f, archTop * 0.5f, centerZ),
                new Vector3(1f, archTop, PortalDepth), palette.Structure);
            CreateBox(portal, "EastWing", new Vector3(centerX + TunnelHalfWidth + 0.5f, archTop * 0.5f, centerZ),
                new Vector3(1f, archTop, PortalDepth), palette.Structure);
            CreateBox(portal, "Crown", new Vector3(centerX, (archTop + PortalTop) * 0.5f, centerZ),
                new Vector3((TunnelHalfWidth * 2f) + 2f, PortalTop - archTop, PortalDepth), palette.Structure);
        }
    }
}
