using System.Collections.Generic;
using System.Linq;
using Mmorpg.Client.Worlds;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Mmorpg.Client.Editor
{
    // Rebuilds a scene's water a moment after one of its shapes changes, in the Inspector, through
    // a handle, by moving, adding, or removing it, or by undo, so the water follows the edit while
    // the shape is dragged. Only an edit rebuilds: opening a scene changes nothing, because a
    // rebuild writes only what differs.
    [InitializeOnLoad]
    internal static class WorldBuilderLive
    {
        // At most one rebuild this often while a shape is dragged (s).
        private const double Interval = 0.05d;

        private static readonly HashSet<Scene> pending = new HashSet<Scene>();
        private static double nextRebuild;

        static WorldBuilderLive()
        {
            WorldBuilderShape.Changed += OnShapeChanged;
            ObjectChangeEvents.changesPublished += OnChangesPublished;
            Undo.undoRedoPerformed += OnUndoRedo;
            EditorApplication.update += Update;
        }

        // Live rebuilding can be paused, such as while a builder makes many shapes at once.
        public static bool Paused { get; set; }

        private static bool Listening
        {
            get { return !Paused && !EditorApplication.isPlayingOrWillChangePlaymode; }
        }

        private static void OnShapeChanged(WorldBuilderShape shape)
        {
            // OnValidate also runs while a scene loads or a domain reloads, which are no edits.
            if (Listening && shape != null && !EditorApplication.isUpdating)
            {
                pending.Add(shape.gameObject.scene);
            }
        }

        // Moving a shape or an object above it changes what it makes, such as a lake's level,
        // without changing the shape's own values, and adding or removing one changes the water
        // it meets, such as a river's end where a waterfall falls from it. Moving the ground's
        // colliders moves the World's edge, where a sea runs on to the horizon.
        private static void OnChangesPublished(ref ObjectChangeEventStream stream)
        {
            if (!Listening)
            {
                return;
            }

            for (var index = 0; index < stream.length; index++)
            {
                switch (stream.GetEventType(index))
                {
                    case ObjectChangeKind.ChangeGameObjectOrComponentProperties:
                        stream.GetChangeGameObjectOrComponentPropertiesEvent(index, out var properties);
                        AddIfShaped(properties.entityId, properties.scene);
                        break;
                    case ObjectChangeKind.CreateGameObjectHierarchy:
                        stream.GetCreateGameObjectHierarchyEvent(index, out var created);
                        AddIfShaped(created.entityId, created.scene);
                        break;
                    case ObjectChangeKind.ChangeGameObjectParent:
                        stream.GetChangeGameObjectParentEvent(index, out var parent);
                        AddIfShaped(parent.entityId, parent.newScene);
                        pending.Add(parent.previousScene);
                        break;

                    // What was removed is gone, so the scene it left rebuilds.
                    case ObjectChangeKind.DestroyGameObjectHierarchy:
                        stream.GetDestroyGameObjectHierarchyEvent(index, out var destroyed);
                        pending.Add(destroyed.scene);
                        break;
                    case ObjectChangeKind.ChangeGameObjectStructure:
                        stream.GetChangeGameObjectStructureEvent(index, out var structure);
                        pending.Add(structure.scene);
                        break;
                    case ObjectChangeKind.ChangeGameObjectStructureHierarchy:
                        stream.GetChangeGameObjectStructureHierarchyEvent(index, out var hierarchy);
                        pending.Add(hierarchy.scene);
                        break;
                }
            }
        }

        private static void AddIfShaped(EntityId entityId, Scene scene)
        {
            var changed = EditorUtility.EntityIdToObject(entityId);
            var owner = changed is GameObject gameObject ? gameObject : changed is Component component ? component.gameObject : null;
            if (owner != null
                && (owner.GetComponentInChildren<WorldBuilderShape>(true) != null || owner.GetComponentInChildren<Collider>(true) != null))
            {
                pending.Add(scene);
            }
        }

        private static void OnUndoRedo()
        {
            if (!Listening)
            {
                return;
            }

            for (var index = 0; index < SceneManager.sceneCount; index++)
            {
                pending.Add(SceneManager.GetSceneAt(index));
            }
        }

        private static void Update()
        {
            if (pending.Count == 0 || Paused || EditorApplication.timeSinceStartup < nextRebuild)
            {
                return;
            }

            var scenes = pending.ToArray();
            pending.Clear();
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return;
            }

            nextRebuild = EditorApplication.timeSinceStartup + Interval;
            foreach (var scene in scenes)
            {
                if (scene.IsValid() && scene.isLoaded && WorldBuilder.Shapes<WorldBuilderShape>(scene).Count > 0)
                {
                    WorldBuilder.Rebuild(scene);
                }
            }
        }
    }
}
