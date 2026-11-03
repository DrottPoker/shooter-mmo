using System.Collections.Generic;
using Mmorpg.Client.Worlds;
using UnityEditor;
using UnityEngine;

namespace Mmorpg.Client.Editor
{
    // Shapes a river with handles in the Scene view: click a point to move it and drag the cubes
    // on its banks to change its width there, Shift+click the river's line to add a point there or
    // a + past an end to lengthen it, and Ctrl+click a point to remove it. Every change can be
    // undone, and the World Builder rebuilds the river as the point moves.
    [CustomEditor(typeof(RiverShape))]
    internal sealed class RiverShapeEditor : UnityEditor.Editor
    {
        private const int MinimumPoints = 2;
        private const float BankSize = 0.06f;

        // The line is drawn through samples this far apart (m).
        private const float LineStep = 0.5f;

        private int selected = -1;

        private void OnEnable()
        {
            EditorApplication.modifierKeysChanged += SceneView.RepaintAll;
        }

        private void OnDisable()
        {
            EditorApplication.modifierKeysChanged -= SceneView.RepaintAll;
        }

        public override void OnInspectorGUI()
        {
            EditorGUILayout.HelpBox(
                "Click a point in the Scene view to move it, and drag the cubes on its banks to change the width "
                + "there. Shift+click the river's line to add a point there, or a + past an end to lengthen it, and "
                + "Ctrl+click a point to remove it. The river flows from its first point to its last and falls "
                + "evenly between them.",
                MessageType.None);
            DrawDefaultInspector();
        }

        private void OnSceneGUI()
        {
            WorldBuilderHandles.FollowMouse();
            var transform = ((RiverShape)target).transform;
            serializedObject.Update();
            var points = serializedObject.FindProperty("points");
            var count = points.arraySize;
            if (count < MinimumPoints)
            {
                return;
            }

            var world = new List<RiverPoint>(count);
            for (var index = 0; index < count; index++)
            {
                var point = Read(points.GetArrayElementAtIndex(index));
                point.Position = transform.TransformPoint(point.Position);
                world.Add(point);
            }

            var path = new RiverPath(world, serializedObject.FindProperty("curved").boolValue);
            var line = DrawRiver(path);
            var mode = WorldBuilderHandles.Mode;
            if (mode == WorldBuilderHandleMode.Remove)
            {
                for (var index = 0; index < count; index++)
                {
                    if (WorldBuilderHandles.RemoveButton(world[index].Position, count > MinimumPoints))
                    {
                        points.DeleteArrayElementAtIndex(index);
                        selected = -1;
                        serializedObject.ApplyModifiedProperties();
                        return;
                    }
                }

                return;
            }

            if (mode == WorldBuilderHandleMode.Add)
            {
                AddButtons(transform, points, world, path, line);
                return;
            }

            selected = Mathf.Min(selected, count - 1);
            for (var index = 0; index < count; index++)
            {
                if (index == selected)
                {
                    EditSelected(transform, points.GetArrayElementAtIndex(index), world[index], path, index);
                }
                else if (WorldBuilderHandles.PointButton(world[index].Position))
                {
                    selected = index;
                    Repaint();
                }
            }
        }

        // The line along the river's middle, and both banks. Returns the line.
        private static Vector3[] DrawRiver(RiverPath path)
        {
            var samples = Mathf.Max(2, Mathf.CeilToInt(path.Length / LineStep) + 1);
            var center = new Vector3[samples];
            var left = new Vector3[samples];
            var right = new Vector3[samples];
            for (var sample = 0; sample < samples; sample++)
            {
                var along = path.Length * sample / (samples - 1);
                var values = path.ValuesAt(along);
                var across = Across(path.DirectionAt(along)) * (values.Width * 0.5f);
                center[sample] = values.Position;
                left[sample] = values.Position - across;
                right[sample] = values.Position + across;
            }

            Handles.color = WorldBuilderHandles.LineColor;
            Handles.DrawAAPolyLine(WorldBuilderHandles.LineWidth, center);
            Handles.DrawAAPolyLine(1f, left);
            Handles.DrawAAPolyLine(1f, right);
            return center;
        }

        // The selected point moves with a position handle, and a cube on each bank sets its width.
        private void EditSelected(Transform transform, SerializedProperty element, RiverPoint point, RiverPath path, int index)
        {
            var rotation = Tools.pivotRotation == PivotRotation.Local ? transform.rotation : Quaternion.identity;
            EditorGUI.BeginChangeCheck();
            var moved = Handles.PositionHandle(point.Position, rotation);
            if (EditorGUI.EndChangeCheck())
            {
                element.FindPropertyRelative("Position").vector3Value = transform.InverseTransformPoint(moved);
                serializedObject.ApplyModifiedProperties();
                return;
            }

            var across = Across(path.DirectionAt(path.PointDistances[index]));
            foreach (var side in new[] { -1f, 1f })
            {
                var outward = across * side;
                var bank = point.Position + (outward * (point.Width * 0.5f));
                var size = HandleUtility.GetHandleSize(bank) * BankSize;
                Handles.color = WorldBuilderHandles.PointColor;
                EditorGUI.BeginChangeCheck();
                var dragged = Handles.Slider(bank, outward, size, Handles.CubeHandleCap, 0f);
                if (EditorGUI.EndChangeCheck())
                {
                    element.FindPropertyRelative("Width").floatValue =
                        Mathf.Max(0.5f, 2f * Vector3.Dot(dragged - point.Position, outward));
                    serializedObject.ApplyModifiedProperties();
                    return;
                }
            }
        }

        // A + where the river's line passes nearest the mouse adds a point there, like the river is
        // there, and a + past each end adds a point there like the end.
        private void AddButtons(Transform transform, SerializedProperty points, List<RiverPoint> world, RiverPath path, Vector3[] line)
        {
            var count = world.Count;
            var first = world[0];
            var last = world[count - 1];
            var before = first.Position - (Along(path.DirectionAt(0f)) * Mathf.Max(2f, first.Width));
            var after = last.Position + (Along(path.DirectionAt(path.Length)) * Mathf.Max(2f, last.Width));
            if (WorldBuilderHandles.AddButton(before))
            {
                first.Position = transform.InverseTransformPoint(before);
                Insert(points, 0, first);
            }
            else if (WorldBuilderHandles.AddButton(after))
            {
                last.Position = transform.InverseTransformPoint(after);
                Insert(points, count, last);
            }
            else if (WorldBuilderHandles.AddButtonOnLine(line, out var added))
            {
                path.DistanceTo(new Vector2(added.x, added.z), out var along);
                var index = 1;
                while (index < count - 1 && path.PointDistances[index] < along)
                {
                    index++;
                }

                var values = path.ValuesAt(along);
                values.Position = transform.InverseTransformPoint(values.Position);
                Insert(points, index, values);
            }
        }

        private void Insert(SerializedProperty points, int index, RiverPoint point)
        {
            points.InsertArrayElementAtIndex(index);
            Write(points.GetArrayElementAtIndex(index), point);
            selected = index;
            serializedObject.ApplyModifiedProperties();
        }

        // The direction of the flow in world space.
        private static Vector3 Along(Vector2 flow)
        {
            return new Vector3(flow.x, 0f, flow.y);
        }

        // The direction across the river in world space, to the right of the flow.
        private static Vector3 Across(Vector2 flow)
        {
            return new Vector3(flow.y, 0f, -flow.x);
        }

        private static RiverPoint Read(SerializedProperty element)
        {
            return new RiverPoint(
                element.FindPropertyRelative("Position").vector3Value,
                element.FindPropertyRelative("Width").floatValue,
                element.FindPropertyRelative("Depth").floatValue,
                element.FindPropertyRelative("Speed").floatValue,
                element.FindPropertyRelative("Foam").floatValue);
        }

        private static void Write(SerializedProperty element, RiverPoint point)
        {
            element.FindPropertyRelative("Position").vector3Value = point.Position;
            element.FindPropertyRelative("Width").floatValue = point.Width;
            element.FindPropertyRelative("Depth").floatValue = point.Depth;
            element.FindPropertyRelative("Speed").floatValue = point.Speed;
            element.FindPropertyRelative("Foam").floatValue = point.Foam;
        }
    }
}
