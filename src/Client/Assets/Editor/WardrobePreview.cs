using System;
using System.Collections.Generic;
using System.Linq;
using Mmorpg.Client.Gameplay;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Mmorpg.Client.Editor
{
    // The Wardrobe preview: a body dressed in chosen garments of its appearance catalog, put
    // together as the game puts it together, that plays Idle, Walk, Jog, or Death, or a cloth
    // stress motion over them, and runs the cloth solver, in a preview scene of its own. While it
    // walks or jogs it moves forward at the clip's speed and the checkered ground moves along under
    // it, so loose cloth lags as in the game; a stress motion moves and turns it along its course,
    // in a network motion as a remote body shows it. WardrobeWindow shows it and ClothMeasurement
    // measures in it; see docs/EQUIPMENT_VISUALS_DESIGN.md.
    public sealed class WardrobePreview : IDisposable
    {
        public const string ControllerPath = "Assets/Art/Animation/HumanLocomotion.controller";

        // The ground's squares, in meters.
        public const float TileSize = 1f;

        // A body farther than this from the origin is moved back by whole squares.
        public const float WrapDistance = 500f;

        public enum Motion
        {
            Idle,
            Walk,
            Jog,
            Death,
        }

        private static readonly int SpeedId = Animator.StringToHash(HumanLocomotion.SpeedParameter);
        private static readonly int MovingId = Animator.StringToHash(HumanLocomotion.MovingParameter);
        private static readonly int RateId = Animator.StringToHash(HumanLocomotion.RateParameter);
        private static readonly int DeadId = Animator.StringToHash(HumanLocomotion.DeadParameter);

        private readonly PlayerBodyMeshes meshes = new PlayerBodyMeshes();
        private readonly PlayerBodyMaterials materials = new PlayerBodyMaterials();
        private readonly List<UnityEngine.Object> owned = new List<UnityEngine.Object>();
        private readonly Animator animator;
        private readonly PlayerBodyAppearance appearance;
        private readonly ClothChainSolver solver;
        private readonly Transform ground;
        private readonly float walkCycleSeconds = 1f;
        private readonly float jogCycleSeconds = 1f;
        private readonly RemoteMovementPresenter network = new RemoteMovementPresenter();
        private uint networkTick;
        private Scene scene;

        public WardrobePreview(ArtAssetManifest body)
            : this(
                body.path,
                AssetDatabase.LoadAssetAtPath<GameObject>(body.UnityTargetPath),
                AssetDatabase.LoadAssetAtPath<BodyAppearanceCatalog>(ArtAppearanceCatalogs.PathFor(body)))
        {
        }

        // A preview of this model with its appearance catalog; source names them in errors.
        public WardrobePreview(string source, GameObject model, BodyAppearanceCatalog catalog)
        {
            Catalog = catalog;
            var controller = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(ControllerPath);
            if (model == null || Catalog == null || controller == null)
            {
                throw new InvalidOperationException(source + ": the model, its appearance catalog, or the locomotion controller is missing.");
            }

            scene = EditorSceneManager.NewPreviewScene();
            try
            {
                Body = (GameObject)PrefabUtility.InstantiatePrefab(model, scene);
                Body.name = model.name;
                animator = Body.GetComponent<Animator>();
                if (animator == null)
                {
                    throw new InvalidOperationException(source + ": the model has no Animator.");
                }

                animator.runtimeAnimatorController = controller;
                animator.applyRootMotion = false;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                foreach (var clip in controller.animationClips)
                {
                    if (clip.name == HumanLocomotion.WalkClip)
                    {
                        walkCycleSeconds = clip.length;
                    }
                    else if (clip.name == HumanLocomotion.JogClip)
                    {
                        jogCycleSeconds = clip.length;
                    }
                }

                appearance = Body.AddComponent<PlayerBodyAppearance>();
                var properties = new SerializedObject(appearance);
                properties.FindProperty("catalog").objectReferenceValue = Catalog;
                properties.ApplyModifiedPropertiesWithoutUndo();
                solver = Body.AddComponent<ClothChainSolver>();
                appearance.CombineParts(meshes);

                ground = CreateGround();
                var lightObject = new GameObject("WardrobeLight");
                SceneManager.MoveGameObjectToScene(lightObject, scene);
                var light = lightObject.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 1.2f;
                lightObject.transform.rotation = Quaternion.Euler(45f, 150f, 0f);
                Play(Motion.Idle);
            }
            catch
            {
                // A model that cannot be shown leaves no scene or objects behind.
                Dispose();
                throw;
            }
        }

        public BodyAppearanceCatalog Catalog { get; }

        public GameObject Body { get; }

        public Scene Scene
        {
            get { return scene; }
        }

        public Motion Playing { get; private set; }

        // The stress motion it plays, or null for the plain clip, and how far into it it is.
        public ClothStressMotion Stress { get; private set; }

        public float StressTime { get; private set; }

        // The clip's speed over the ground, in meters per second.
        public float Speed
        {
            get { return Playing == Motion.Walk ? HumanLocomotion.WalkSpeed : Playing == Motion.Jog ? HumanLocomotion.JogSpeed : 0f; }
        }

        public IReadOnlyList<BodyAppearanceCatalog.Garment> Worn
        {
            get { return appearance.Worn; }
        }

        public IReadOnlyList<BodyAppearanceCatalog.Attachment> Held
        {
            get { return appearance.Held; }
        }

        public ClothChainSolver Solver
        {
            get { return solver; }
        }

        // Where the camera looks: the body's chest.
        public Vector3 Focus
        {
            get { return Body.transform.position + Vector3.up * 1.1f; }
        }

        // Dresses the body in the garments of its catalog with these names, with these paint
        // layers on its skin, holding these attachments; its underwear is always worn.
        public void Wear(IEnumerable<string> garments, IEnumerable<string> paints = null, IEnumerable<string> attachments = null)
        {
            var look = BodyLook.Default;
            foreach (var garment in garments)
            {
                look = look.WithGarment(garment, true);
            }

            foreach (var paint in paints ?? Enumerable.Empty<string>())
            {
                look = look.WithPaint(paint, true);
            }

            foreach (var attachment in attachments ?? Enumerable.Empty<string>())
            {
                look = look.WithAttachment(attachment, true);
            }

            appearance.ApplyLook(look, materials);
        }

        // The paint layers the body shows, by the rules of their categories.
        public IReadOnlyList<BodyAppearanceCatalog.Paint> Painted
        {
            get
            {
                var shown = new List<BodyAppearanceCatalog.Paint>();
                Catalog.ShownPaints(appearance.Look, PlayerBodyMeshes.Drawn(appearance.Worn), shown);
                return shown;
            }
        }

        // Plays a clip; a body that leaves a stress motion faces forward again, the way a walk or a
        // jog moves it.
        public void Play(Motion motion)
        {
            Playing = motion;
            if (Stress != null)
            {
                Stress = null;
                Body.transform.rotation = Quaternion.identity;
            }

            Animate(Speed, motion == Motion.Death);
        }

        // Plays a stress motion from its start, its clips from their start, so every run of a motion
        // is alike; at its end it starts again.
        public void Play(ClothStressMotion motion)
        {
            Stress = motion;
            StressTime = 0f;
            animator.Rebind();
            animator.Update(0f);
            network.Configure(ClothStressMotion.NetworkTickRateHz,
                RemoteMovementPresenter.InterpolationDelayTicks(ClothStressMotion.NetworkTickRateHz, ClothStressMotion.NetworkSnapshotRateHz),
                null, null);
            networkTick = 0;
            Place(motion.At(0f));
        }

        // Moves the body at the clip's speed, poses it, moves the ground along, and runs the
        // cloth solver as seen from a camera this far from the body's chest once the body moved, so
        // the camera keeps up with a jump.
        public void Step(float deltaTime, Vector3 cameraOffset)
        {
            if (deltaTime <= 0f)
            {
                return;
            }

            if (Stress != null)
            {
                StepStress(deltaTime, cameraOffset);
                return;
            }

            var position = Body.transform.position + Vector3.forward * (Speed * deltaTime);
            if (position.z > WrapDistance)
            {
                // Back by whole squares, the ground looks the same; the solver starts the chains again.
                position.z -= Mathf.Floor(position.z / TileSize) * TileSize;
            }

            Body.transform.position = position;
            ground.position = new Vector3(0f, 0f, Mathf.Floor(position.z / TileSize) * TileSize);
            animator.Update(deltaTime);
            solver.Simulate(deltaTime, Focus + cameraOffset);
        }

        private void StepStress(float deltaTime, Vector3 cameraOffset)
        {
            StressTime += deltaTime;
            if (StressTime > Stress.Seconds)
            {
                Play(Stress);
            }

            Place(Stress.Network ? Networked(deltaTime) : Stress.At(StressTime));
            animator.Update(deltaTime);
            solver.Simulate(deltaTime, Focus + cameraOffset);
        }

        // The course as a remote body shows it: snapshots of it at the network's rate, presented
        // by the game's own presenter, a few ticks behind.
        private ClothStressMotion.Pose Networked(float deltaTime)
        {
            var tickSeconds = 1f / ClothStressMotion.NetworkTickRateHz;
            var everyTicks = ClothStressMotion.NetworkTickRateHz / ClothStressMotion.NetworkSnapshotRateHz;
            for (; networkTick * tickSeconds <= StressTime; networkTick++)
            {
                if (networkTick % everyTicks != 0)
                {
                    continue;
                }

                var at = Stress.At(networkTick * tickSeconds);
                var before = Stress.At(((int)networkTick - 1) * tickSeconds);
                var velocity = (at.Position - before.Position) / tickSeconds;
                network.Push(networkTick, new Mmorpg.GameSimulation.PlayerMovementState(at.Position.x, at.Position.y, at.Position.z,
                    velocity.x, velocity.y, velocity.z, at.Yaw, true));
            }

            network.Advance(deltaTime, deltaTime);
            var delay = network.DelayTicks * tickSeconds;
            var played = Stress.At(StressTime - delay);
            return new ClothStressMotion.Pose(network.Position, network.YawDegrees, played.Speed, played.Dead);
        }

        // Stands the body where a pose of a stress motion puts it, the ground under it, its clips at
        // the pose's speed.
        private void Place(ClothStressMotion.Pose pose)
        {
            Body.transform.SetPositionAndRotation(pose.Position, Quaternion.Euler(0f, pose.Yaw, 0f));
            ground.position = new Vector3(Mathf.Floor(pose.Position.x / TileSize) * TileSize, 0f,
                Mathf.Floor(pose.Position.z / TileSize) * TileSize);
            Animate(pose.Speed, pose.Dead);
        }

        private void Animate(float speed, bool dead)
        {
            HumanLocomotion.Evaluate(speed, walkCycleSeconds, jogCycleSeconds, out var blend, out var rate);
            animator.SetBool(MovingId, speed >= HumanLocomotion.MovingSpeed);
            animator.SetFloat(SpeedId, blend);
            animator.SetFloat(RateId, rate);
            animator.SetBool(DeadId, dead);
        }

        // Renders the scene from the camera into the texture.
        public void Render(Camera camera, RenderTexture target)
        {
            camera.scene = scene;
            camera.targetTexture = target;
            var async = ShaderUtil.allowAsyncCompilation;
            ShaderUtil.allowAsyncCompilation = false;
            try
            {
                camera.Render();
            }
            finally
            {
                ShaderUtil.allowAsyncCompilation = async;
                camera.targetTexture = null;
            }
        }

        public void Dispose()
        {
            if (scene.IsValid())
            {
                EditorSceneManager.ClosePreviewScene(scene);
            }

            foreach (var item in owned)
            {
                if (item != null)
                {
                    UnityEngine.Object.DestroyImmediate(item);
                }
            }

            owned.Clear();
            meshes.Dispose();
            materials.Dispose();
        }

        // A checkered floor of 40 by 40 squares, moved along under the body by whole squares.
        private Transform CreateGround()
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGB24, false)
            {
                name = "WardrobeChecker",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Repeat,
                hideFlags = HideFlags.HideAndDontSave,
            };
            texture.SetPixels(new[]
            {
                new Color(0.36f, 0.40f, 0.36f), new Color(0.27f, 0.31f, 0.27f),
                new Color(0.27f, 0.31f, 0.27f), new Color(0.36f, 0.40f, 0.36f),
            });
            texture.Apply();
            owned.Add(texture);
            var material = new Material(Shader.Find(ArtMaterialCreator.LitShader))
            {
                name = "WardrobeGround",
                hideFlags = HideFlags.HideAndDontSave,
            };
            material.SetTexture("_BaseMap", texture);
            material.SetTextureScale("_BaseMap", new Vector2(20f, 20f));
            material.SetFloat("_Smoothness", 0.1f);
            owned.Add(material);
            var plane = GameObject.CreatePrimitive(PrimitiveType.Plane);
            plane.name = "WardrobeGround";
            SceneManager.MoveGameObjectToScene(plane, scene);
            UnityEngine.Object.DestroyImmediate(plane.GetComponent<Collider>());
            // The primitive plane is 10 m wide; four of them make 40 squares of a meter.
            plane.transform.localScale = new Vector3(4f, 1f, 4f);
            plane.GetComponent<MeshRenderer>().sharedMaterial = material;
            return plane.transform;
        }

        // Every body with an appearance catalog that a garment fits.
        public static ArtAssetManifest[] Bodies()
        {
            return ArtAppearanceCatalogs.ManifestsWithLooks()
                .Where(body => AssetDatabase.LoadAssetAtPath<BodyAppearanceCatalog>(ArtAppearanceCatalogs.PathFor(body)) != null)
                .OrderBy(body => body.Name, StringComparer.Ordinal)
                .ToArray();
        }
    }
}
