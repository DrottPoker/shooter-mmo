using System.Linq;
using Mmorpg.Client.Worlds;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace Mmorpg.Client.Editor
{
    // Adds new World Builder shapes to a scene: a lake, a river, a sea, or a waterfall from a
    // river, below the scene's Water group, with a unique body id, the water's materials, and the
    // static flags water keeps, ready to shape with the handles in the Scene view. Every addition
    // can be undone.
    public static class WorldBuilderCreate
    {
        public const string WaterGroupName = "Water";

        public static LakeShape AddLake(Scene scene, Vector3 position)
        {
            return AddWater<LakeShape>(scene, "Lake", "lake", WaterBodyKind.Lake, position);
        }

        public static RiverShape AddRiver(Scene scene, Vector3 position)
        {
            return AddWater<RiverShape>(scene, "River", "river", WaterBodyKind.River, position);
        }

        public static SeaShape AddSea(Scene scene, Vector3 position)
        {
            return AddWater<SeaShape>(scene, "Sea", "sea", WaterBodyKind.Sea, position);
        }

        // A waterfall from the river's end into the highest water below it, if any.
        public static WaterfallShape AddWaterfall(RiverShape river)
        {
            var scene = river.gameObject.scene;
            var waterfall = new GameObject(UniqueName(scene, river.name + " Waterfall"));
            Undo.RegisterCreatedObjectUndo(waterfall, "Add Waterfall");
            waterfall.transform.SetParent(WaterGroup(scene), false);
            waterfall.AddComponent<MeshFilter>();
            var renderer = waterfall.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = WaterAssets.LoadMaterial(WaterAssets.WaterfallMaterialPath);
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            var shape = waterfall.AddComponent<WaterfallShape>();
            var serialized = new SerializedObject(shape);
            serialized.FindProperty("river").objectReferenceValue = river;
            serialized.FindProperty("landing").objectReferenceValue = WaterBelow(river);
            serialized.ApplyModifiedPropertiesWithoutUndo();
            Selection.activeGameObject = waterfall;
            return shape;
        }

        // The water body below the end of a river: the highest lake, sea, or other river under
        // where its water lands, found from a meter or two out from the end.
        public static WaterBody WaterBelow(RiverShape river)
        {
            var water = WaterScene.Collect(river.gameObject.scene);
            var model = water.Rivers.Find(candidate => candidate.River == river);
            if (model == null)
            {
                return null;
            }

            var path = model.Path;
            var end = path.ValuesAt(path.Length).Position;
            var outward = path.PlanarAt(path.Length) + (path.DirectionAt(path.Length) * 1.5f);
            SurfaceModel best = null;
            var bestLevel = float.MinValue;
            foreach (var body in water.Lakes.Cast<SurfaceModel>().Concat(water.Seas).Concat(water.Rivers))
            {
                if (body == model || !body.Covers(outward))
                {
                    continue;
                }

                var level = body.LevelAt(outward);
                if (level < end.y - WaterScene.MinimumFall && level > bestLevel)
                {
                    best = body;
                    bestLevel = level;
                }
            }

            return best != null ? best.Shape.Body : null;
        }

        private static T AddWater<T>(Scene scene, string name, string idPrefix, WaterBodyKind kind, Vector3 position)
            where T : WaterSurfaceShape
        {
            var body = new GameObject(UniqueName(scene, name));
            Undo.RegisterCreatedObjectUndo(body, "Add " + name);
            body.transform.SetParent(WaterGroup(scene), false);
            body.transform.position = position;

            // Water is static, like the ground, but never batched, because the client reads
            // each surface to put wakes on its waves, and no occluder, since it is seen through.
            GameObjectUtility.SetStaticEditorFlags(
                body,
                (StaticEditorFlags)~0 & ~(StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccluderStatic));
            body.AddComponent<MeshFilter>();
            var renderer = body.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = WaterAssets.LoadMaterial(WaterAssets.SurfaceMaterialPath);
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            var water = new SerializedObject(body.AddComponent<WaterBody>());
            water.FindProperty("bodyId").stringValue = UniqueBodyId(scene, idPrefix);
            water.FindProperty("kind").enumValueIndex = (int)kind;
            water.ApplyModifiedPropertiesWithoutUndo();
            var shape = body.AddComponent<T>();
            Selection.activeGameObject = body;
            WorldBuilder.Rebuild(scene);
            return shape;
        }

        // The Water group below the collision root, so the bake reads the water, or below the
        // scene's root without one.
        private static Transform WaterGroup(Scene scene)
        {
            Transform root = null;
            foreach (var candidate in scene.GetRootGameObjects())
            {
                var authoring = candidate.GetComponentInChildren<WorldCollisionAuthoring>(true);
                if (authoring != null && authoring.CollisionRoot != null)
                {
                    root = authoring.CollisionRoot;
                    break;
                }
            }

            var existing = root != null
                ? root.Find(WaterGroupName)
                : scene.GetRootGameObjects().FirstOrDefault(candidate => candidate.name == WaterGroupName)?.transform;
            if (existing != null)
            {
                return existing;
            }

            var group = new GameObject(WaterGroupName);
            Undo.RegisterCreatedObjectUndo(group, "Add Water Group");
            if (root != null)
            {
                group.transform.SetParent(root, false);
            }
            else
            {
                SceneManager.MoveGameObjectToScene(group, scene);
            }

            return group.transform;
        }

        private static string UniqueBodyId(Scene scene, string prefix)
        {
            var taken = WorldBuilder.WaterBodies(scene).Select(body => body.BodyId).ToHashSet();
            if (!taken.Contains(prefix))
            {
                return prefix;
            }

            for (var number = 2; ; number++)
            {
                var id = prefix + "-" + number;
                if (!taken.Contains(id))
                {
                    return id;
                }
            }
        }

        private static string UniqueName(Scene scene, string name)
        {
            var taken = scene.GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<Transform>(true))
                .Select(transform => transform.name)
                .ToHashSet();
            if (!taken.Contains(name))
            {
                return name;
            }

            for (var number = 2; ; number++)
            {
                var candidate = name + " " + number;
                if (!taken.Contains(candidate))
                {
                    return candidate;
                }
            }
        }
    }
}
