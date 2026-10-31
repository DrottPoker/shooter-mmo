namespace Mmorpg.Client.Gameplay
{
    // The fixed order of a frame's late presentation. Characters move in Update, the camera
    // follows them in LateUpdate, and whatever is placed on screen at a world position, such as
    // a health bar over a head, is placed after the camera moved. Without a fixed order Unity
    // may place it with the previous frame's camera, and it trails the character.
    public static class PresentationExecutionOrder
    {
        // Character animation reads how far its character moved this frame, after every
        // character moved and before the Animator evaluates.
        public const int Locomotion = 50;

        // Projectiles and the arrows on archers' strings follow the weapons in LateUpdate, after the
        // bodies posed their hands and drew their strings there.
        public const int Projectiles = 60;

        public const int Camera = 100;

        // Cloth chains move in LateUpdate, after the Animator posed the bodies and the camera moved,
        // so the cloth is laid for the bodies this frame's camera sees.
        public const int Cloth = 110;

        public const int ScreenAnchoredInterface = 200;
    }
}
