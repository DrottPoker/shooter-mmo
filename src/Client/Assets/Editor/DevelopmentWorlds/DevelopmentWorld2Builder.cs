using UnityEditor;
using UnityEngine;

namespace Mmorpg.Client.Editor
{
    // Development World 2, the test-everything World: 384 by 384 meters with a walled town
    // in the center and one test zone in each direction, 30 to 110 m from the town. North is
    // the movement course, northeast the highlands terrain, east the combat range, west the
    // interest road to a distant outpost, southwest the wolf woods and the alpha lair,
    // southeast the quarry, south an open meadow, and northwest a sandbox for new features.
    public static partial class DevelopmentWorld2Builder
    {
        public const string MenuPath =
            "MMORPG/Tools/Development Worlds/Rebuild Development World 2 Environment";
        public const string WorldId = "development-world-2";
        public const string ScenePath = "Assets/Scenes/DevelopmentWorld2.unity";
        public const float GroundSize = 384f;

        // The town wall's inner faces and the gate width in each side.
        private const float TownHalfSize = 36f;
        private const float TownGateWidth = 10f;

        [MenuItem(MenuPath)]
        public static void RebuildFromMenu()
        {
            if (EditorUtility.DisplayDialog(
                    "Rebuild Development World 2",
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

            builder.BuildGroundAndBoundaries(GroundSize, 6f);
            BuildTown(builder);
            BuildRoads(builder);
            BuildMovementCourse(builder);
            BuildHighlands(builder);
            BuildCombatRange(builder);
            BuildInterestRoad(builder);
            BuildWolfWoods(builder);
            BuildQuarry(builder);
            BuildMeadow(builder);
            BuildSandbox(builder);
            builder.Finish("DEVELOPMENT WORLD 2");
        }

        // The spawn is at (0, 0, -12) facing Mira at (0, 0, 6); two guards stand inside the
        // south gate. The corner buildings test a solid house, a roof to walk under, and a
        // house to walk into.
        private static void BuildTown(DevelopmentWorldBuilder builder)
        {
            var town = builder.Group(builder.Environment, "Town");
            var palette = builder.Palette;
            builder.Decal(town, "TownSquare", Vector2.zero, new Vector2(TownHalfSize * 2f, TownHalfSize * 2f), palette.Plaza);
            builder.Decal(town, "SpawnPad", new Vector2(0f, -12f), new Vector2(6f, 6f), palette.Marker, 1);
            builder.FloorLabel(town, "SpawnLabel", new Vector2(0f, -16f), 0f, "SPAWN", 0.8f);
            BuildTownWalls(builder, town);

            builder.Box(town, "ServiceHall", new Vector3(0f, 3f, 16f), new Vector3(18f, 6f, 8f), palette.Structure);
            builder.Box(town, "ServiceHallRoof", new Vector3(0f, 6.25f, 16f), new Vector3(20f, 0.5f, 10f), palette.Roof);
            builder.Sign(town, "MiraSign", new Vector3(-6f, 0f, 3f), 0f, "MIRA\nTrade, bank, recovery,\ninsurance, quests");

            builder.Box(town, "NorthwestHouse", new Vector3(-22f, 3f, 22f), new Vector3(12f, 6f, 10f), palette.Structure);
            builder.Box(town, "NorthwestHouseRoof", new Vector3(-22f, 6.25f, 22f), new Vector3(13f, 0.5f, 11f), palette.Roof);

            // Four pillars carry a roof with 4 m of headroom: camera and roof clicks.
            var arcade = builder.Group(town, "Arcade");
            foreach (var corner in new[] { new Vector2(16f, 16f), new Vector2(28f, 16f), new Vector2(16f, 28f), new Vector2(28f, 28f) })
            {
                builder.Box(arcade, "ArcadePillar" + corner.x + "_" + corner.y, new Vector3(corner.x, 2f, corner.y), new Vector3(1f, 4f, 1f), palette.Structure);
            }

            builder.Box(arcade, "ArcadeRoof", new Vector3(22f, 4.25f, 22f), new Vector3(14f, 0.5f, 14f), palette.Roof);
            builder.Sign(arcade, "ArcadeSign", new Vector3(22f, 0f, 12f), 0f, "ARCADE\nWalk under a roof");

            // A 10 by 10 m room with a 2.4 m doorway in its east wall and a roof.
            var house = builder.Group(town, "OpenHouse");
            builder.Box(house, "NorthWall", new Vector3(-22f, 2f, -16.75f), new Vector3(11f, 4f, 0.5f), palette.Structure);
            builder.Box(house, "SouthWall", new Vector3(-22f, 2f, -27.25f), new Vector3(11f, 4f, 0.5f), palette.Structure);
            builder.Box(house, "WestWall", new Vector3(-27.25f, 2f, -22f), new Vector3(0.5f, 4f, 10f), palette.Structure);
            builder.Box(house, "EastWallSouth", new Vector3(-16.75f, 2f, -25.1f), new Vector3(0.5f, 4f, 3.8f), palette.Structure);
            builder.Box(house, "EastWallNorth", new Vector3(-16.75f, 2f, -18.9f), new Vector3(0.5f, 4f, 3.8f), palette.Structure);
            builder.Box(house, "Roof", new Vector3(-22f, 4.25f, -22f), new Vector3(11.5f, 0.5f, 11.5f), palette.Roof);
            builder.Sign(house, "OpenHouseSign", new Vector3(-13f, 0f, -26f), 90f, "HOUSE\nWalk inside");

            var crates = builder.Group(town, "Crates");
            builder.Box(crates, "Crate01", new Vector3(20f, 1f, -20f), new Vector3(2f, 2f, 2f), palette.Traversal);
            builder.Box(crates, "Crate02", new Vector3(23f, 0.75f, -21f), new Vector3(1.5f, 1.5f, 1.5f), palette.Traversal, true, 20f);
            builder.Box(crates, "Crate03", new Vector3(21f, 0.5f, -24f), new Vector3(1f, 1f, 1f), palette.Traversal, true, -15f);
            builder.Box(crates, "Crate04", new Vector3(26f, 1f, -26f), new Vector3(2f, 2f, 2f), palette.Traversal, true, 10f);
        }

        private static void BuildTownWalls(DevelopmentWorldBuilder builder, Transform town)
        {
            var walls = builder.Group(town, "TownWalls");
            var palette = builder.Palette;
            var center = TownHalfSize + 0.5f;
            var segmentLength = TownHalfSize + 1f - (TownGateWidth * 0.5f);
            var segmentCenter = (TownGateWidth * 0.5f) + (segmentLength * 0.5f);
            foreach (var side in new[] { -1f, 1f })
            {
                foreach (var half in new[] { -1f, 1f })
                {
                    var suffix = (side < 0f ? "South" : "North") + (half < 0f ? "West" : "East");
                    builder.Box(walls, "Wall" + suffix, new Vector3(half * segmentCenter, 1.5f, side * center),
                        new Vector3(segmentLength, 3f, 1f), palette.Structure);
                    suffix = (side < 0f ? "West" : "East") + (half < 0f ? "South" : "North");
                    builder.Box(walls, "Wall" + suffix, new Vector3(side * center, 1.5f, half * segmentCenter),
                        new Vector3(1f, 3f, segmentLength), palette.Structure);
                    builder.Box(walls, "Tower" + (side < 0f ? "South" : "North") + (half < 0f ? "West" : "East"),
                        new Vector3(half * center, 3f, side * center), new Vector3(4f, 6f, 4f), palette.Structure);
                }
            }
        }

        // Visual roads from the four gates to the zones; nothing on them collides.
        private static void BuildRoads(DevelopmentWorldBuilder builder)
        {
            var roads = builder.Group(builder.Environment, "Roads");
            var path = builder.Palette.Path;
            builder.Decal(roads, "NorthRoad", new Vector2(0f, 51f), new Vector2(6f, 28f), path);
            builder.Decal(roads, "EastRoad", new Vector2(51f, 0f), new Vector2(28f, 6f), path);
            builder.Decal(roads, "SouthRoad", new Vector2(0f, -51f), new Vector2(6f, 28f), path);
            builder.Decal(roads, "WestRoad", new Vector2(-106f, 0f), new Vector2(138f, 6f), path);
        }
    }
}
