using System.Collections.Generic;
using System.Linq;
using Mmorpg.Client.Worlds;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Mmorpg.Client.Editor
{
    // Builds the parts of a World that shapes describe, such as lakes, rivers, seas, and
    // waterfalls, from those shapes. It rebuilds everything of a scene at once, because one shape
    // shapes another where they meet, and writes only what changes. The World Builder window, the
    // collision bake, and the Development World builders use it; see docs/DEVELOPMENT_WORLDS.md
    // under World Builder.
    public static class WorldBuilder
    {
        // Rebuilds what the shapes of the scene make. Returns whether anything changed, and marks
        // the scene dirty when it did.
        public static bool Rebuild(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded)
            {
                return false;
            }

            var changed = WaterBuilder.Build(WaterScene.Collect(scene));
            if (changed && !EditorApplication.isPlaying && !EditorSceneManager.IsPreviewScene(scene))
            {
                EditorSceneManager.MarkSceneDirty(scene);
            }

            return changed;
        }

        // The problems of the scene's water: errors, which stop the collision bake, and warnings.
        public static List<WorldBuilderIssue> Validate(Scene scene)
        {
            var issues = WaterValidation.Validate(WaterBodies(scene));
            foreach (var lake in Shapes<LakeShape>(scene).Where(lake => lake.Shore.Count < 3))
            {
                issues.Add(new WorldBuilderIssue(lake, "A lake's shore needs at least three points."));
            }

            foreach (var sea in Shapes<SeaShape>(scene).Where(sea => sea.Outline.Count < 3))
            {
                issues.Add(new WorldBuilderIssue(sea, "A sea's outline needs at least three points."));
            }

            foreach (var river in Shapes<RiverShape>(scene).Where(river => river.Points.Count < 2))
            {
                issues.Add(new WorldBuilderIssue(river, "A river needs at least two points."));
            }

            var water = WaterScene.Collect(scene);
            var falling = new HashSet<RiverShape>();
            foreach (var waterfall in Shapes<WaterfallShape>(scene))
            {
                ValidateWaterfall(waterfall, water, falling, issues);
            }

            foreach (var shape in Shapes<WorldBuilderShape>(scene))
            {
                var transform = shape.transform;
                var tilt = Quaternion.Angle(transform.rotation, Quaternion.Euler(0f, transform.eulerAngles.y, 0f));
                if (tilt > 0.01f || (transform.lossyScale - Vector3.one).sqrMagnitude > 0.000001f)
                {
                    issues.Add(new WorldBuilderIssue(
                        shape,
                        "A shape may only turn around the vertical axis and keeps a scale of 1, so its water lies level.",
                        WorldBuilderIssueSeverity.Warning));
                }
            }

            var collisionRoot = Shapes<WorldCollisionAuthoring>(scene).Select(authoring => authoring.CollisionRoot).FirstOrDefault();
            if (collisionRoot != null)
            {
                foreach (var body in WaterBodies(scene).Where(body => !body.transform.IsChildOf(collisionRoot)))
                {
                    issues.Add(new WorldBuilderIssue(
                        body,
                        "The water lies outside the collision root " + collisionRoot.name
                        + ", so the collision bake leaves it out and nothing wades or swims in it.",
                        WorldBuilderIssueSeverity.Warning));
                }
            }

            foreach (var mesh in WorldBuilderAssets.UnusedMeshes(scene))
            {
                issues.Add(new WorldBuilderIssue(
                    mesh,
                    "No shape uses this mesh any more. Remove Unused Meshes moves it to the trash.",
                    WorldBuilderIssueSeverity.Warning));
            }

            return issues;
        }

        // A waterfall falls from the end of a river that no other waterfall falls from, down into
        // another water body.
        private static void ValidateWaterfall(
            WaterfallShape waterfall,
            WaterScene water,
            HashSet<RiverShape> falling,
            List<WorldBuilderIssue> issues)
        {
            var river = waterfall.River;
            if (river == null)
            {
                issues.Add(new WorldBuilderIssue(waterfall, "A waterfall falls from a river: choose its River."));
            }
            else if (!falling.Add(river))
            {
                issues.Add(new WorldBuilderIssue(waterfall, "Another waterfall already falls from " + river.name + "; a river has one waterfall, at its end."));
            }

            if (waterfall.Landing == null)
            {
                issues.Add(new WorldBuilderIssue(waterfall, "A waterfall falls into a water body: choose its Landing."));
            }
            else if (river != null && waterfall.Landing == river.Body)
            {
                issues.Add(new WorldBuilderIssue(waterfall, "A waterfall falls into another water body than the river it falls from."));
            }
            else
            {
                var model = water.Rivers.Find(candidate => candidate.River == river);
                var landing = water.FindSurface(waterfall.Landing);
                if (model != null
                    && landing != null
                    && model.Path.ValuesAt(model.Path.Length).Position.y - landing.Level < WaterScene.MinimumFall)
                {
                    issues.Add(new WorldBuilderIssue(
                        waterfall,
                        "The landing lies at or above the lip, so the water has nowhere to fall: lower the landing or raise the river's last point."));
                }
            }
        }

        // Moves the meshes that the World Builder made for the scene, and that nothing uses any
        // more, to the trash. Returns how many it moved.
        public static int RemoveUnusedMeshes(Scene scene)
        {
            return WorldBuilderAssets.RemoveUnusedMeshes(scene);
        }

        public static List<WaterBody> WaterBodies(Scene scene)
        {
            return Shapes<WaterBody>(scene);
        }

        public static List<T> Shapes<T>(Scene scene)
            where T : Component
        {
            var found = new List<T>();
            if (!scene.IsValid() || !scene.isLoaded)
            {
                return found;
            }

            foreach (var root in scene.GetRootGameObjects())
            {
                found.AddRange(root.GetComponentsInChildren<T>(false)
                    .Where(component => !(component is Behaviour behaviour) || behaviour.enabled));
            }

            return found;
        }
    }
}
