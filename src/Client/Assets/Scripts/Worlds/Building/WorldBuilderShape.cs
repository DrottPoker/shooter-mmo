using System;
using UnityEngine;

namespace Mmorpg.Client.Worlds
{
    // A part of a World that the World Builder makes from a few authored values, such as a lake
    // from its shore and its level. In the Editor the World Builder rebuilds what the shapes of a
    // scene make whenever one of them changes; in the game a shape does nothing, and only what it
    // made takes part, such as a water body's surface. See docs/DEVELOPMENT_WORLDS.md under World
    // Builder.
    public abstract class WorldBuilderShape : MonoBehaviour
    {
#if UNITY_EDITOR
        // Raised in the Editor when a shape's values change, so the World Builder rebuilds. Its
        // listeners are Editor tools that live as long as the Editor's domain, so unlike the
        // client's static state it keeps them when Play Mode starts without a domain reload.
        public static event Action<WorldBuilderShape> Changed;

        // Tells the World Builder that the shape changed outside the Inspector, such as through a
        // handle in the Scene view.
        public void NotifyChanged()
        {
            Changed?.Invoke(this);
        }

        protected virtual void OnValidate()
        {
            NotifyChanged();
        }
#endif
    }
}
