using System;
using System.Collections.Generic;
using UnityEngine;

namespace Mmorpg.Client.Gameplay
{
    // The content of a garment or an attachment, which loads only while a look in view shows it: a
    // garment's model for one body with the material of its texture, or an attachment's model with
    // the clips of a skinned one, such as a bow's Draw. MMORPG > Tools > Art Pipeline > Update
    // Appearance Catalogs writes one per garment and body and one per attachment, each an
    // Addressables entry of its own that the appearance catalog names by address; see
    // docs/UNITY_CLIENT_ARCHITECTURE.md, Body Content.
    public sealed class BodyPieceContent : ScriptableObject
    {
        [SerializeField] private GameObject model;
        [SerializeField] private Material material;
        [SerializeField] private AnimationClip[] clips = Array.Empty<AnimationClip>();

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

        public void Configure(GameObject pieceModel, Material pieceMaterial, AnimationClip[] pieceClips)
        {
            model = pieceModel;
            material = pieceMaterial;
            clips = pieceClips ?? Array.Empty<AnimationClip>();
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
    }
}
