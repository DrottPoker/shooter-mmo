using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Mmorpg.Client.Gameplay
{
    // Tells world input whether the pointer is over interface, or held by it, so a click or
    // scroll on a panel, or a click that puts down an item carried on the pointer, never also
    // moves the character or the camera. Every World scene panel is uGUI, so the EventSystem
    // knows the rest.
    public sealed class WorldPointerFocus
    {
        private readonly List<Func<bool>> captures = new List<Func<bool>>();

        // Holds the pointer while the capture returns true, such as while an item rides on it.
        public void AddCapture(Func<bool> capture)
        {
            if (capture != null && !captures.Contains(capture))
            {
                captures.Add(capture);
            }
        }

        public void RemoveCapture(Func<bool> capture)
        {
            captures.Remove(capture);
        }

        public bool IsPointerOverInterface(Vector2 screenPosition)
        {
            foreach (var capture in captures)
            {
                if (capture())
                {
                    return true;
                }
            }

            return EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
        }
    }
}
