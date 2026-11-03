using UnityEditor;
using UnityEngine;

namespace Mmorpg.Client.Editor
{
    internal enum WorldBuilderHandleMode
    {
        Move,
        Add,
        Remove
    }

    // The look and the shared buttons of the World Builder's handles in the Scene view: points
    // to drag, while Shift is held a + on the line under the mouse to add a point there, and while
    // Ctrl is held a point to click to remove it.
    internal static class WorldBuilderHandles
    {
        public const float LineWidth = 3f;

        // The size of a point's handle, and of a +, as a part of the Scene view's handle size.
        public const float PointSize = 0.08f;
        public const float AddSize = 0.06f;

        // How close to a line the mouse shows a + on it and clicks it (pixels), and the same as a
        // part of the Scene view's handle size, which is 80 pixels.
        private const float AddReach = 24f;
        private const float AddPickSize = AddReach / 80f;

        public static readonly Color LineColor = new Color(0.25f, 0.85f, 1f, 0.9f);
        public static readonly Color PointColor = new Color(0.25f, 0.85f, 1f, 1f);
        public static readonly Color AddColor = new Color(0.35f, 1f, 0.45f, 1f);
        public static readonly Color RemoveColor = new Color(1f, 0.3f, 0.25f, 1f);
        public static readonly Color KeptColor = new Color(0.6f, 0.6f, 0.6f, 1f);

        private static WorldBuilderHandleMode mode;

        // What a click does: add a point while Shift is held, remove one while Ctrl, or Cmd on a
        // Mac, is held, and otherwise move one. The mode only changes while no handle is held, so
        // a click or a drag ends in the mode it started in.
        public static WorldBuilderHandleMode Mode
        {
            get
            {
                if (GUIUtility.hotControl == 0)
                {
                    mode = EditorGUI.actionKey ? WorldBuilderHandleMode.Remove
                        : Event.current.shift ? WorldBuilderHandleMode.Add
                        : WorldBuilderHandleMode.Move;
                }

                return mode;
            }
        }

        // Repaints the Scene view as the mouse moves while points are added, so the + follows it.
        public static void FollowMouse()
        {
            if (Event.current.type == EventType.MouseMove && Event.current.shift)
            {
                HandleUtility.Repaint();
            }
        }

        // A point to click to remove it; a point that the shape needs to keep shows grey.
        public static bool RemoveButton(Vector3 position, bool removable)
        {
            var size = HandleUtility.GetHandleSize(position) * PointSize;
            Handles.color = removable ? RemoveColor : KeptColor;
            return Handles.Button(position, Quaternion.identity, size, size * 1.5f, Handles.DotHandleCap) && removable;
        }

        // A point to click, such as to select it.
        public static bool PointButton(Vector3 position)
        {
            var size = HandleUtility.GetHandleSize(position) * PointSize;
            Handles.color = PointColor;
            return Handles.Button(position, Quaternion.identity, size, size * 1.5f, Handles.DotHandleCap);
        }

        // A + to click to add a point there.
        public static bool AddButton(Vector3 position)
        {
            return AddButton(position, HandleUtility.GetHandleSize(position) * AddSize * 1.5f);
        }

        // A + on the line where it passes nearest the mouse, if it passes near it, so a click
        // anywhere near the line adds a point there: returns whether it was clicked, and the point.
        public static bool AddButtonOnLine(Vector3[] line, out Vector3 point)
        {
            point = Vector3.zero;
            if (line.Length < 2 || HandleUtility.DistanceToPolyLine(line) > AddReach)
            {
                return false;
            }

            point = HandleUtility.ClosestPointToPolyLine(line);
            return AddButton(point, HandleUtility.GetHandleSize(point) * AddPickSize);
        }

        private static bool AddButton(Vector3 position, float pickSize)
        {
            var size = HandleUtility.GetHandleSize(position) * AddSize;
            Handles.color = AddColor;
            return Handles.Button(position, Quaternion.identity, size, pickSize, PlusHandleCap);
        }

        // The segment of the line from point index to index + 1 that passes nearest the point.
        public static int NearestSegment(Vector3[] line, Vector3 point)
        {
            var nearest = 0;
            var best = float.MaxValue;
            for (var index = 0; index < line.Length - 1; index++)
            {
                var distance = HandleUtility.DistancePointLine(point, line[index], line[index + 1]);
                if (distance < best)
                {
                    best = distance;
                    nearest = index;
                }
            }

            return nearest;
        }

        // A + that faces the Scene view's camera.
        private static void PlusHandleCap(int controlId, Vector3 position, Quaternion rotation, float size, EventType eventType)
        {
            if (eventType == EventType.Layout)
            {
                HandleUtility.AddControl(controlId, HandleUtility.DistanceToCircle(position, size));
            }
            else if (eventType == EventType.Repaint && Camera.current != null)
            {
                var camera = Camera.current.transform;
                Handles.DrawLine(position - (camera.right * size), position + (camera.right * size), LineWidth);
                Handles.DrawLine(position - (camera.up * size), position + (camera.up * size), LineWidth);
            }
        }
    }
}
