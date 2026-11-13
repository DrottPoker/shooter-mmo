using System;

namespace Mmorpg.Client.Gameplay
{
    // Who the local player is right now. It is empty until WorldSceneContext spawns the player of
    // a joined session, and empty again once that player or its World scene goes. The composition
    // root creates one and passes it to every component that needs the player, so nothing
    // searches the scene for it.
    public sealed class LocalPlayerSource
    {
        private LocalPlayerController player;

        // Raised when a player appears and when it goes away.
        public event Action Changed;

        // The spawned local player, or null.
        public LocalPlayerController Controller
        {
            // A destroyed player reads as none, also before its owner cleared it.
            get { return player != null ? player : null; }
        }

        // The input of the spawned local player, for the components that only read keys.
        public LocalPlayerInput Input
        {
            get
            {
                var controller = Controller;
                return controller != null ? controller.PlayerInput : null;
            }
        }

        public bool HasPlayer
        {
            get { return Controller != null; }
        }

        // Called by WorldSceneContext once the player it spawned has its input and camera.
        public void Set(LocalPlayerController controller)
        {
            if (controller == null)
            {
                throw new ArgumentNullException(nameof(controller));
            }

            if (ReferenceEquals(player, controller))
            {
                return;
            }

            player = controller;
            Changed?.Invoke();
        }

        // Clears the player only while it is still this one, so a scene that goes late never
        // clears the player of a newer one.
        public void Clear(LocalPlayerController controller)
        {
            if (ReferenceEquals(controller, null) || !ReferenceEquals(player, controller))
            {
                return;
            }

            player = null;
            Changed?.Invoke();
        }
    }
}
