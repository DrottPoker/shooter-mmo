using System;
using System.Collections.Generic;
using UnityEngine;

namespace Mmorpg.Client.Gameplay
{
    // The content of a garment or an attachment, which loads only while a look in view shows it: a
    // garment's model for one body with the material of its texture, or an attachment's model with
    // the clips of a skinned one, such as a bow's Draw, and the rotation each clip leaves its bones
    // in at its end. MMORPG > Tools > Art Pipeline > Update Appearance Catalogs writes one per
    // garment and body and one per attachment, each an Addressables entry of its own that the
    // appearance catalog names by address; see docs/UNITY_CLIENT_ARCHITECTURE.md, Body Content.
    public sealed class BodyPieceContent : ScriptableObject
    {
        [SerializeField] private GameObject model;
        [SerializeField] private Material material;
        [SerializeField] private AnimationClip[] clips = Array.Empty<AnimationClip>();
        [SerializeField] private EndRotation[] endRotations = Array.Empty<EndRotation>();

        // A garment's model for its body, with its renderer and the body's armature, or an
        // attachment's model, whose root object Grip lies on the socket bone.
        public GameObject Model
        {
            get { return model; }
        }

        // A garment's texture material, which the model draws with instead of its own; null for an
        // attachment, which draws with its model's materials.
        public Material Material
        {
            get { return material; }
        }

        // The clips of a skinned attachment, by name.
        public IReadOnlyList<AnimationClip> Clips
        {
            get { return clips ?? Array.Empty<AnimationClip>(); }
        }

        // The local rotation every bone that a clip turns has at the clip's end, which a player reads
        // without sampling the clip: a player samples no clip outside an Animator.
        public IReadOnlyList<EndRotation> EndRotations
        {
            get { return endRotations ?? Array.Empty<EndRotation>(); }
        }

        public void Configure(GameObject pieceModel, Material pieceMaterial, AnimationClip[] pieceClips,
            EndRotation[] pieceEndRotations = null)
        {
            model = pieceModel;
            material = pieceMaterial;
            clips = pieceClips ?? Array.Empty<AnimationClip>();
            endRotations = pieceEndRotations ?? Array.Empty<EndRotation>();
        }

        public AnimationClip ClipNamed(string clipName)
        {
            foreach (var clip in Clips)
            {
                if (clip != null && string.Equals(clip.name, clipName, StringComparison.Ordinal))
                {
                    return clip;
                }
            }

            return null;
        }

        // The local rotation the clip leaves the bone in at its end; false when the clip does not turn
        // the bone or the content has no such clip.
        public bool TryGetEndRotation(string clipName, string boneName, out Quaternion rotation)
        {
            foreach (var end in EndRotations)
            {
                if (end != null && string.Equals(end.Clip, clipName, StringComparison.Ordinal)
                    && string.Equals(end.Bone, boneName, StringComparison.Ordinal))
                {
                    rotation = end.Rotation;
                    return true;
                }
            }

            rotation = Quaternion.identity;
            return false;
        }

        // A bone of the model that a clip turns, with its local rotation at the clip's end.
        [Serializable]
        public sealed class EndRotation
        {
            [SerializeField] private string clip;
            [SerializeField] private string bone;
            [SerializeField] private Quaternion rotation = Quaternion.identity;

            public EndRotation(string clip, string bone, Quaternion rotation)
            {
                this.clip = clip ?? string.Empty;
                this.bone = bone ?? string.Empty;
                this.rotation = rotation;
            }

            public string Clip
            {
                get { return clip ?? string.Empty; }
            }

            public string Bone
            {
                get { return bone ?? string.Empty; }
            }

            public Quaternion Rotation
            {
                get { return rotation; }
            }
        }
    }
}
