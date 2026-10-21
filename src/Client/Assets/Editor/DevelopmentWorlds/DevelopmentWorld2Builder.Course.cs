using System.Globalization;
using UnityEngine;

namespace Mmorpg.Client.Editor
{
    // The movement course north of the town, X -60 to 60 and Z 64 to 150. Every station
    // sits just inside or just outside a movement limit, green when the character gets
    // through and red when it must not: step heights around the 0.35 m step limit, slopes
    // around the 45 degree slope limit, gaps around the 0.7 m body width, stairs and a
    // ledge, a bridge to walk over and under, a maze for route planning, and camera tests.
    public static partial class DevelopmentWorld2Builder
    {
        public static readonly float[] StepRises = { 0.15f, 0.25f, 0.34f, 0.36f, 0.5f };
        public static readonly float[] SlopeDegrees = { 20f, 35f, 42f, 48f, 60f };
        public static readonly float[] GapWidths = { 1.5f, 1f, 0.6f };

        // Station X centers, shared by the step pads and the slope pads.
        public static readonly float[] StationX = { -20f, -12f, -4f, 4f, 12f };
        // Gap centers lie on route grid cell centers (odd multiples of 0.25 m), so the 1.0 m
        // gap leaves room for the planner's 0.4 m clearance on both sides.
        public static readonly float[] GapX = { 4.25f, 12.25f, 20.25f };

        public const float StepPadSouthZ = 76.5f;
        public const float SlopePadSouthZ = 96f;
        public const float SlopePadHeight = 2f;
        public const float GapWallZ = 112f;
        public const float StairsX = -32f;
        public const float StairsStartZ = 108f;
        public const float StairsTopHeight = 3f;
        public const float BridgeHeight = 4f;

        private static void BuildMovementCourse(DevelopmentWorldBuilder builder)
        {
            var course = builder.Group(builder.Environment, "MovementCourse");
            builder.Sign(course, "CourseSign", new Vector3(-6f, 0f, 66f), 0f, "MOVEMENT COURSE\nSteps, slopes, gaps, stairs");
            BuildStepStation(builder, course);
            BuildSlopeStation(builder, course);
            BuildStairsAndLedge(builder, course);
            BuildGapStation(builder, course);
            BuildBridge(builder, course);
            BuildMaze(builder, course);
            BuildCameraStation(builder, course);
        }

        // 3 by 3 m pads to step onto from the south.
        private static void BuildStepStation(DevelopmentWorldBuilder builder, Transform course)
        {
            var steps = builder.Group(course, "StepHeights");
            var palette = builder.Palette;
            for (var index = 0; index < StepRises.Length; index++)
            {
                var rise = StepRises[index];
                var passable = rise <= DevelopmentWorldBuilder.StepHeight;
                builder.Box(steps, "StepPad" + (index + 1).ToString("00"),
                    new Vector3(StationX[index], rise * 0.5f, StepPadSouthZ + 1.5f),
                    new Vector3(3f, rise, 3f),
                    passable ? palette.Passable : palette.Blocked);
                builder.FloorLabel(steps, "StepLabel" + (index + 1).ToString("00"),
                    new Vector2(StationX[index], StepPadSouthZ - 1.5f), 0f,
                    rise.ToString("0.00", CultureInfo.InvariantCulture) + " m" + (passable ? string.Empty : "\nblocked"));
            }

            builder.Sign(steps, "StepSign", new Vector3(-27f, 0f, 77f), 0f, "STEP HEIGHT\nLimit 0.35 m");
        }

        // Ramps rise from the south onto separate 2 m pads, so a blocked pad is never
        // reachable from its neighbor.
        private static void BuildSlopeStation(DevelopmentWorldBuilder builder, Transform course)
        {
            var slopes = builder.Group(course, "Slopes");
            var palette = builder.Palette;
            for (var index = 0; index < SlopeDegrees.Length; index++)
            {
                var slope = SlopeDegrees[index];
                var passable = slope < DevelopmentWorldBuilder.MaximumSlopeDegrees;
                var material = passable ? palette.Passable : palette.Blocked;
                var name = (index + 1).ToString("00");
                builder.Box(slopes, "SlopePad" + name,
                    new Vector3(StationX[index], SlopePadHeight * 0.5f, SlopePadSouthZ + 2f),
                    new Vector3(4f, SlopePadHeight, 4f), material);
                builder.Ramp(slopes, "SlopeRamp" + name, StationX[index], 3f, SlopePadSouthZ,
                    SlopePadHeight, slope, material);
                builder.FloorLabel(slopes, "SlopeLabel" + name,
                    new Vector2(StationX[index], 88f), 0f,
                    slope.ToString("0", CultureInfo.InvariantCulture) + " deg" + (passable ? string.Empty : "\nblocked"));
            }

            builder.Sign(slopes, "SlopeSign", new Vector3(-27f, 0f, 90f), 0f, "SLOPES\nLimit 45 degrees");
        }

        // Ten 0.3 m steps climb to a 3 m platform. Routes never step off its north ledge,
        // because a route only drops as far as the ground snap distance.
        private static void BuildStairsAndLedge(DevelopmentWorldBuilder builder, Transform course)
        {
            var stairs = builder.Group(course, "StairsAndLedge");
            var palette = builder.Palette;
            builder.Stairs(stairs, "Stair", StairsX, 3f, StairsStartZ, 10, 0.3f, 0.5f, palette.Traversal);
            builder.Box(stairs, "LedgePlatform", new Vector3(StairsX, StairsTopHeight * 0.5f, 116f),
                new Vector3(6f, StairsTopHeight, 6f), palette.Structure);
            builder.FloorLabel(stairs, "LedgeLabel", new Vector2(StairsX, 121f), 0f, "3 m ledge");
            builder.Sign(stairs, "StairsSign", new Vector3(StairsX - 8f, 0f, 109f), 0f, "STAIRS AND LEDGE\n0.30 m steps; routes take\nthe stairs, not the ledge");
        }

        // One 2.5 m wall along X with three gaps to walk north through. The character is
        // 0.7 m wide.
        private static void BuildGapStation(DevelopmentWorldBuilder builder, Transform course)
        {
            var gaps = builder.Group(course, "Gaps");
            var palette = builder.Palette;
            var start = 0f;
            for (var index = 0; index <= GapX.Length; index++)
            {
                var end = index < GapX.Length ? GapX[index] - (GapWidths[index] * 0.5f) : 24f;
                builder.Box(gaps, "GapWall" + (index + 1).ToString("00"),
                    new Vector3((start + end) * 0.5f, 1.25f, GapWallZ),
                    new Vector3(end - start, 2.5f, 1f), palette.Structure);
                if (index < GapX.Length)
                {
                    start = GapX[index] + (GapWidths[index] * 0.5f);
                    var passable = GapWidths[index] > 0.7f;
                    builder.FloorLabel(gaps, "GapLabel" + (index + 1).ToString("00"),
                        new Vector2(GapX[index], GapWallZ - 2.5f), 0f,
                        GapWidths[index].ToString("0.0", CultureInfo.InvariantCulture) + " m" + (passable ? string.Empty : "\nblocked"));
                }
            }

            builder.Sign(gaps, "GapSign", new Vector3(28f, 0f, 109f), 0f, "GAPS\nBody width 0.7 m");
        }

        // Two 4 m platforms joined by a bridge with 3.5 m of headroom below it. The west one
        // has a 10 degree ramp, the east one stairs of 0.25 m.
        private static void BuildBridge(DevelopmentWorldBuilder builder, Transform course)
        {
            var bridge = builder.Group(course, "Bridge");
            var palette = builder.Palette;
            builder.Box(bridge, "WestPlatform", new Vector3(-20f, BridgeHeight * 0.5f, 140f),
                new Vector3(8f, BridgeHeight, 8f), palette.Structure);
            builder.Ramp(bridge, "WestPlatformRamp", -20f, 4f, 136f, BridgeHeight, 10f, palette.Traversal);
            builder.Box(bridge, "EastPlatform", new Vector3(20f, BridgeHeight * 0.5f, 140f),
                new Vector3(8f, BridgeHeight, 8f), palette.Structure);
            builder.Stairs(bridge, "EastPlatformStair", 20f, 3f, 136f - 9.6f, 16, 0.25f, 0.6f, palette.Traversal);
            builder.Box(bridge, "BridgeDeck", new Vector3(0f, BridgeHeight - 0.25f, 140f),
                new Vector3(32f, 0.5f, 4f), palette.Traversal);
            builder.Sign(bridge, "BridgeSign", new Vector3(-6f, 0f, 133f), 0f, "BRIDGE\nWalk over and under");
        }

        // A 24 by 24 m serpentine with 6 m lanes: in from the south, out to the north.
        private static void BuildMaze(DevelopmentWorldBuilder builder, Transform course)
        {
            var maze = builder.Group(course, "Maze");
            var wall = builder.Palette.Structure;
            const float west = -58f;
            const float east = -34f;
            const float south = 76f;
            const float north = 100f;
            builder.Box(maze, "MazeWest", new Vector3(west - 0.5f, 1.25f, 88f), new Vector3(1f, 2.5f, 26f), wall);
            builder.Box(maze, "MazeEast", new Vector3(east + 0.5f, 1.25f, 88f), new Vector3(1f, 2.5f, 26f), wall);
            builder.Box(maze, "MazeSouth", new Vector3(-43f, 1.25f, south - 0.5f), new Vector3(18f, 2.5f, 1f), wall);
            builder.Box(maze, "MazeNorth", new Vector3(-49f, 1.25f, north + 0.5f), new Vector3(18f, 2.5f, 1f), wall);
            builder.Box(maze, "MazeInner01", new Vector3(-48f, 1.25f, 82f), new Vector3(20f, 2.5f, 1f), wall);
            builder.Box(maze, "MazeInner02", new Vector3(-44f, 1.25f, 88f), new Vector3(20f, 2.5f, 1f), wall);
            builder.Box(maze, "MazeInner03", new Vector3(-48f, 1.25f, 94f), new Vector3(20f, 2.5f, 1f), wall);
            builder.Sign(maze, "MazeSign", new Vector3(-55f, 0f, 70f), 0f, "MAZE\nIn south, out north");
        }

        // A covered corridor with a 2.6 m ceiling and a 2 m alley between 8 m walls.
        private static void BuildCameraStation(DevelopmentWorldBuilder builder, Transform course)
        {
            var camera = builder.Group(course, "CameraTests");
            var palette = builder.Palette;
            builder.Box(camera, "CorridorWest", new Vector3(42.25f, 1.3f, 86f), new Vector3(0.5f, 2.6f, 12f), palette.Structure);
            builder.Box(camera, "CorridorEast", new Vector3(45.75f, 1.3f, 86f), new Vector3(0.5f, 2.6f, 12f), palette.Structure);
            builder.Box(camera, "CorridorCeiling", new Vector3(44f, 2.75f, 86f), new Vector3(4f, 0.3f, 12f), palette.Roof);
            builder.Box(camera, "AlleyWest", new Vector3(39.5f, 4f, 108f), new Vector3(1f, 8f, 16f), palette.Structure);
            builder.Box(camera, "AlleyEast", new Vector3(42.5f, 4f, 108f), new Vector3(1f, 8f, 16f), palette.Structure);
            builder.Sign(camera, "CameraSign", new Vector3(50f, 0f, 77f), 0f, "CAMERA\nLow ceiling, narrow alley");
        }
    }
}
