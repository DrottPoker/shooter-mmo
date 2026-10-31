using System;
using System.Collections.Generic;
using UnityEngine;

namespace Mmorpg.Client.Gameplay
{
    // The remains a player leaves: the dead character's body in the last pose of the Death clip, in
    // its look and the garments still in the corpse's equipment, baked once per body model and look
    // into a static mesh that every corpse that looks the same shares, so a field of corpses costs
    // no animation or skinning. The owner destroys the meshes; the materials come from the shared
    // set.
    public sealed class PlayerCorpseBodies : IDisposable
    {
        // The presentation key of every player corpse, as the worker sends it.
        public const string PresentationKey = "corpse.remains";

        // A body that shows its character dead this close to a corpse is the one that left it; the
        // corpse stays hidden under it until the character respawns.
        public const float OwnBodyRadius = 1.5f;

        private static readonly int DeathStateId = Animator.StringToHash(HumanLocomotion.DeathState);

        private readonly Dictionary<string, Baked> baked = new Dictionary<string, Baked>(StringComparer.Ordinal);

        // How many lying bodies are baked.
        public int Count
        {
            get { return baked.Count; }
        }

        public static bool IsPlayerCorpse(string presentationKey)
        {
            return string.Equals(presentationKey, PresentationKey, StringComparison.Ordinal);
        }

        // The key of a lying body: its model's name and its look.
        public static string KeyOf(string bodyModel, BodyLook look)
        {
            return (bodyModel ?? string.Empty) + "|" + (look ?? BodyLook.Default).Key;
        }

        // The lying body of the body prefab in the look and its materials from the shared set, baked
        // on the first call for the key.
        public bool TryGet(string key, PlayerBodyAppearance bodyPrefab, BodyLook look, PlayerBodyMeshes meshes,
            PlayerBodyMaterials bodyMaterials, out Mesh lyingMesh, out Material[] lyingMaterials)
        {
            if (!baked.TryGetValue(key ?? string.Empty, out var body)
                && bodyPrefab != null && meshes != null && bodyMaterials != null)
            {
                body = Bake(bodyPrefab, look ?? BodyLook.Default, meshes, bodyMaterials);
                if (body.Mesh != null)
                {
                    baked[key ?? string.Empty] = body;
                }
            }

            lyingMesh = body.Mesh;
            lyingMaterials = body.Materials;
            return lyingMesh != null;
        }

        // Destroys the lying bodies no corpse shows any more.
        public void KeepOnly(ICollection<string> keys)
        {
            var unused = new List<string>();
            foreach (var key in baked.Keys)
            {
                if (keys == null || !keys.Contains(key))
                {
                    unused.Add(key);
                }
            }

            foreach (var key in unused)
            {
                DestroyMesh(baked[key].Mesh);
                baked.Remove(key);
            }
        }

        public void Dispose()
        {
            foreach (var body in baked.Values)
            {
                DestroyMesh(body.Mesh);
            }

            baked.Clear();
        }

        // A hidden copy of the body plays the end of Death, and its combined renderer is baked in
        // the body's own space, so the mesh lies where the dying body lay.
        private static Baked Bake(PlayerBodyAppearance bodyPrefab, BodyLook look, PlayerBodyMeshes meshes,
            PlayerBodyMaterials bodyMaterials)
        {
            var body = UnityEngine.Object.Instantiate(bodyPrefab.gameObject);
            body.hideFlags = HideFlags.HideAndDontSave;
            try
            {
                var appearance = body.GetComponent<PlayerBodyAppearance>();
                appearance.CombineParts(meshes);
                // The underwear is paint over the skin, which only a look draws.
                appearance.ApplyLook(look, bodyMaterials);
                var animator = body.GetComponent<Animator>();
                if (animator == null || appearance.CombinedRenderer == null)
                {
                    return default;
                }

                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                animator.Play(DeathStateId, 0, 1f);
                animator.Update(0f);
                var mesh = new Mesh { name = "PlayerCorpseBody" };
                appearance.CombinedRenderer.BakeMesh(mesh, true);
                mesh.RecalculateBounds();
                return new Baked(mesh, appearance.CombinedRenderer.sharedMaterials);
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

        private static void DestroyMesh(Mesh mesh)
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
        }

        private readonly struct Baked
        {
            public Baked(Mesh mesh, Material[] materials)
            {
                Mesh = mesh;
                Materials = materials;
            }

            public Mesh Mesh { get; }

            public Material[] Materials { get; }
        }
    }
}
