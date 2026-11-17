using System;
using System.Collections.Generic;
using Mmorpg.Client.Worlds;
using UnityEngine;

namespace Mmorpg.Client.Gameplay
{
    // The work that the first body of a session did in a frame of its own, done before the World is
    // shown instead: for every body model a player can show, the first instance of the model and its
    // Animator, the combined mesh of its default look and the material copies of that look, the cloth
    // measures of the body and the cloth world, and the first draw of every material its bodies draw
    // with, whose GPU pipeline states the graphics driver makes on first use, skinned on the GPU, in
    // the player camera's passes and into targets of its formats, its shadows included. It reads
    // nothing of the catalog's garments and attachments: their content loads on demand, and the first
    // time a garment or an attachment brings a material of another shader variant, the warm-up draws
    // that material once before any body shows it (IsWarm). The bodies stand on a stage far below the
    // World and beyond every player camera; a camera that renders as the player camera does draws
    // them once, before the player camera, which draws over its small corner of the screen, and the
    // stage rests until the next variant. The meshes of the default looks stay with the World, since
    // every body that comes into view starts from one, and the stage's bodies hold the content of the
    // default looks, their underwear, for the World's life.
    public sealed class PlayerBodyWarmUp : IBodyContentWarmUp
    {
        public const string StageName = "PlayerBodyWarmUpStage";
        public const string VariantRendererName = "WarmUpVariants";

        // Far below every World, beyond every player camera's far plane, and away from the
        // Character Preview's stage.
        public static readonly Vector3 StagePosition = new Vector3(0f, -11000f, 0f);

        // The camera draws a corner of the screen; the pipeline states do not depend on its size.
        private static readonly Rect CameraRect = new Rect(0f, 0f, 0.125f, 0.125f);

        private readonly GameObject stage;
        private readonly List<PlayerBodyMeshes.Combined> defaultMeshes = new List<PlayerBodyMeshes.Combined>();
        // The variants drawn once, the materials that wait for their draw, and those drawn now.
        private readonly HashSet<string> drawn = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> requested = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<Material> queued = new List<Material>();
        private readonly List<Material> drawing = new List<Material>();
        private readonly List<Material> found = new List<Material>();
        private readonly List<GameObject> variantRenderers = new List<GameObject>();
        private PlayerBodyAppearance stageBody;
        private Camera playerCamera;
        private Camera warmCamera;
        private int bodies;
        private int drawnFrame = -1;

        private PlayerBodyWarmUp(GameObject stage)
        {
            this.stage = stage;
        }

        // How long the work took on the main thread while the World loaded, without the draw.
        public double Milliseconds { get; private set; }

        // How many body models and drawn material variants it prepared.
        public int Models { get; private set; }

        public int MaterialVariants { get; private set; }

        // Whether the stage draws this frame, as after Begin or a new variant.
        public bool IsDrawing
        {
            get { return drawnFrame >= 0; }
        }

        // The content every body shows before its look: the default look of each model, its
        // underwear. The World is shown once it is loaded, and the warm-up's bodies hold it.
        public static List<PlayerBodyContent.Needs> DefaultNeeds(IReadOnlyList<PlayerBodyModels.Model> models)
        {
            var needs = new List<PlayerBodyContent.Needs>();
            foreach (var model in models ?? Array.Empty<PlayerBodyModels.Model>())
            {
                if (model != null && model.Body != null)
                {
                    needs.Add(PlayerBodyContent.NeedsOf(model.Body.Catalog, BodyLook.Default));
                }
            }

            return needs;
        }

        // Puts every model on the stage in its default look, whose content must be loaded,
        // prepares it, and lets a camera like the player camera draw it and the materials of all
        // content loaded so far at the end of this frame. Without a player camera, as in a test
        // without one, nothing is drawn but the rest is prepared.
        public static PlayerBodyWarmUp Begin(IReadOnlyList<PlayerBodyModels.Model> models, PlayerBodyMeshes meshes,
            PlayerBodyMaterials materials, Camera playerCamera, Transform parent)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var stage = new GameObject(StageName);
            stage.transform.SetParent(parent, false);
            stage.transform.SetPositionAndRotation(StagePosition, Quaternion.identity);
            var warmUp = new PlayerBodyWarmUp(stage) { playerCamera = playerCamera };
            foreach (var model in models ?? Array.Empty<PlayerBodyModels.Model>())
            {
                var prefab = model != null ? model.Body : null;
                if (prefab == null)
                {
                    continue;
                }

                var body = UnityEngine.Object.Instantiate(prefab, stage.transform, false);
                body.name = model.Name;
                body.transform.localPosition = new Vector3(warmUp.bodies * 1.2f, 0f, 0f);
                warmUp.bodies++;
                body.CombineParts(meshes);
                body.ApplyLook(BodyLook.Default, materials);
                warmUp.defaultMeshes.Add(meshes.Acquire(
                    PlayerBodyMeshes.PartsOf(prefab), prefab.transform, prefab.Catalog, null, null));
                var solver = body.GetComponent<ClothChainSolver>();
                if (solver != null)
                {
                    solver.PrepareMeasures();
                    ActivateCloth(solver);
                }

                if (body.CombinedRenderer != null)
                {
                    foreach (var material in body.CombinedRenderer.sharedMaterials)
                    {
                        warmUp.drawn.Add(VariantOf(material));
                    }

                    warmUp.stageBody = warmUp.stageBody != null ? warmUp.stageBody : body;
                }

                warmUp.Models++;
            }

            // What loaded before the stage, such as the local player's look, draws with it.
            if (meshes.Content != null)
            {
                meshes.Content.ForEachReady(content => warmUp.IsWarm(content));
            }

            warmUp.Milliseconds = watch.Elapsed.TotalMilliseconds;
            warmUp.Draw();
            if (meshes.Content != null)
            {
                meshes.Content.WarmUp = warmUp;
            }

            return warmUp;
        }

        // Whether the content's materials drew once; when one did not, the stage draws it in the next
        // frame that does not draw already, and the content is warm the frame after. Without a stage
        // body or a player camera nothing can draw, so content is taken as it is.
        public bool IsWarm(UnityEngine.Object content)
        {
            if (stageBody == null || playerCamera == null)
            {
                return true;
            }

            found.Clear();
            MaterialsOf(content, found);
            var warm = true;
            foreach (var material in found)
            {
                var variant = VariantOf(material);
                if (drawn.Contains(variant))
                {
                    continue;
                }

                warm = false;
                if (requested.Add(variant))
                {
                    queued.Add(material);
                }
            }

            found.Clear();
            return warm;
        }

        // Ends a draw the frame after it began and starts the next one when materials wait; call
        // once a frame. Allocates nothing unless a draw begins or ends.
        public void Update()
        {
            if (drawnFrame >= 0 && Time.frameCount > drawnFrame)
            {
                Rest();
            }

            if (drawnFrame < 0 && queued.Count > 0)
            {
                Draw();
            }
        }

        // Removes the stage at once and lets go of the default looks' meshes.
        public void Dispose(PlayerBodyMeshes meshes)
        {
            if (meshes != null && meshes.Content != null && meshes.Content.WarmUp == this)
            {
                meshes.Content.WarmUp = null;
            }

            if (stage != null)
            {
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

            foreach (var combined in defaultMeshes)
            {
                meshes?.Release(combined);
            }

            defaultMeshes.Clear();
        }

        // The stage draws the queued materials this frame, through a camera like the player camera.
        private void Draw()
        {
            if (stage == null)
            {
                return;
            }

            stage.SetActive(true);
            drawing.AddRange(queued);
            queued.Clear();
            if (stageBody != null && drawing.Count > 0)
            {
                MaterialVariants += DrawVariants(stageBody, drawing, variantRenderers);
            }

            if (playerCamera != null && bodies > 0)
            {
                PrepareCamera();
            }

            drawnFrame = Time.frameCount;
        }

        // The stage rests once its camera drew: hidden, without the variants' renderers, which now
        // count as drawn.
        private void Rest()
        {
            drawnFrame = -1;
            foreach (var material in drawing)
            {
                var variant = VariantOf(material);
                drawn.Add(variant);
                requested.Remove(variant);
            }

            drawing.Clear();
            foreach (var renderer in variantRenderers)
            {
                if (renderer == null)
                {
                    continue;
                }

                renderer.SetActive(false);
                if (Application.isPlaying)
                {
                    UnityEngine.Object.Destroy(renderer);
                }
                else
                {
                    UnityEngine.Object.DestroyImmediate(renderer);
                }
            }

            variantRenderers.Clear();
            if (stage != null)
            {
                stage.SetActive(false);
            }
        }

        // The body hangs cloth on every kind of chain, so the cloth world exists and has taken a body.
        private static void ActivateCloth(ClothChainSolver solver)
        {
            foreach (ClothChainKind kind in Enum.GetValues(typeof(ClothChainKind)))
            {
                solver.Activate(kind, ClothProfile.For(kind));
            }
        }

        // The materials content draws with: a garment's texture material, or the materials of an
        // attachment's model.
        private static void MaterialsOf(UnityEngine.Object content, List<Material> materials)
        {
            var piece = content as BodyPieceContent;
            if (piece == null)
            {
                return;
            }

            if (piece.Material != null)
            {
                materials.Add(piece.Material);
                return;
            }

            if (piece.Model == null)
            {
                return;
            }

            foreach (var renderer in piece.Model.GetComponentsInChildren<Renderer>(true))
            {
                foreach (var material in renderer.sharedMaterials)
                {
                    if (material != null)
                    {
                        materials.Add(material);
                    }
                }
            }
        }

        // What decides a material's pipeline states: its shader, keywords, queue, and the render
        // states its properties set.
        public static string VariantOf(Material material)
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
        private static int DrawVariants(PlayerBodyAppearance body, List<Material> variants, List<GameObject> renderers)
        {
            var combined = body.CombinedRenderer;
            var submeshes = combined != null && combined.sharedMesh != null ? combined.sharedMesh.subMeshCount : 0;
            if (submeshes == 0)
            {
                return 0;
            }

            for (var first = 0; first < variants.Count; first += submeshes)
            {
                var count = Mathf.Min(submeshes, variants.Count - first);
                var materials = new Material[count];
                variants.CopyTo(first, materials, 0, count);
                var rendererObject = new GameObject(VariantRendererName);
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
                renderer.sharedMaterials = materials;
                renderers.Add(rendererObject);
            }

            return variants.Count;
        }

        // A camera that renders as the player camera does, in front of the bodies, before it.
        private void PrepareCamera()
        {
            if (warmCamera == null)
            {
                var cameraObject = new GameObject("WarmUpCamera");
                cameraObject.transform.SetParent(stage.transform, false);
                warmCamera = cameraObject.AddComponent<Camera>();
            }

            CameraRendering.CopyFrom(warmCamera, playerCamera);
            warmCamera.targetTexture = null;
            warmCamera.rect = CameraRect;
            warmCamera.depth = playerCamera.depth - 1f;
            var middle = (bodies - 1) * 0.6f;
            warmCamera.transform.localPosition = new Vector3(middle, 1f, 2.4f + bodies * 0.6f);
            warmCamera.transform.localRotation = Quaternion.LookRotation(new Vector3(middle, 0.9f, 0f) - warmCamera.transform.localPosition);
            warmCamera.enabled = true;
        }
    }
}
