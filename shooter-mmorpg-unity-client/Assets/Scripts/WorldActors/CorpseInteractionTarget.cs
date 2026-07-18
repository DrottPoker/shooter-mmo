using System;
using ShooterMmo.GameProtocol;
using ShooterMmo.Items;
using UnityEngine;

namespace ShooterMmo.WorldActors
{
    [DisallowMultipleComponent]
    public sealed class CorpseInteractionTarget : MonoBehaviour, IWorldInteractionTarget
    {
        public RealtimeWorldInteractionTargetKind TargetKind
        {
            get { return RealtimeWorldInteractionTargetKind.Corpse; }
        }

        public ulong TargetEntityId
        {
            get { return 0; }
        }

        public Guid TargetRuntimeId { get; private set; }

        public long TargetRevision
        {
            get { return 1; }
        }

        public bool IsTargetActive { get; private set; }

        public string TargetDisplayName { get; private set; } = string.Empty;

        public void Apply(CorpsePresenceEntry corpse)
        {
            if (corpse == null)
            {
                throw new ArgumentNullException(nameof(corpse));
            }

            TargetRuntimeId = corpse.CorpseId;
            TargetDisplayName = corpse.SourceDisplayName;
            IsTargetActive = true;
        }
    }
}
