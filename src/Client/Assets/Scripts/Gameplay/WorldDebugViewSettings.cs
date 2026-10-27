using System;

namespace Mmorpg.Client.Gameplay
{
    // What the Debug menu draws in the world: the collision around the local player and the
    // path it follows. Both are off whenever the client starts; the composition root keeps them
    // across World scenes.
    public sealed class WorldDebugViewSettings
    {
        private bool showCollision;
        private bool showPath;

        public event Action Changed;

        public bool ShowCollision
        {
            get { return showCollision; }
            set
            {
                if (showCollision != value)
                {
                    showCollision = value;
                    Changed?.Invoke();
                }
            }
        }

        public bool ShowPath
        {
            get { return showPath; }
            set
            {
                if (showPath != value)
                {
                    showPath = value;
                    Changed?.Invoke();
                }
            }
        }
    }
}
