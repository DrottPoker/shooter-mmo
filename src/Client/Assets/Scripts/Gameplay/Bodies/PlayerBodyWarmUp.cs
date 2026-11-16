using System;
using System.Collections.Generic;
using Mmorpg.Client.Worlds;
using UnityEngine;

namespace Mmorpg.Client.Gameplay
{
    // The work that the first body of a session did in a frame of its own, done while the World
    // loads instead: for every body model a player can show, the first instance of the model and its
    // Animator, the combined mesh of its default look and the material copies of that look, the cloth
    // measures of the body and of every garment with loose parts, with the cloth world, and the first
    // draw of every material a body can show, whose GPU pipeline states the graphics driver makes on
    // first use: the skin, the garment, and the attachment materials, skinned on the GPU, in the
    // player camera's passes and into targets of its formats, its shadows included. The bodies stand
    // on a stage far below the World and beyond every player camera; a camera that renders as the
    // player camera does draws them once, before the player camera, which draws over its small corner
    // of the screen, and the bodies and the camera go in the next frame. The meshes of the default
    // looks stay with the World, since every body that comes into view starts from one.
    public sealed class PlayerBodyWarmUp
    {
        public const string StageName = "PlayerBodyWarmUpStage";

        // Far below every World, beyond every player camera's far plane, and away from the
        // Character Preview's stage.
        public static readonly Vector3 StagePosition = new Vector3(0f, -11000f, 0f);

        // The camera draws a corner of the screen; the pipeline states do not depend on its size.
        private static readonly Rect CameraRect = new Rect(0f, 0f, 0.125f, 0.125f);

        private readonly GameObject stage;
        private readonly List<PlayerBodyMeshes.Combined> defaultMeshes = new List<PlayerBodyMeshes.Combined>();
        private readonly int drawnFrame;

        private PlayerBodyWarmUp(GameObject stage, int drawnFrame)
        {
            this.stage = stage;
            this.drawnFrame = drawnFrame;
        }

        // How long the work took on the main thread while the World loaded, without the draw.
        public double Milliseconds { get; private set; }

        // How many body models and drawn material variants it prepared.
        public int Models { get; private set; }

        public int MaterialVariants { get; private set; }

        // Puts every model on the stage, prepares it, and lets a camera like the player camera draw
        // it at the end of this frame. Without a player camera, as in a test without one, nothing
        // is drawn but the rest is prepared.
        public static PlayerBodyWarmUp Begin(IReadOnlyList<PlayerBodyModels.Model> models, PlayerBodyMeshes meshes,
            PlayerBodyMaterials materials, Camera playerCamera, Transform parent)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var stage = new GameObject(StageName);
            stage.transform.SetParent(parent, false);
            stage.transform.SetPositionAndRotation(StagePosition, Quaternion.identity);
            var warmUp = new PlayerBodyWarmUp(stage, Time.frameCount);
            var drawn = new HashSet<string>(StringComparer.Ordinal);
            var variants = new List<Material>();
            var place = 0;
            foreach (var model in models ?? Array.Empty<PlayerBodyModels.Model>())
            {
                var prefab = model != null ? model.Body : null;
                if (prefab == null)
                {
                    continue;
                }

                var body = UnityEngine.Object.Instantiate(prefab, stage.transform, false);
                body.name = model.Name;
                body.transform.localPosition = new Vector3(place * 1.2f, 0f, 0f);
                place++;
                body.CombineParts(meshes);
                body.ApplyLook(BodyLook.Default, materials);
                warmUp.defaultMeshes.Add(meshes.Acquire(
                    PlayerBodyMeshes.PartsOf(prefab), prefab.transform, prefab.Catalog, null, null));
                var solver = body.GetComponent<ClothChainSolver>();
                if (solver != null && prefab.Catalog != null)
                {
                    solver.PrepareMeasures(prefab.Catalog.Garments);
                    ActivateCloth(solver, prefab.Catalog);
                }

                if (body.CombinedRenderer != null)
                {
                    foreach (var material in body.CombinedRenderer.sharedMaterials)
                    {
                        drawn.Add(VariantOf(material));
                    }

                    CollectVariants(prefab.Catalog, drawn, variants);
                    warmUp.MaterialVariants += DrawVariants(body, variants);
                    variants.Clear();
                }

                warmUp.Models++;
            }

            warmUp.Milliseconds = watch.Elapsed.TotalMilliseconds;
            if (playerCamera != null && warmUp.Models > 0)
            {
                AddCamera(stage.transform, playerCamera, place);
            }

            return warmUp;
        }

        // Removes the stage once its camera has drawn, the frame after it began; true when done.
        public bool TryEnd()
        {
            if (stage != null && Time.frameCount <= drawnFrame)
            {
                return false;
            }

            Remove();
            return true;
        }

        // Removes the stage at once and lets go of the default looks' meshes.
        public void Dispose(PlayerBodyMeshes meshes)
        {
            Remove();
            foreach (var combined in defaultMeshes)
            {
                meshes?.Release(combined);
            }

            defaultMeshes.Clear();
        }

        private void Remove()
        {
            if (stage == null)
            {
                return;
            }

            // Destroy waits for the end of the frame; until then nothing of the stage may draw.
            stage.SetActive(false);
            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(stage);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(stage);
            }
        }

        // The body hangs the cloth of the first garment of each kind of chain, so the cloth world
        // exists and has taken a body.
        private static void ActivateCloth(ClothChainSolver solver, BodyAppearanceCatalog catalog)
        {
            foreach (var garment in catalog.Garments)
            {
                var kind = garment != null && garment.Model != null ? PlayerBodyAppearance.ChainsOf(garment.Cloth) : null;
                if (kind.HasValue && solver.ProfileOf(kind.Value) == null)
                {
                    solver.Activate(kind.Value, ClothProfile.For(kind.Value), garment.Model);
                }
            }
        }

        // One material of every shader variant a garment or an attachment of the catalog draws with
        // that is not drawn yet.
        private static void CollectVariants(BodyAppearanceCatalog catalog, HashSet<string> drawn, List<Material> variants)
        {
            if (catalog == null)
            {
                return;
            }

            foreach (var garment in catalog.Garments)
            {
                if (garment != null && garment.Material != null && drawn.Add(VariantOf(garment.Material)))
                {
                    variants.Add(garment.Material);
                }
            }

            foreach (var attachment in catalog.Attachments)
            {
                if (attachment == null || attachment.Model == null)
                {
                    continue;
                }

                foreach (var renderer in attachment.Model.GetComponentsInChildren<Renderer>(true))
                {
                    foreach (var material in renderer.sharedMaterials)
                    {
                        if (material != null && drawn.Add(VariantOf(material)))
                        {
                            variants.Add(material);
                        }
                    }
                }
            }
        }

        // What decides a material's pipeline states: its shader, keywords, queue, and the render
        // states its properties set.
        private static string VariantOf(Material material)
        {
            if (material == null)
            {
                return string.Empty;
            }

            return material.shader.name + "|" + string.Join(" ", material.shaderKeywords) + "|" + material.renderQueue
                + "|" + StateOf(material, "_Cull") + "|" + StateOf(material, "_SrcBlend") + "|" + StateOf(material, "_DstBlend")
                + "|" + StateOf(material, "_ZWrite");
        }

        private static string StateOf(Material material, string property)
        {
            return material.HasProperty(property) ? material.GetFloat(property).ToString(System.Globalization.CultureInfo.InvariantCulture) : "-";
        }

        // Every variant draws on the body's combined mesh, skinned to its bones as a body is, by
        // renderers of their own, as many submeshes at a time as the mesh has.
        private static int DrawVariants(PlayerBodyAppearance body, List<Material> variants)
        {
            var combined = body.CombinedRenderer;
            var submeshes = combined.sharedMesh != null ? combined.sharedMesh.subMeshCount : 0;
            if (submeshes == 0)
            {
                return 0;
            }

            for (var first = 0; first < variants.Count; first += submeshes)
            {
                var count = Mathf.Min(submeshes, variants.Count - first);
                var drawing = new Material[count];
                variants.CopyTo(first, drawing, 0, count);
                var rendererObject = new GameObject("WarmUpVariants");
                rendererObject.transform.SetParent(body.transform, false);
                var renderer = rendererObject.AddComponent<SkinnedMeshRenderer>();
                renderer.shadowCastingMode = combined.shadowCastingMode;
                renderer.receiveShadows = combined.receiveShadows;
                renderer.quality = combined.quality;
                renderer.updateWhenOffscreen = true;
                renderer.sharedMesh = combined.sharedMesh;
                renderer.bones = combined.bones;
                renderer.rootBone = combined.rootBone;
                renderer.localBounds = combined.localBounds;
                renderer.sharedMaterials = drawing;
            }

            return variants.Count;
        }

        // A camera that renders as the player camera does, in front of the bodies, before it.
        private static void AddCamera(Transform stage, Camera playerCamera, int bodies)
        {
            var cameraObject = new GameObject("WarmUpCamera");
            cameraObject.transform.SetParent(stage, false);
            var warmCamera = cameraObject.AddComponent<Camera>();
            CameraRendering.CopyFrom(warmCamera, playerCamera);
            warmCamera.targetTexture = null;
            warmCamera.rect = CameraRect;
            warmCamera.depth = playerCamera.depth - 1f;
            cameraObject.transform.SetParent(stage, false);
            var middle = (bodies - 1) * 0.6f;
            cameraObject.transform.localPosition = new Vector3(middle, 1f, 2.4f + bodies * 0.6f);
            cameraObject.transform.localRotation = Quaternion.LookRotation(new Vector3(middle, 0.9f, 0f) - cameraObject.transform.localPosition);
            warmCamera.enabled = true;
        }
    }
}
