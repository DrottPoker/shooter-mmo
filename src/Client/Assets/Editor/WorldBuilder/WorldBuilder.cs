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
    // waterfalls and the beds they carve into a terrain, from those shapes. It rebuilds everything
    // of a scene at once, because one shape shapes another where they meet, and writes only what
    // changes. The World Builder window, the collision bake, and the Development World builders
    // use it; see docs/DEVELOPMENT_WORLDS.md under World Builder.
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

            var water = WaterScene.Collect(scene);
            var changed = WaterBuilder.Build(water);
            changed |= TerrainCarver.Build(scene, WaterBed.Collect(water));
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

            // What a shape that makes nothing lacks, exactly as the build sees it.
            foreach (var incomplete in WaterScene.Collect(scene).Incomplete)
            {
                issues.Add(new WorldBuilderIssue(incomplete.Key, incomplete.Value));
            }

            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var disabled in root.GetComponentsInChildren<WorldBuilderShape>(false).Where(shape => !shape.enabled))
                {
                    issues.Add(new WorldBuilderIssue(
                        disabled,
                        "The shape is disabled, so the World Builder builds nothing from it and what it made stays as it was.",
                        WorldBuilderIssueSeverity.Warning));
                }
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
