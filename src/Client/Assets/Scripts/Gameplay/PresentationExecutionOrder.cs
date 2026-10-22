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

        // Cloth chains move in LateUpdate, after the Animator posed the bodies.
        public const int Cloth = 60;

        public const int Camera = 100;

        public const int ScreenAnchoredInterface = 200;
    }
}
