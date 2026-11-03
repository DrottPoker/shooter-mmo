using UnityEditor;
using UnityEngine;

namespace Mmorpg.Client.Editor
{
    // Shapes a closed outline in the X and Z of a shape's transform with handles in the Scene
    // view: drag a point to move it, Shift+click the outline to add a point there, and Ctrl+click
    // a point to remove it. Every change can be undone, and the World Builder rebuilds what the
    // shape makes as the point moves.
    internal abstract class OutlineShapeEditor : UnityEditor.Editor
    {
        private const int MinimumPoints = 3;

        // The serialized List<Vector2> that holds the outline.
        protected abstract string OutlineProperty { get; }

        // What the hint above the shape's values says beyond how to shape the outline.
        protected abstract string Hint { get; }

        // Unity finds these messages by reflection on the derived editor, which sees no private
        // method of this base class.
        protected void OnEnable()
        {
            EditorApplication.modifierKeysChanged += SceneView.RepaintAll;
        }

        protected void OnDisable()
        {
            EditorApplication.modifierKeysChanged -= SceneView.RepaintAll;
        }

        public override void OnInspectorGUI()
        {
            EditorGUILayout.HelpBox(
                "Drag the points in the Scene view to shape the outline. Shift+click the outline to add a point "
                + "there, and Ctrl+click a point to remove it. " + Hint,
                MessageType.None);
            DrawDefaultInspector();
        }

        protected void OnSceneGUI()
        {
            WorldBuilderHandles.FollowMouse();
            var transform = ((Component)target).transform;
            serializedObject.Update();
            var outline = serializedObject.FindProperty(OutlineProperty);
            var count = outline.arraySize;
            if (count == 0)
            {
                return;
            }

            // The outline as a closed line in world space.
            var line = new Vector3[count + 1];
            for (var index = 0; index < count; index++)
            {
                var local = outline.GetArrayElementAtIndex(index).vector2Value;
                line[index] = transform.TransformPoint(new Vector3(local.x, 0f, local.y));
            }

            line[count] = line[0];
            Handles.color = WorldBuilderHandles.LineColor;
            Handles.DrawAAPolyLine(WorldBuilderHandles.LineWidth, line);

            var mode = WorldBuilderHandles.Mode;
            if (mode == WorldBuilderHandleMode.Remove)
            {
                for (var index = 0; index < count; index++)
                {
                    if (WorldBuilderHandles.RemoveButton(line[index], count > MinimumPoints))
                    {
                        outline.DeleteArrayElementAtIndex(index);
                        serializedObject.ApplyModifiedProperties();
                        return;
                    }
                }

                return;
            }

            if (mode == WorldBuilderHandleMode.Add)
            {
                if (WorldBuilderHandles.AddButtonOnLine(line, out var added))
                {
                    var local = transform.InverseTransformPoint(added);
                    var index = WorldBuilderHandles.NearestSegment(line, added) + 1;
                    outline.InsertArrayElementAtIndex(index);
                    outline.GetArrayElementAtIndex(index).vector2Value = new Vector2(local.x, local.z);
                    serializedObject.ApplyModifiedProperties();
                }

                return;
            }

            for (var index = 0; index < count; index++)
            {
                var size = HandleUtility.GetHandleSize(line[index]) * WorldBuilderHandles.PointSize;
                Handles.color = WorldBuilderHandles.PointColor;
                EditorGUI.BeginChangeCheck();
                var moved = Handles.Slider2D(
                    line[index],
                    transform.up,
                    transform.right,
                    transform.forward,
                    size,
                    Handles.DotHandleCap,
                    Vector2.zero);
                if (EditorGUI.EndChangeCheck())
                {
                    var local = transform.InverseTransformPoint(moved);
                    outline.GetArrayElementAtIndex(index).vector2Value = new Vector2(local.x, local.z);
                    serializedObject.ApplyModifiedProperties();
                }
            }
        }
    }
}
