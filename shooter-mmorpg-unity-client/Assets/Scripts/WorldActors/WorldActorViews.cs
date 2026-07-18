using System;
using ShooterMmo.GameProtocol;
using UnityEngine;

namespace ShooterMmo.WorldActors
{
    public interface IWorldInteractionTarget
    {
        RealtimeWorldInteractionTargetKind TargetKind { get; }
        ulong TargetEntityId { get; }
        Guid TargetRuntimeId { get; }
        long TargetRevision { get; }
        bool IsTargetActive { get; }
        string TargetDisplayName { get; }
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

            gameObject.SetActive(actor.IsActive);
        }
    }

    public sealed class NpcView : WorldActorView
    {
    }

    public sealed class MobView : WorldActorView
    {
    }
}
