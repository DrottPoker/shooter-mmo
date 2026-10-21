using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using UnityEngine;

namespace Mmorpg.Client.Gameplay
{
    // Presentation of another player. Its only collider is a runtime trigger capsule of the
    // movement capsule's size, so the pointer can pick the player; it never blocks anything.
    // With the body tint setting on, the body is tinted orange for a person and blue for a
    // development bot, so neither can be taken for the purple local player.
    [DisallowMultipleComponent]
    public sealed class RemotePlayerView : MonoBehaviour
    {
        private const string PointerTargetName = "PointerTargetBounds";

        private readonly RemoteMovementPresenter movement = new RemoteMovementPresenter();
        private CharacterLocomotionAnimator body;

        public ulong NetworkEntityId { get; private set; }

        // The animated body of the player, or null without one.
        public CharacterLocomotionAnimator Body
        {
            get
            {
                if (body == null)
                {
                    body = GetComponentInChildren<CharacterLocomotionAnimator>(true);
                }

                return body;
            }
        }

        public string PersistentId { get; private set; }

        public string DisplayName { get; private set; }

        public string ArchetypeId { get; private set; }

        // The height of the movement capsule above the root, the player's ground point.
        public float TargetHeight { get; private set; }

        public static bool SupportsArchetype(string archetypeId)
        {
            return string.Equals(archetypeId, RealtimePlayerArchetypeIds.Player, System.StringComparison.Ordinal)
                || string.Equals(archetypeId, RealtimePlayerArchetypeIds.DevelopmentBot, System.StringComparison.Ordinal);
        }

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

        public void ApplyBodyTint(bool tinted, PlayerBodyMaterials materials)
        {
            var appearance = GetComponentInChildren<PlayerBodyAppearance>(true);
            if (appearance != null)
            {
                appearance.ApplyTint(tinted, PlayerBodyColors.ForRemote(ArchetypeId), materials);
            }
        }

        // Every player shows the default look until looks are saved with characters and sent to
        // other clients; a development bot shows a look of its own, picked from its persistent
        // id, so a crowd of bots shows every face and eye color.
        public void ApplyBodyLook(PlayerBodyMaterials materials)
        {
            var appearance = GetComponentInChildren<PlayerBodyAppearance>(true);
            if (appearance != null)
            {
                appearance.ApplyLook(LookFor(ArchetypeId, PersistentId, appearance.Catalog), materials);
            }
        }

        public static BodyLook LookFor(string archetypeId, string persistentId, BodyAppearanceCatalog catalog)
        {
            return string.Equals(archetypeId, RealtimePlayerArchetypeIds.DevelopmentBot, System.StringComparison.Ordinal)
                ? BodyLook.Varied(persistentId, catalog)
                : BodyLook.Default;
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
