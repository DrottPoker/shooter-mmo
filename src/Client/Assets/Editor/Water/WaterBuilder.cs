using Mmorpg.Client.Worlds;
using UnityEditor;
using UnityEngine;

namespace Mmorpg.Client.Editor
{
    // Writes everything the water shapes of a scene make: the surface of every lake, river, and
    // sea, the still sea beyond the World and its seabed, and every waterfall's falling water and
    // spray. A mesh is written only where it changes, so a rebuild without a change marks nothing
    // dirty.
    internal static class WaterBuilder
    {
        public const string HorizonName = "SeaBeyondTheWorld";
        public const string SeabedName = "Seabed";

        // Returns whether anything changed.
        public static bool Build(WaterScene water)
        {
            var assets = new WorldBuilderAssets();
            var changed = false;
            foreach (var lake in water.Lakes)
            {
                changed |= Write(assets, lake.Shape, LakeSurfaceBuilder.Build(lake));
            }

            foreach (var river in water.Rivers)
            {
                changed |= Write(assets, river.Shape, RiverSurfaceBuilder.Build(river));
            }

            foreach (var sea in water.Seas)
            {
                var surface = Write(assets, sea.Shape, SeaSurfaceBuilder.Build(sea, water.WorldEdge));
                changed |= surface | BuildHorizon(assets, sea, water.WorldEdge);
            }

            var spray = AssetDatabase.LoadAssetAtPath<Material>(WaterAssets.SprayMaterialPath);
            foreach (var waterfall in water.Waterfalls)
            {
                var filter = waterfall.Shape.GetComponent<MeshFilter>();
                var mesh = assets.MeshOf(filter, waterfall.Shape.name);
                var column = new Mesh();
                WaterfallBuilder.BuildColumn(waterfall, column);
                if (!SameMesh(column, mesh))
                {
                    EditorUtility.CopySerialized(column, mesh);
                    EditorUtility.SetDirty(mesh);
                    changed = true;
                }

                Object.DestroyImmediate(column);
                WaterfallBuilder.BuildSpray(waterfall, spray);
            }

            foreach (var shape in water.Incomplete)
            {
                changed |= Clear(assets, shape);
            }

            return changed;
        }

        // An incomplete shape shows nothing until it is complete: its mesh is emptied, or let go
        // where another shape of the build writes it, and a waterfall's spray goes.
        private static bool Clear(WorldBuilderAssets assets, WorldBuilderShape shape)
        {
            var changed = false;
            var filter = shape.GetComponent<MeshFilter>();
            var mesh = filter != null ? filter.sharedMesh : null;
            if (mesh != null && assets.IsClaimed(mesh))
            {
                filter.sharedMesh = null;
                EditorUtility.SetDirty(filter);
                changed = true;
            }
            else if (mesh != null && mesh.vertexCount > 0)
            {
                mesh.Clear();
                EditorUtility.SetDirty(mesh);
                changed = true;
            }

            if (shape is WaterfallShape)
            {
                foreach (var name in new[] { WaterfallBuilder.BoilName, WaterfallBuilder.MistName, WaterfallBuilder.SplashName })
                {
                    changed |= Remove(shape.transform, name);
                }
            }

            return changed;
        }

        private static bool Write(WorldBuilderAssets assets, WaterSurfaceShape shape, WaterSurfaceMesh surface)
        {
            var mesh = assets.MeshOf(shape.GetComponent<MeshFilter>(), shape.Body.BodyId.Length > 0 ? shape.Body.BodyId : shape.name);
            if (!surface.WriteTo(mesh))
            {
                return false;
            }

            EditorUtility.SetDirty(mesh);
            return true;
        }

        // The still sea beyond the World's ground on the horizon sides and its seabed, as children
        // of the sea that only show; a sea without a horizon has neither.
        private static bool BuildHorizon(WorldBuilderAssets assets, SeaModel sea, Rect? worldEdge)
        {
            var shape = sea.Sea;
            var parent = shape.transform;
            var enabled = worldEdge.HasValue && shape.Horizon != HorizonSides.None && shape.HorizonExtent > 0f;
            if (!enabled)
            {
                var removed = Remove(parent, HorizonName) | Remove(parent, SeabedName);
                return removed;
            }

            var owner = shape.GetComponent<MeshFilter>().sharedMesh;
            var horizon = Child(parent, HorizonName, shape.GetComponent<MeshRenderer>().sharedMaterial);
            var horizonMesh = assets.MeshNextTo(horizon.GetComponent<MeshFilter>(), owner, "BeyondTheWorld");
            var changed = SeaSurfaceBuilder.BuildHorizon(sea, worldEdge.Value).WriteTo(horizonMesh);
            if (changed)
            {
                EditorUtility.SetDirty(horizonMesh);
            }

            if (shape.SeabedMaterial == null)
            {
                return changed | Remove(parent, SeabedName);
            }

            var seabed = Child(parent, SeabedName, shape.SeabedMaterial);
            var seabedMesh = assets.MeshNextTo(seabed.GetComponent<MeshFilter>(), owner, "Seabed");
            var built = new Mesh();
            SeaSurfaceBuilder.BuildSeabed(sea, worldEdge.Value, built);
            if (!SameMesh(built, seabedMesh))
            {
                EditorUtility.CopySerialized(built, seabedMesh);
                EditorUtility.SetDirty(seabedMesh);
                changed = true;
            }

            Object.DestroyImmediate(built);
            return changed;
        }

        // A child that only shows: a mesh with the material, without a collider or shadows.
        private static GameObject Child(Transform parent, string name, Material material)
        {
            var existing = parent.Find(name);
            var child = existing != null ? existing.gameObject : new GameObject(name);
            child.transform.SetParent(parent, false);
            child.transform.localPosition = Vector3.zero;
            child.transform.localRotation = Quaternion.identity;
            child.transform.localScale = Vector3.one;
            if (!child.TryGetComponent<MeshFilter>(out _))
            {
                child.AddComponent<MeshFilter>();
            }

            var renderer = child.TryGetComponent<MeshRenderer>(out var found) ? found : child.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return child;
        }

        private static bool Remove(Transform parent, string name)
        {
            var existing = parent.Find(name);
            if (existing == null)
            {
                return false;
            }

            Object.DestroyImmediate(existing.gameObject);
            return true;
        }

        // Whether the built mesh matches the existing one. It takes the existing mesh's name, so
        // copying it over the existing mesh keeps that name.
        private static bool SameMesh(Mesh built, Mesh existing)
        {
            built.name = existing.name;
            return EditorJsonUtility.ToJson(built) == EditorJsonUtility.ToJson(existing);
        }
    }
}
