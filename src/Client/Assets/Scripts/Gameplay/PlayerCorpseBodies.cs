using System;
using UnityEngine;

namespace Mmorpg.Client.Gameplay
{
    // The remains a player leaves: the body in the last pose of the Death clip and in the default
    // look, with its underwear, baked once into a static mesh that every player corpse shares, so a
    // field of corpses costs no animation or skinning. The owner destroys the mesh; the materials
    // come from the shared set.
    public sealed class PlayerCorpseBodies : IDisposable
    {
        // The presentation key of every player corpse, as the worker sends it.
        public const string PresentationKey = "corpse.remains";

        // A body that shows its character dead this close to a corpse is the one that left it; the
        // corpse stays hidden under it until the character respawns.
        public const float OwnBodyRadius = 1.5f;

        private static readonly int DeathStateId = Animator.StringToHash(HumanLocomotion.DeathState);

        private Mesh mesh;
        private Material[] materials;

        public static bool IsPlayerCorpse(string presentationKey)
        {
            return string.Equals(presentationKey, PresentationKey, StringComparison.Ordinal);
        }

        // The lying body of the body prefab and its materials from the shared set, baked on the
        // first call.
        public bool TryGet(PlayerBodyAppearance bodyPrefab, PlayerBodyMeshes meshes, PlayerBodyMaterials bodyMaterials,
            out Mesh lyingMesh, out Material[] lyingMaterials)
        {
            if (mesh == null && bodyPrefab != null && meshes != null && bodyMaterials != null)
            {
                Bake(bodyPrefab, meshes, bodyMaterials);
            }

            lyingMesh = mesh;
            lyingMaterials = materials;
            return mesh != null;
        }

        public void Dispose()
        {
            if (mesh == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(mesh);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(mesh);
            }

            mesh = null;
        }

        // A hidden copy of the body plays the end of Death, and its combined renderer is baked in
        // the body's own space, so the mesh lies where the dying body lay.
        private void Bake(PlayerBodyAppearance bodyPrefab, PlayerBodyMeshes meshes, PlayerBodyMaterials bodyMaterials)
        {
            var body = UnityEngine.Object.Instantiate(bodyPrefab.gameObject);
            body.hideFlags = HideFlags.HideAndDontSave;
            try
            {
                var appearance = body.GetComponent<PlayerBodyAppearance>();
                appearance.CombineParts(meshes);
                // The underwear is paint over the skin, which only a look draws.
                appearance.ApplyLook(BodyLook.Default, bodyMaterials);
                var animator = body.GetComponent<Animator>();
                if (animator == null || appearance.CombinedRenderer == null)
                {
                    return;
                }

                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                animator.Play(DeathStateId, 0, 1f);
                animator.Update(0f);
                var baked = new Mesh { name = "PlayerCorpseBody" };
                appearance.CombinedRenderer.BakeMesh(baked, true);
                baked.RecalculateBounds();
                mesh = baked;
                materials = appearance.CombinedRenderer.sharedMaterials;
            }
            finally
            {
                // Destroy waits for the end of the frame; the copy must not draw meanwhile.
                body.SetActive(false);
                if (Application.isPlaying)
                {
                    UnityEngine.Object.Destroy(body);
                }
                else
                {
                    UnityEngine.Object.DestroyImmediate(body);
                }
            }
        }
    }
}
