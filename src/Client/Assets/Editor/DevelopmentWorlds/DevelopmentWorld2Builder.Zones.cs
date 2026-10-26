using UnityEngine;

namespace Mmorpg.Client.Editor
{
    // The outer test zones of Development World 2. Actor positions come from the World's
    // actor-spawns.json; the layout only keeps them free and labels the zone.
    public static partial class DevelopmentWorld2Builder
    {
        // The outpost lies beyond the 144 m interest exit radius from the town.
        public static readonly Vector2 OutpostCenter = new Vector2(-172f, 0f);
        public static readonly int[] InterestMarkerDistances = { 64, 128, 144 };

        public const float RangeLaneStartX = 130f;
        public const float RangeLaneCenterZ = 30f;

        // Trees of the wolf woods, kept off the wolf spawn area X/Z -120 to -90, its patrol,
        // and the lair.
        private static readonly Vector2[] WoodsTrees =
        {
            new Vector2(-70f, -78f), new Vector2(-76f, -96f), new Vector2(-68f, -118f),
            new Vector2(-82f, -136f), new Vector2(-84f, -70f), new Vector2(-100f, -74f),
            new Vector2(-116f, -70f), new Vector2(-132f, -78f), new Vector2(-136f, -96f),
            new Vector2(-142f, -114f), new Vector2(-128f, -130f), new Vector2(-108f, -138f),
            new Vector2(-94f, -146f), new Vector2(-146f, -82f), new Vector2(-80f, -112f),
            new Vector2(-124f, -144f)
        };

        private static readonly Vector2[] MeadowTrees =
        {
            new Vector2(-24f, -78f), new Vector2(18f, -92f), new Vector2(-10f, -118f),
            new Vector2(30f, -130f), new Vector2(-34f, -146f), new Vector2(8f, -160f)
        };

        // A 40 by 40 m arena with a 1.2 m wall that eyes see over, full and low cover inside,
        // and a 35 m range lane with a stripe every 5 m north of it.
        private static void BuildCombatRange(DevelopmentWorldBuilder builder)
        {
            var range = builder.Group(builder.Environment, "CombatRange");
            var palette = builder.Palette;
            builder.Sign(range, "RangeSign", new Vector3(68f, 0f, -6f), 90f, "COMBAT RANGE\nPvP arena, range lane");

            var arena = builder.Group(range, "Arena", new Vector3(100f, 0f, 0f));
            builder.Decal(arena, "ArenaFloor", Vector2.zero, new Vector2(40f, 40f), palette.Plaza);
            builder.Box(arena, "WallNorth", new Vector3(0f, 0.6f, 20.3f), new Vector3(41.2f, 1.2f, 0.6f), palette.Structure);
            builder.Box(arena, "WallSouth", new Vector3(0f, 0.6f, -20.3f), new Vector3(41.2f, 1.2f, 0.6f), palette.Structure);
            foreach (var side in new[] { -1f, 1f })
            {
                var name = side < 0f ? "West" : "East";
                builder.Box(arena, "Wall" + name + "North", new Vector3(side * 20.3f, 0.6f, 11.5f), new Vector3(0.6f, 1.2f, 17f), palette.Structure);
                builder.Box(arena, "Wall" + name + "South", new Vector3(side * 20.3f, 0.6f, -11.5f), new Vector3(0.6f, 1.2f, 17f), palette.Structure);
            }

            foreach (var corner in new[] { new Vector2(-8f, -8f), new Vector2(8f, -8f), new Vector2(-8f, 8f), new Vector2(8f, 8f) })
            {
                builder.Box(arena, "FullCover" + corner.x + "_" + corner.y, new Vector3(corner.x, 2f, corner.y),
                    new Vector3(2f, 4f, 2f), palette.Structure);
            }

            builder.Box(arena, "LowCoverNorth", new Vector3(0f, 0.5f, 12f), new Vector3(4f, 1f, 1f), palette.Traversal);
            builder.Box(arena, "LowCoverSouth", new Vector3(0f, 0.5f, -12f), new Vector3(4f, 1f, 1f), palette.Traversal);
            builder.FloorLabel(arena, "LowCoverLabel", new Vector2(0f, 10f), 0f, "low cover 1.0 m");
            builder.FloorLabel(arena, "FullCoverLabel", new Vector2(-8f, 5.5f), 0f, "full cover 4 m");

            var lane = builder.Group(range, "RangeLane");
            builder.Decal(lane, "LaneFloor", new Vector2(RangeLaneStartX + 17.5f, RangeLaneCenterZ), new Vector2(39f, 12f), palette.Path);
            for (var distance = 0; distance <= 35; distance += 5)
            {
                var x = RangeLaneStartX + distance;
                builder.Decal(lane, "Stripe" + distance.ToString("00"), new Vector2(x, RangeLaneCenterZ), new Vector2(0.3f, 12f),
                    distance == 0 ? palette.Marker : palette.Plaza, 1);
                builder.FloorLabel(lane, "StripeLabel" + distance.ToString("00"), new Vector2(x, RangeLaneCenterZ + 7f), 90f,
                    distance == 0 ? "start" : distance + " m");
            }
        }

        // The west road leads 172 m from the town to an outpost with a guard, past markers
        // at the interest enter and exit radius, so town entities and the guard swap.
        private static void BuildInterestRoad(DevelopmentWorldBuilder builder)
        {
            var road = builder.Group(builder.Environment, "InterestRoad");
            var palette = builder.Palette;
            foreach (var distance in InterestMarkerDistances)
            {
                builder.Decal(road, "Marker" + distance, new Vector2(-distance, 0f), new Vector2(0.5f, 6f), palette.Marker, 1);
                builder.Sign(road, "MarkerSign" + distance, new Vector3(-distance, 0f, 5f), 90f,
                    distance + " m FROM TOWN\n" + (distance == 128 ? "Interest enter" : distance == 144 ? "Interest exit" : "Collision streaming"));
            }

            var outpost = builder.Group(road, "Outpost", new Vector3(OutpostCenter.x, 0f, OutpostCenter.y));
            builder.Decal(outpost, "OutpostFloor", Vector2.zero, new Vector2(16f, 16f), palette.Path, 1);
            builder.Box(outpost, "PalisadeNorth", new Vector3(0f, 1.25f, 8.5f), new Vector3(18f, 2.5f, 1f), palette.Trunk);
            builder.Box(outpost, "PalisadeSouth", new Vector3(0f, 1.25f, -8.5f), new Vector3(18f, 2.5f, 1f), palette.Trunk);
            builder.Box(outpost, "PalisadeWest", new Vector3(-8.5f, 1.25f, 0f), new Vector3(1f, 2.5f, 16f), palette.Trunk);
            builder.Box(outpost, "PalisadeEastNorth", new Vector3(8.5f, 1.25f, 5.5f), new Vector3(1f, 2.5f, 5f), palette.Trunk);
            builder.Box(outpost, "PalisadeEastSouth", new Vector3(8.5f, 1.25f, -5.5f), new Vector3(1f, 2.5f, 5f), palette.Trunk);
            builder.Box(outpost, "Watchtower", new Vector3(-5f, 4f, -5f), new Vector3(3f, 8f, 3f), palette.Trunk);
            builder.Sign(road, "OutpostSign", new Vector3(OutpostCenter.x + 12f, 0f, 6f), 90f, "OUTPOST\n172 m from town");
        }

        // Wolves roam X/Z -120 to -90 between trees; the Feral Alpha waits in a ring of rocks
        // around (-160, -160) that opens to the northeast.
        private static void BuildWolfWoods(DevelopmentWorldBuilder builder)
        {
            var woods = builder.Group(builder.Environment, "WolfWoods");
            var palette = builder.Palette;
            builder.Sign(woods, "WoodsSign", new Vector3(-62f, 0f, -58f), 45f, "WOLF WOODS\nFive Feral Wolves");
            for (var index = 0; index < WoodsTrees.Length; index++)
            {
                Tree(builder, woods, "Tree" + (index + 1).ToString("00"), WoodsTrees[index], index);
            }

            var lair = builder.Group(woods, "AlphaLair", new Vector3(-160f, 0f, -160f));
            builder.Decal(lair, "LairFloor", Vector2.zero, new Vector2(20f, 20f), palette.Path);
            for (var index = 0; index < 7; index++)
            {
                // Seven rocks on a 12 m ring, leaving the northeast open. A yaw of -(90 + a)
                // turns each rock's long side along the ring at angle a.
                var angleDegrees = 90f + (index * 40f);
                var angle = angleDegrees * Mathf.Deg2Rad;
                var height = 4f + (index % 3);
                builder.Box(lair, "LairRock" + (index + 1).ToString("00"),
                    new Vector3(Mathf.Cos(angle) * 12f, height * 0.5f, Mathf.Sin(angle) * 12f),
                    new Vector3(7f, height, 4f), palette.Rock, true, -(90f + angleDegrees));
            }

            builder.Sign(woods, "LairSign", new Vector3(-142f, 0f, -146f), 45f, "ALPHA LAIR\nFeral Alpha boss");
        }

        // Seven Iron Veins: a 2 by 2 group around (84, 0, -84) and three single veins, inside
        // a ring of quarry rocks open to the town.
        private static void BuildQuarry(DevelopmentWorldBuilder builder)
        {
            var quarry = builder.Group(builder.Environment, "Quarry");
            var palette = builder.Palette;
            builder.Decal(quarry, "QuarryFloor", new Vector2(92f, -92f), new Vector2(48f, 48f), palette.Path);
            builder.Box(quarry, "QuarryWallSouth", new Vector3(96f, 2.5f, -118f), new Vector3(44f, 5f, 3f), palette.Rock, true, 3f);
            builder.Box(quarry, "QuarryWallEast", new Vector3(118f, 2.5f, -96f), new Vector3(3f, 5f, 44f), palette.Rock, true, -3f);
            builder.Box(quarry, "QuarryBoulder01", new Vector3(96f, 1f, -90f), new Vector3(3f, 2f, 2.5f), palette.Rock, true, 25f);
            builder.Box(quarry, "QuarryBoulder02", new Vector3(74f, 0.75f, -92f), new Vector3(2f, 1.5f, 2f), palette.Rock, true, -10f);
            builder.Sign(quarry, "QuarrySign", new Vector3(64f, 0f, -62f), -45f, "QUARRY\nSeven Iron Veins");
        }

        private static void BuildMeadow(DevelopmentWorldBuilder builder)
        {
            var meadow = builder.Group(builder.Environment, "Meadow");
            builder.Sign(meadow, "MeadowSign", new Vector3(6f, 0f, -66f), 0f, "MEADOW\nOpen ground for crowds");
            // The four Oak Trees east of the meadow, around (40, 0, -90), are actors.
            builder.Sign(meadow, "OakGroveSign", new Vector3(28f, 0f, -74f), 0f, "OAK GROVE\nFour Oak Trees");
            for (var index = 0; index < MeadowTrees.Length; index++)
            {
                Tree(builder, meadow, "Tree" + (index + 1).ToString("00"), MeadowTrees[index], index);
            }
        }

        private static void BuildSandbox(DevelopmentWorldBuilder builder)
        {
            var sandbox = builder.Group(builder.Environment, "Sandbox");
            builder.Decal(sandbox, "SandboxPad", new Vector2(-120f, 120f), new Vector2(100f, 100f), builder.Palette.Sandbox);
            builder.Sign(sandbox, "SandboxSign", new Vector3(-66f, 0f, 66f), -45f, "SANDBOX\nSpace for new features");
        }

        // A collidable trunk under a crown that only blocks the view.
        private static void Tree(DevelopmentWorldBuilder builder, Transform parent, string name, Vector2 position, int variant)
        {
            var height = 5f + (variant % 3);
            var tree = builder.Group(parent, name, new Vector3(position.x, 0f, position.y), variant * 37f);
            builder.Box(tree, "Trunk", new Vector3(0f, height * 0.5f, 0f), new Vector3(0.8f, height, 0.8f), builder.Palette.Trunk);
            builder.Box(tree, "Crown", new Vector3(0f, height + 1.2f, 0f), new Vector3(4f, 3f, 4f), builder.Palette.Foliage, false);
        }
    }
}
