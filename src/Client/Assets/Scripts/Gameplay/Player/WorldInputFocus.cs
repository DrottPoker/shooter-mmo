using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Mmorpg.Client.Gameplay
{
    // Tells world input whether the interface has the pointer or the keyboard. The pointer is
    // over interface, or held by it, so a click or scroll on a panel, or a click that puts down
    // an item carried on the pointer, never also moves the character or the camera. The keyboard
    // is held while the player types in a text field, so the arrow keys move the caret and not
    // the camera. Every World scene panel is uGUI, so the EventSystem knows the rest.
    public sealed class WorldInputFocus
    {
        private readonly List<Func<bool>> captures = new List<Func<bool>>();
        private readonly List<Func<bool>> keyboardCaptures = new List<Func<bool>>();

        // True while the interface takes every key, such as while a text field has the focus.
        public bool IsKeyboardHeldByInterface
        {
            get
            {
                foreach (var capture in keyboardCaptures)
                {
                    if (capture())
                    {
                        return true;
                    }
                }

                return false;
            }
        }

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

        // Holds the keyboard while the capture returns true.
        public void AddKeyboardCapture(Func<bool> capture)
        {
            if (capture != null && !keyboardCaptures.Contains(capture))
            {
                keyboardCaptures.Add(capture);
            }
        }

        public void RemoveKeyboardCapture(Func<bool> capture)
        {
            keyboardCaptures.Remove(capture);
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
