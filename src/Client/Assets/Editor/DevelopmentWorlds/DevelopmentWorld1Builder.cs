using UnityEditor;
using UnityEngine;

namespace Mmorpg.Client.Editor
{
    // Development World 1, the stress arena: a 64 by 64 meter walled floor where every
    // player and bot stays inside one interest radius. Spawn is in the open center, a few
    // obstacles give bot route planning work, and the hotspots sit on the edges: Mira in the
    // north, nine Iron Veins in the east, a wolf pen in the west, and a raised platform in
    // the south. The 10 m square that stress bots walk from the spawn stays free.
    public static class DevelopmentWorld1Builder
    {
        public const string MenuPath =
            "MMORPG/Tools/Development Worlds/Rebuild Development World 1 Environment";
        public const string WorldId = "development-world-1";
        public const string ScenePath = "Assets/Scenes/DevelopmentWorld1.unity";
        public const float GroundSize = 64f;

        [MenuItem(MenuPath)]
        public static void RebuildFromMenu()
        {
            if (EditorUtility.DisplayDialog(
                    "Rebuild Development World 1",
                    "This replaces every child under the Environment root and moves PlayerSpawn to the World manifest spawn. Actor content is not changed.",
                    "Rebuild",
                    "Cancel"))
            {
                RebuildForAutomation();
            }
        }

        [MenuItem(MenuPath, true)]
        private static bool ValidateRebuildFromMenu()
        {
            return !EditorApplication.isPlayingOrWillChangePlaymode;
        }

        public static void RebuildForAutomation()
        {
            var builder = DevelopmentWorldBuilder.Begin(ScenePath, WorldId, GroundSize);
            if (builder == null)
            {
                return;
            }

            builder.BuildGroundAndBoundaries(GroundSize, 3f);
            BuildSpawn(builder);
            BuildObstacles(builder);
            BuildServiceBooth(builder);
            BuildGatheringField(builder);
            BuildWolfPen(builder);
            BuildPlatform(builder);
            builder.Finish("DEVELOPMENT WORLD 1");
        }

        private static void BuildSpawn(DevelopmentWorldBuilder builder)
        {
            var spawn = builder.Group(builder.Environment, "Spawn");
            builder.Decal(spawn, "SpawnPad", Vector2.zero, new Vector2(8f, 8f), builder.Palette.Marker);
            builder.FloorLabel(spawn, "SpawnLabel", new Vector2(0f, -5f), 0f, "SPAWN AND LOOT HOTSPOT", 0.8f);
        }

        private static void BuildObstacles(DevelopmentWorldBuilder builder)
        {
            var obstacles = builder.Group(builder.Environment, "Obstacles");
            var palette = builder.Palette;
            builder.Box(obstacles, "PillarNorthWest", new Vector3(-14f, 1.5f, 14f), new Vector3(2f, 3f, 2f), palette.Structure);
            builder.Box(obstacles, "PillarNorthEast", new Vector3(14f, 1.5f, 14f), new Vector3(2f, 3f, 2f), palette.Structure);
            builder.Box(obstacles, "PillarSouthWest", new Vector3(-14f, 1.5f, -14f), new Vector3(2f, 3f, 2f), palette.Structure);
            builder.Box(obstacles, "PillarSouthEast", new Vector3(14f, 1.5f, -14f), new Vector3(2f, 3f, 2f), palette.Structure);
            builder.Box(obstacles, "LowCoverWest", new Vector3(-8f, 0.6f, -12f), new Vector3(6f, 1.2f, 0.8f), palette.Traversal);
            builder.Box(obstacles, "LowCoverEast", new Vector3(8f, 0.6f, -12f), new Vector3(6f, 1.2f, 0.8f), palette.Traversal);
            builder.Box(obstacles, "HighCoverWest", new Vector3(-20f, 1.5f, 17f), new Vector3(4f, 3f, 0.8f), palette.Traversal);
            builder.Box(obstacles, "HighCoverEast", new Vector3(20f, 1.5f, 17f), new Vector3(4f, 3f, 0.8f), palette.Traversal);
        }

        // Mira stands at (0, 0, 25), facing the spawn, in front of her booth.
        private static void BuildServiceBooth(DevelopmentWorldBuilder builder)
        {
            var booth = builder.Group(builder.Environment, "ServiceBooth");
            var palette = builder.Palette;
            builder.Decal(booth, "BoothFloor", new Vector2(0f, 26f), new Vector2(16f, 8f), palette.Plaza);
            builder.Box(booth, "Booth", new Vector3(0f, 1.5f, 28.5f), new Vector3(10f, 3f, 2f), palette.Structure);
            builder.Box(booth, "BoothRoof", new Vector3(0f, 3.15f, 27.5f), new Vector3(11f, 0.3f, 4.5f), palette.Roof, false);
            builder.Sign(booth, "BoothSign", new Vector3(-7f, 0f, 22f), 0f, "MIRA\nTrade, bank, recovery");
        }

        // A 3 by 3 Iron Vein group, 4 m apart, centered at (24, 0, 0).
        private static void BuildGatheringField(DevelopmentWorldBuilder builder)
        {
            var field = builder.Group(builder.Environment, "GatheringField");
            builder.Decal(field, "FieldFloor", new Vector2(24f, 0f), new Vector2(14f, 14f), builder.Palette.Path);
            builder.Sign(field, "FieldSign", new Vector3(17f, 0f, -9f), 0f, "IRON VEINS\nNine shared veins");
        }

        // Four Feral Wolves patrol behind 2 m walls; the east opening faces the arena.
        private static void BuildWolfPen(DevelopmentWorldBuilder builder)
        {
            var pen = builder.Group(builder.Environment, "WolfPen");
            var palette = builder.Palette;
            builder.Decal(pen, "PenFloor", new Vector2(-24f, 0f), new Vector2(14f, 14f), palette.Path);
            builder.Box(pen, "PenNorthWall", new Vector3(-24f, 1f, 7.5f), new Vector3(14f, 2f, 1f), palette.Rock);
            builder.Box(pen, "PenSouthWall", new Vector3(-24f, 1f, -7.5f), new Vector3(14f, 2f, 1f), palette.Rock);
            builder.Box(pen, "PenEastWallNorth", new Vector3(-16.5f, 1f, 5.25f), new Vector3(1f, 2f, 5.5f), palette.Rock);
            builder.Box(pen, "PenEastWallSouth", new Vector3(-16.5f, 1f, -5.25f), new Vector3(1f, 2f, 5.5f), palette.Rock);
            builder.Sign(pen, "PenSign", new Vector3(-14f, 0f, -10f), 90f, "WOLF PEN\nFour Feral Wolves");
        }

        // A 1 m platform with a 10 degree ramp from the north and stairs from the south, so
        // crowds also climb and descend.
        private static void BuildPlatform(DevelopmentWorldBuilder builder)
        {
            var platform = builder.Group(builder.Environment, "Platform");
            var palette = builder.Palette;
            builder.Box(platform, "PlatformDeck", new Vector3(10f, 0.5f, -24f), new Vector3(12f, 1f, 6f), palette.Structure);
            var ramp = builder.Group(platform, "PlatformRampFrame", new Vector3(10f, 0f, -21f), 180f);
            builder.Ramp(ramp, "PlatformRamp", 0f, 4f, 0f, 1f, 10f, palette.Traversal);
            builder.Stairs(platform, "PlatformStep", 10f, 3f, -29.4f, 4, 0.25f, 0.6f, palette.Traversal);
        }
    }
}
