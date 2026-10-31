using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.WorldData.Items.Presentation;
using UnityEngine;

namespace Mmorpg.Client.Gameplay
{
    // Presentation of another player, in the body model, look, and garments the server sends for
    // it. Its only collider is a runtime trigger capsule of the movement capsule's size, so the
    // pointer can pick the player; it never blocks anything.
    // With the body tint setting on, the body is tinted orange for a person and blue for a
    // development bot, so neither can be taken for the purple local player.
    [DisallowMultipleComponent]
    public sealed class RemotePlayerView : MonoBehaviour
    {
        private const string PointerTargetName = "PointerTargetBounds";

        private readonly RemoteMovementPresenter movement = new RemoteMovementPresenter();
        private CharacterLocomotionAnimator body;
        private int presentedDiscontinuities;

        public ulong NetworkEntityId { get; private set; }

        // The animated body of the player, or null without one. A body that left the player, as
        // when its body slot shows another model, is looked up again.
        public CharacterLocomotionAnimator Body
        {
            get
            {
                if (body == null || !body.transform.IsChildOf(transform))
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

        // The look the server sent last, or null before one arrived.
        public RealtimeCharacterAppearance Appearance { get; private set; }

        // Puts in the body of the model, the default for none or an unknown name, before the body
        // is combined and given its look and tint; true when it put in a new body.
        public bool ShowBody(string bodyModel)
        {
            var slot = GetComponentInChildren<PlayerBodySlot>(true);
            return slot != null && slot.Models != null && slot.Show(slot.Models.NamedOrDefault(bodyModel));
        }

        public void ApplyBodyTint(bool tinted, PlayerBodyMaterials materials)
        {
            var appearance = GetComponentInChildren<PlayerBodyAppearance>(true);
            if (appearance != null)
            {
                appearance.ApplyTint(tinted, PlayerBodyColors.ForRemote(ArchetypeId), materials);
            }
        }

        // Shows the look the server sent, dressed in the garments of the items others see.
        public void ApplyAppearance(
            RealtimeCharacterAppearance appearance,
            ItemPresentationCatalogIndex presentation,
            PlayerBodyMaterials materials)
        {
            Appearance = appearance;
            var body = GetComponentInChildren<PlayerBodyAppearance>(true);
            if (body != null)
            {
                body.ApplyLook(DressedLook(appearance, body.Catalog, presentation), materials);
            }
        }

        // The look of a character as others see it: its own look, its hair, and the garments of
        // its visible items, with its headwear as it wears it, holding the tool it uses.
        public static BodyLook DressedLook(
            RealtimeCharacterAppearance appearance,
            BodyAppearanceCatalog catalog,
            ItemPresentationCatalogIndex presentation)
        {
            return appearance == null
                ? BodyLook.Default
                : EquipmentGarments.Dress(
                    CharacterLooks.LookOf(appearance.Choices),
                    catalog,
                    CharacterLooks.EquipmentOf(appearance),
                    presentation,
                    appearance.HeadwearLowered,
                    appearance.ToolInUse);
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
            if (movement.Discontinuities != presentedDiscontinuities)
            {
                // The pose jumped: the loose cloth is carried along rather than swung by it.
                presentedDiscontinuities = movement.Discontinuities;
                ClothChainSolver.NotifyDiscontinuity(gameObject);
            }
        }
    }
}
