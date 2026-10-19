using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Mmorpg.Client.Gameplay
{
    // Tells world input whether the pointer is over interface, so a click or scroll on a
    // panel never also moves the character or the camera. uGUI panels are found through the
    // EventSystem, and immediate-mode panels report their screen rectangles while drawing.
    public sealed class WorldPointerFocus
    {
        private readonly List<Rect> reportedRects = new List<Rect>();
        private readonly List<Rect> previousFrameRects = new List<Rect>();
        private int reportingFrame = -1;

        // Called from OnGUI with a rectangle in GUI coordinates, whose origin is top left.
        public void ReportInterfaceRect(Rect guiRect)
        {
            BeginFrame(Time.frameCount);
            reportedRects.Add(guiRect);
        }

        public bool IsPointerOverInterface(Vector2 screenPosition)
        {
            if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject())
            {
                return true;
            }

            // OnGUI runs after Update, so input reads the rectangles of the previous frame.
            BeginFrame(Time.frameCount);
            var guiPoint = new Vector2(screenPosition.x, Screen.height - screenPosition.y);
            return Contains(reportedRects, guiPoint) || Contains(previousFrameRects, guiPoint);
        }

        private void BeginFrame(int frame)
        {
            if (frame == reportingFrame)
            {
                return;
            }

            previousFrameRects.Clear();
            if (frame == reportingFrame + 1)
            {
                previousFrameRects.AddRange(reportedRects);
            }

            reportedRects.Clear();
            reportingFrame = frame;
        }

        private static bool Contains(List<Rect> rects, Vector2 point)
        {
            for (var index = 0; index < rects.Count; index++)
            {
                if (rects[index].Contains(point))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
