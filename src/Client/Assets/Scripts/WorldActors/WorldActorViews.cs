using System;
using Mmorpg.GameProtocol;
using UnityEngine;

namespace Mmorpg.Client.WorldActors
{
    public interface IWorldInteractionTarget
    {
        RealtimeWorldInteractionTargetKind TargetKind { get; }
        ulong TargetEntityId { get; }
        Guid TargetRuntimeId { get; }
        long TargetRevision { get; }
        bool IsTargetActive { get; }
        string TargetDisplayName { get; }

        // Where the player walks to before it asks the worker to open the interaction.
        Vector3 TargetPosition { get; }
    }

    [DisallowMultipleComponent]
    public class WorldActorView : MonoBehaviour, IWorldInteractionTarget
    {
        private BoxCollider interactionCollider;

        public ulong TargetEntityId { get; private set; }
        public Guid TargetRuntimeId { get; private set; }
        public long TargetRevision { get; private set; }
        public bool IsTargetActive { get; private set; }
        public string TargetDisplayName { get; private set; } = string.Empty;
        public string PresentationArchetypeId { get; private set; } = string.Empty;

        public Vector3 TargetPosition
        {
            get { return transform.position; }
        }
        public RealtimeWorldInteractionTargetKind TargetKind
        {
            get { return RealtimeWorldInteractionTargetKind.WorldActor; }
        }

        public virtual void Initialize(WorldActorClientEntry actor)
        {
            if (actor == null)
            {
                throw new ArgumentNullException(nameof(actor));
            }

            TargetEntityId = actor.EntityId;
            TargetRuntimeId = actor.RuntimeActorId;
            TargetDisplayName = actor.DisplayName;
            PresentationArchetypeId = actor.PresentationArchetypeId;
            var boundsTransform = transform.Find("InteractionTargetBounds");
            if (boundsTransform == null)
            {
                var boundsObject = new GameObject("InteractionTargetBounds");
                boundsTransform = boundsObject.transform;
                boundsTransform.SetParent(transform, false);
            }

            interactionCollider = boundsTransform.GetComponent<BoxCollider>();
            if (interactionCollider == null)
            {
                interactionCollider = boundsTransform.gameObject.AddComponent<BoxCollider>();
            }

            interactionCollider.isTrigger = true;
            interactionCollider.center = new Vector3(
                actor.BoundsCenterX,
                actor.BoundsCenterY,
                actor.BoundsCenterZ);
            interactionCollider.size = new Vector3(
                actor.BoundsSizeX,
                actor.BoundsSizeY,
                actor.BoundsSizeZ);
            Apply(actor);
        }

        public virtual void Apply(WorldActorClientEntry actor)
        {
            if (actor == null
                || actor.EntityId != TargetEntityId
                || actor.RuntimeActorId != TargetRuntimeId)
            {
                throw new InvalidOperationException(
                    "World actor presentation cannot change runtime identity.");
            }

            TargetRevision = actor.InteractionRevision;
            IsTargetActive = actor.IsActive;
            transform.SetPositionAndRotation(
                new Vector3(actor.PositionX, actor.PositionY, actor.PositionZ),
                Quaternion.Euler(0f, actor.YawDegrees, 0f));
            if (interactionCollider != null)
            {
                interactionCollider.enabled = actor.IsActive;
            }

            ApplyActivity(actor.IsActive);
        }

        // An inactive actor, such as a dead Mob, is hidden until it returns.
        protected virtual void ApplyActivity(bool isActive)
        {
            gameObject.SetActive(isActive);
        }
    }

    public sealed class NpcView : WorldActorView
    {
    }

    public sealed class MobView : WorldActorView
    {
    }

    // A depleted resource node stays visible but cannot be targeted until it respawns. An
    // authored prefab provides an "Available" and a "Depleted" child; the development
    // fallback primitive shrinks instead.
    public sealed class ResourceNodeView : WorldActorView
    {
        public const string AvailableModelName = "Available";
        public const string DepletedModelName = "Depleted";
        private const float DepletedFallbackHeight = 0.35f;

        private Transform availableModel;
        private Transform depletedModel;
        private Vector3 fallbackScale;
        private bool resolved;

        public bool IsDepleted { get; private set; }

        protected override void ApplyActivity(bool isActive)
        {
            ResolveModels();
            IsDepleted = !isActive;
            gameObject.SetActive(true);
            if (availableModel != null && depletedModel != null)
            {
                availableModel.gameObject.SetActive(isActive);
                depletedModel.gameObject.SetActive(!isActive);
                return;
            }

            transform.localScale = isActive
                ? fallbackScale
                : new Vector3(fallbackScale.x, fallbackScale.y * DepletedFallbackHeight, fallbackScale.z);
        }

        private void ResolveModels()
        {
            if (resolved)
            {
                return;
            }

            resolved = true;
            availableModel = transform.Find(AvailableModelName);
            depletedModel = transform.Find(DepletedModelName);
            fallbackScale = transform.localScale;
        }
    }
}
