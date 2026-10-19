using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using UnityEngine;

namespace Mmorpg.Client.Gameplay
{
    // Presentation of another player. Its only collider is a runtime trigger capsule of the
    // movement capsule's size, so the pointer can pick the player; it never blocks anything.
    [DisallowMultipleComponent]
    public sealed class RemotePlayerView : MonoBehaviour
    {
        public const string SupportedArchetypeId = "player.default";
        private const string PointerTargetName = "PointerTargetBounds";

        private readonly RemoteMovementPresenter movement = new RemoteMovementPresenter();

        public ulong NetworkEntityId { get; private set; }

        public string PersistentId { get; private set; }

        public string DisplayName { get; private set; }

        public string ArchetypeId { get; private set; }

        // The height of the movement capsule above the root, the player's ground point.
        public float TargetHeight { get; private set; }

        public void Initialize(
            ulong networkEntityId,
            string persistentId,
            string displayName,
            string archetypeId,
            int simulationTickRateHz,
            int delayTicks,
            ICollisionWorld worldCollision,
            CharacterCollisionSettings characterCollision)
        {
            NetworkEntityId = networkEntityId;
            PersistentId = persistentId;
            DisplayName = displayName;
            ArchetypeId = archetypeId;
            movement.Configure(simulationTickRateHz, delayTicks, worldCollision, characterCollision);
            name = "RemotePlayer_" + networkEntityId + "_" + displayName;
            ConfigurePointerTarget(characterCollision);
        }

        public void PushSnapshot(uint serverTick, RealtimePlayerState state)
        {
            if (state == null || NetworkEntityId == 0)
            {
                return;
            }

            var hadPose = movement.HasPose;
            if (movement.Push(serverTick, RemoteMovementPresenter.ToMovementState(state)) && !hadPose)
            {
                ApplyPresentedPose();
            }
        }

        private void Update()
        {
            if (movement.Advance(Time.unscaledDeltaTime, Time.deltaTime))
            {
                ApplyPresentedPose();
            }
        }

        private void ConfigurePointerTarget(CharacterCollisionSettings characterCollision)
        {
            if (characterCollision == null)
            {
                TargetHeight = 0f;
                return;
            }

            TargetHeight = characterCollision.Height;
            var boundsTransform = transform.Find(PointerTargetName);
            if (boundsTransform == null)
            {
                boundsTransform = new GameObject(PointerTargetName).transform;
                boundsTransform.SetParent(transform, false);
            }

            var capsule = boundsTransform.GetComponent<CapsuleCollider>();
            if (capsule == null)
            {
                capsule = boundsTransform.gameObject.AddComponent<CapsuleCollider>();
            }

            capsule.isTrigger = true;
            capsule.direction = 1;
            capsule.radius = characterCollision.Radius;
            capsule.height = characterCollision.Height;
            capsule.center = new Vector3(0f, characterCollision.Height * 0.5f, 0f);
        }

        private void ApplyPresentedPose()
        {
            transform.SetPositionAndRotation(movement.Position, movement.Rotation);
        }
    }
}
