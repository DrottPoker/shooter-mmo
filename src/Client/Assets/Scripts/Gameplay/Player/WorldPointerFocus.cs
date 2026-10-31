using UnityEngine;
using UnityEngine.EventSystems;

namespace Mmorpg.Client.Gameplay
{
    // Tells world input whether the pointer is over interface, so a click or scroll on a
    // panel never also moves the character or the camera. Every World scene panel is uGUI, so
    // the EventSystem knows.
    public sealed class WorldPointerFocus
    {
        public bool IsPointerOverInterface(Vector2 screenPosition)
        {
            return EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
        }
    }
}
