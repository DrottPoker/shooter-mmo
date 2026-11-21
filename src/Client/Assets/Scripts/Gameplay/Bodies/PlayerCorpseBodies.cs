using System;
using System.Collections.Generic;
using UnityEngine;

namespace Mmorpg.Client.Gameplay
{
    // The remains a player leaves: the dead character's body in the last pose of the Death clip with
    // closed eyes, in its look and the garments still in the corpse's equipment, baked once per body
    // model and look into a static mesh that every corpse that looks the same shares, so a field of
    // corpses costs no animation or skinning. A bake reads the combined mesh of a hidden body back,
    // so it takes it from a set of readable meshes, which the hidden body releases when it goes. The
    // owner destroys the lying meshes; their materials come from the shared set, which counts every
    // lying body as a user until it goes. A body is baked only once the content of its look is
    // loaded, and a lying body holds that content while it exists, so it shows the body as it died.
    public sealed class PlayerCorpseBodies : IDisposable
    {
        // The presentation key of every player corpse, as the worker sends it.
        public const string PresentationKey = "corpse.remains";

        // A body that shows its character dead this close to a corpse is the one that left it; the
        // corpse stays hidden under it until the character respawns.
        public const float OwnBodyRadius = 1.5f;

        private static readonly int DeathStateId = Animator.StringToHash(HumanLocomotion.DeathState);

        private readonly Dictionary<string, Baked> baked = new Dictionary<string, Baked>(StringComparer.Ordinal);
        // The content of the looks that wait to be baked, by key.
        private readonly Dictionary<string, PlayerBodyContent.Needs> waiting =
            new Dictionary<string, PlayerBodyContent.Needs>(StringComparer.Ordinal);

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
        // on the first call for the key from readable meshes, which a set that keeps no copy of its
        // uploaded meshes cannot give.
        public bool TryGet(string key, PlayerBodyAppearance bodyPrefab, BodyLook look, PlayerBodyMeshes meshes,
            PlayerBodyMaterials bodyMaterials, out Mesh lyingMesh, out Material[] lyingMaterials)
        {
            return TryGet(key, bodyPrefab, look, meshes, bodyMaterials, out lyingMesh, out lyingMaterials, out _);
        }

        // As TryGet, and false with loading while the content of the look loads, which the meshes'
        // content then loads; ask again in a later frame.
        public bool TryGet(string key, PlayerBodyAppearance bodyPrefab, BodyLook look, PlayerBodyMeshes meshes,
            PlayerBodyMaterials bodyMaterials, out Mesh lyingMesh, out Material[] lyingMaterials, out bool loading)
        {
            key = key ?? string.Empty;
            loading = false;
            if (!baked.TryGetValue(key, out var body)
                && bodyPrefab != null && meshes != null && bodyMaterials != null)
            {
                look = look ?? BodyLook.Default;
                var content = meshes.Content;
                PlayerBodyContent.Needs needs = null;
                if (content != null)
                {
                    if (!waiting.TryGetValue(key, out needs))
                    {
                        needs = PlayerBodyContent.NeedsOf(bodyPrefab.Catalog, look);
                        waiting[key] = needs;
                    }

                    if (!content.Want(needs))
                    {
                        loading = true;
                        lyingMesh = null;
                        lyingMaterials = null;
                        return false;
                    }

                    waiting.Remove(key);
                }

                body = Bake(bodyPrefab, look, meshes, bodyMaterials);
                if (body.Mesh != null)
                {
                    content?.AddUser(needs);
                    body = new Baked(body.Mesh, body.Materials, body.MaterialSet, content, needs);
                    baked[key] = body;
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
                Forget(baked[key]);
                baked.Remove(key);
            }

            waiting.Clear();
        }

        public void Dispose()
        {
            foreach (var body in baked.Values)
            {
                Forget(body);
            }

            baked.Clear();
            waiting.Clear();
        }

        private static void Forget(Baked body)
        {
            DestroyMesh(body.Mesh);
            if (body.MaterialSet != null && body.Materials != null)
            {
                foreach (var material in body.Materials)
                {
                    body.MaterialSet.Release(material);
                }
            }

            body.Content?.Release(body.Needs);
        }

        // A hidden copy of the body plays the end of Death and closes its lids, and its combined
        // renderer is baked in the body's own space, so the mesh lies where the dying body lay.
        private static Baked Bake(PlayerBodyAppearance bodyPrefab, BodyLook look, PlayerBodyMeshes meshes,
            PlayerBodyMaterials bodyMaterials)
        {
            var body = UnityEngine.Object.Instantiate(bodyPrefab.gameObject);
            body.hideFlags = HideFlags.HideAndDontSave;
            var appearance = body.GetComponent<PlayerBodyAppearance>();
            try
            {
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
                // A corpse lies with closed eyes.
                appearance.Eyelids?.Show(1f);
                var mesh = new Mesh { name = "PlayerCorpseBody" };
                appearance.CombinedRenderer.BakeMesh(mesh, true);
                mesh.RecalculateBounds();
                var materials = appearance.CombinedRenderer.sharedMaterials;
                foreach (var material in materials)
                {
                    bodyMaterials.AddUser(material);
                }

                return new Baked(mesh, materials, bodyMaterials);
            }
            finally
            {
                // The copy lets go of its mesh, materials, and content at once, also out of play,
                // where it gets no OnDestroy.
                appearance.ReleaseShared();

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
            public Baked(Mesh mesh, Material[] materials, PlayerBodyMaterials materialSet,
                PlayerBodyContent content = null, PlayerBodyContent.Needs needs = null)
            {
                Mesh = mesh;
                Materials = materials;
                MaterialSet = materialSet;
                Content = content;
                Needs = needs;
            }

            // The content of the look, which the lying body holds while it exists.
            public PlayerBodyContent Content { get; }

            public PlayerBodyContent.Needs Needs { get; }

            public Mesh Mesh { get; }

            public Material[] Materials { get; }

            // The shared set the materials come from, which counts the lying body as their user.
            public PlayerBodyMaterials MaterialSet { get; }
        }
    }
}
