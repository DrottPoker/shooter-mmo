using System;
using System.Collections.Generic;
using Mmorpg.Client.Gameplay;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Mmorpg.Client.Editor
{
    // One body of the capture of the game views, put in and dressed exactly as a player's body is,
    // with the World's shared meshes, materials, and content: the body model's prefab, CombineParts,
    // and ApplyLook with the look of an outfit. It differs from a player's body only in what moves
    // it: no locomotion driver writes its Animator, which holds the rest pose, as bound, or a frame
    // of a clip, still, and its lids stay open. Its cloth swings as a player's does, laid by the
    // cloth scheduler from the capture's camera. See docs/ART_PIPELINE.md, Capturing The Views From
    // The Game.
    internal sealed class GameViewBody : IDisposable
    {
        private const string HeadBone = "Head";
        private const string HipsBone = "Hips";
        private const string LeftFootBone = "Foot_L";
        private const string RightFootBone = "Foot_R";
        private const string FigureRendererName = "GameViewFigure";

        private static readonly int SpeedId = Animator.StringToHash(HumanLocomotion.SpeedParameter);
        private static readonly int MovingId = Animator.StringToHash(HumanLocomotion.MovingParameter);
        private static readonly int RateId = Animator.StringToHash(HumanLocomotion.RateParameter);
        private static readonly int DeadId = Animator.StringToHash(HumanLocomotion.DeadParameter);

        private readonly PlayerBodyAppearance body;
        private readonly Animator animator;
        private readonly Transform[] restTransforms;
        private readonly Vector3[] restPositions;
        private readonly Quaternion[] restRotations;
        private readonly Vector3[] restScales;
        private readonly Transform head;
        private readonly Transform hips;
        private readonly Transform leftFoot;
        private readonly Transform rightFoot;
        private readonly Vector3 headTailInHead;
        private readonly float headLength;
        private readonly Quaternion hipsAtRest;
        private readonly SkinnedMeshRenderer[] parts;
        private PlayerBodyMeshes readableMeshes;
        private SkinnedMeshRenderer figure;
        private Mesh baked;
        private int[] layers;

        private GameViewBody(PlayerBodyAppearance body, SkinnedMeshRenderer[] parts)
        {
            this.body = body;
            this.parts = parts;
            animator = body.GetComponent<Animator>();
            restTransforms = body.GetComponentsInChildren<Transform>(true);
            restPositions = new Vector3[restTransforms.Length];
            restRotations = new Quaternion[restTransforms.Length];
            restScales = new Vector3[restTransforms.Length];
            for (var index = 0; index < restTransforms.Length; index++)
            {
                restPositions[index] = restTransforms[index].localPosition;
                restRotations[index] = restTransforms[index].localRotation;
                restScales[index] = restTransforms[index].localScale;
            }

            head = Bone(HeadBone);
            hips = Bone(HipsBone);
            leftFoot = Bone(LeftFootBone);
            rightFoot = Bone(RightFootBone);

            // The head bone runs upright at rest from its head at the mouth to the top of the skull,
            // the highest point of the body at rest, as the rig of tools/art/blender/human_base lays it.
            var top = float.MinValue;
            foreach (var part in parts)
            {
                foreach (var vertex in part.sharedMesh.vertices)
                {
                    top = Mathf.Max(top, part.transform.TransformPoint(vertex).y);
                }
            }

            headLength = top - head.position.y;
            headTailInHead = Quaternion.Inverse(head.rotation) * body.transform.up;
            hipsAtRest = Quaternion.Inverse(body.transform.rotation) * hips.rotation;
        }

        public PlayerBodyAppearance Body
        {
            get { return body; }
        }

        public ClothChainSolver Solver
        {
            get { return body.GetComponent<ClothChainSolver>(); }
        }

        // The length of the head bone, from its head to the top of the skull, in meters.
        public float HeadLength
        {
            get { return headLength; }
        }

        // Puts a body of the model into the scene, at rest at the place, and dresses it in the look,
        // whose content must be loaded: the shared meshes build its combination at once, as for a
        // body that shows its first look while the World loads.
        public static GameViewBody Create(PlayerBodyModels.Model model, Scene scene, Vector3 position, Quaternion rotation, BodyLook look,
            PlayerBodyMeshes meshes, PlayerBodyMaterials materials)
        {
            var instance = Object.Instantiate(model.Body, position, rotation);
            instance.name = "GameViewBody " + model.Name;
            SceneManager.MoveGameObjectToScene(instance.gameObject, scene);
            var created = new GameViewBody(instance, PlayerBodyMeshes.PartsOf(instance));

            // Nothing drives the body but the capture.
            var locomotion = instance.GetComponent<CharacterLocomotionAnimator>();
            if (locomotion != null)
            {
                locomotion.enabled = false;
            }

            if (created.animator != null)
            {
                created.animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                created.animator.applyRootMotion = false;
            }

            instance.CombineParts(meshes);
            instance.ApplyLook(look, materials);

            // The lids rest open, as no clip and no Blender view turns them.
            if (instance.Eyelids != null)
            {
                EyelidScheduler.Unregister(instance.Eyelids);
                instance.Eyelids.Show(0f);
            }

            return created;
        }

        // The names of the garments the body wears, from the lowest layer up.
        public List<string> WornNames()
        {
            var names = new List<string>();
            foreach (var garment in body.Worn)
            {
                names.Add(garment.Name);
            }

            return names;
        }

        // The rest pose: the Animator off and every transform as the prefab put it in, which is the
        // pose the mesh is bound in.
        public void ShowRest()
        {
            if (animator != null)
            {
                animator.enabled = false;
            }

            for (var index = 0; index < restTransforms.Length; index++)
            {
                if (restTransforms[index] != null)
                {
                    restTransforms[index].localPosition = restPositions[index];
                    restTransforms[index].localRotation = restRotations[index];
                    restTransforms[index].localScale = restScales[index];
                }
            }

            KeepLidsOpen();
        }

        // Turns the Animator on for a clip pose; false while it is not initialized yet, which a frame
        // later it is.
        public bool PrepareClip()
        {
            if (animator == null || animator.runtimeAnimatorController == null)
            {
                throw new InvalidOperationException("The body " + body.name + " has no Animator controller.");
            }

            if (!animator.enabled)
            {
                animator.enabled = true;
            }

            return animator.isInitialized;
        }

        // Holds a frame of a clip still, from frame 0 to the clip's last, in the state that plays it
        // at normalized time frame over last frame: Idle and Death in their base states, Walk and Jog
        // in the locomotion blend at the speed that plays only that clip, and an attack on the
        // Action layer over the base layer's Idle at its start, as the game plays an attack, without
        // the grip and hold layers, since no weapon is held. Returns what the Animator shows.
        public string ShowClip(string clip, int frame, out float normalizedTime)
        {
            var asset = ClipNamed(clip);
            var last = Mathf.RoundToInt(asset.length * asset.frameRate);
            if (last <= 0 || frame > last)
            {
                throw new InvalidOperationException("The clip " + clip + " has frames 0 to " + last + ", not " + frame + ".");
            }

            normalizedTime = (float)frame / last;
            var baseLayer = animator.GetLayerName(0);
            var actionLayer = animator.GetLayerIndex(HumanLocomotion.ActionLayer);
            var moving = clip == HumanLocomotion.WalkClip || clip == HumanLocomotion.JogClip;
            var speed = clip == HumanLocomotion.JogClip ? HumanLocomotion.JogSpeed : HumanLocomotion.WalkSpeed;
            string baseState;
            string actionState = null;
            if (moving)
            {
                baseState = HumanLocomotion.LocomotionState;
            }
            else if (clip == HumanLocomotion.DeathClip)
            {
                baseState = HumanLocomotion.DeathState;
            }
            else if (actionLayer >= 0 && animator.HasState(actionLayer, Animator.StringToHash(HumanLocomotion.ActionLayer + "." + clip)))
            {
                baseState = HumanLocomotion.IdleState;
                actionState = clip;
            }
            else if (animator.HasState(0, Animator.StringToHash(baseLayer + "." + clip)))
            {
                baseState = clip;
            }
            else
            {
                throw new InvalidOperationException("The Animator controller has no state that plays " + clip + ".");
            }

            animator.speed = 0f;
            animator.SetBool(MovingId, moving);
            animator.SetFloat(SpeedId, speed);
            animator.SetFloat(RateId, 1f);
            animator.SetBool(DeadId, clip == HumanLocomotion.DeathClip);
            for (var layer = 1; layer < animator.layerCount; layer++)
            {
                animator.SetLayerWeight(layer, 0f);
            }

            animator.Play(baseState, 0, actionState != null ? 0f : normalizedTime);
            if (actionState != null)
            {
                animator.SetLayerWeight(actionLayer, 1f);
                animator.Play(actionState, actionLayer, normalizedTime);
            }

            animator.Update(0f);
            KeepLidsOpen();
            var layerShown = actionState != null ? actionLayer : 0;
            var info = animator.GetCurrentAnimatorStateInfo(layerShown);
            var stateShown = actionState ?? baseState;
            if (info.shortNameHash != Animator.StringToHash(stateShown) || Mathf.Abs(Mathf.Repeat(info.normalizedTime - normalizedTime + 0.5f, 1f) - 0.5f) > 1e-4f)
            {
                throw new InvalidOperationException("The Animator does not hold " + clip + " at frame " + frame + ": it shows the state "
                    + info.shortNameHash + " at " + info.normalizedTime.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture) + ".");
            }

            return (actionState != null ? HumanLocomotion.ActionLayer + "." + actionState + " over " + baseLayer + "." + baseState : baseLayer + "." + baseState)
                + (moving ? " at " + speed.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " m/s" : string.Empty);
        }

        // Where the particles of every active cloth chain lie now, in the world, as the last step laid
        // them, and the farthest of them from where the target pose lays it; none without cloth.
        public List<Vector3> ClothParticles(out float fromTarget)
        {
            var particles = new List<Vector3>();
            fromTarget = 0f;
            var solver = Solver;
            if (solver == null || !solver.IsActive)
            {
                return particles;
            }

            foreach (var chain in ClothChains.All)
            {
                var laid = solver.ParticlesOf(chain);
                var targets = solver.TargetsOf(chain);
                for (var index = 0; index < laid.Count; index++)
                {
                    particles.Add(laid[index]);
                    if (index < targets.Count)
                    {
                        fromTarget = Mathf.Max(fromTarget, Vector3.Distance(laid[index], targets[index]));
                    }
                }
            }

            return particles;
        }

        // The vertices the body draws now, in its own frame.
        public List<Vector3> Figure()
        {
            var renderer = FigureRenderer();
            if (baked == null)
            {
                baked = new Mesh { name = "GameViewFigure" };
            }

            renderer.BakeMesh(baked, true);
            var toWorld = Matrix4x4.TRS(renderer.transform.position, renderer.transform.rotation, Vector3.one);
            var vertices = baked.vertices;
            var points = new List<Vector3>(vertices.Length);
            foreach (var vertex in vertices)
            {
                points.Add(body.transform.InverseTransformPoint(toWorld.MultiplyPoint3x4(vertex)));
            }

            return points;
        }

        // The point a close view of the head looks at, a share along the head bone, in the body's
        // frame.
        public Vector3 HeadPoint()
        {
            var tail = head.rotation * headTailInHead * headLength;
            return body.transform.InverseTransformPoint(head.position + tail * (float)GameViewFraming.HeadShare);
        }

        // The bones the body is skinned to, in the order BonePositions gives them.
        public string[] BoneNames()
        {
            var bones = FigureRenderer().bones;
            var names = new string[bones.Length];
            for (var index = 0; index < bones.Length; index++)
            {
                names[index] = bones[index].name;
            }

            return names;
        }

        // Where the head of every bone the body is skinned to lies now, in the body's own frame:
        // three numbers per bone.
        public float[] BonePositions()
        {
            var bones = FigureRenderer().bones;
            var positions = new float[bones.Length * 3];
            for (var index = 0; index < bones.Length; index++)
            {
                var local = body.transform.InverseTransformPoint(bones[index].position);
                positions[index * 3] = local.x;
                positions[index * 3 + 1] = local.y;
                positions[index * 3 + 2] = local.z;
            }

            return positions;
        }

        // How far the hips turned about the upright from the rest pose, in degrees, positive to the
        // body's right.
        public float HipsTurn()
        {
            var turn = Quaternion.Inverse(body.transform.rotation) * hips.rotation * Quaternion.Inverse(hipsAtRest);
            var front = turn * Vector3.forward;
            front.y = 0f;
            return front.sqrMagnitude > 1e-8f ? Vector3.SignedAngle(Vector3.forward, front, Vector3.up) : 0f;
        }

        // The head of the hips bone, which a close view of the waist looks at, in the body's frame.
        public Vector3 HipsPoint()
        {
            return body.transform.InverseTransformPoint(hips.position);
        }

        // The heights of the foot bones' heads in the body's frame.
        public float LeftFootHeight
        {
            get { return body.transform.InverseTransformPoint(leftFoot.position).y; }
        }

        public float RightFootHeight
        {
            get { return body.transform.InverseTransformPoint(rightFoot.position).y; }
        }

        // How far the pose lies from the bind pose of the drawn mesh: the largest difference of an
        // entry of a bone's bind matrix, so 0 for the bind pose itself.
        public float FromBindPose(out string farthestBone)
        {
            var renderer = FigureRenderer();
            var bindposes = renderer.sharedMesh.bindposes;
            var bones = renderer.bones;
            var farthest = 0f;
            farthestBone = string.Empty;
            for (var index = 0; index < bones.Length && index < bindposes.Length; index++)
            {
                var now = bones[index].worldToLocalMatrix * renderer.transform.localToWorldMatrix;
                for (var entry = 0; entry < 16; entry++)
                {
                    var difference = Mathf.Abs(now[entry] - bindposes[index][entry]);
                    if (difference > farthest)
                    {
                        farthest = difference;
                        farthestBone = bones[index].name;
                    }
                }
            }

            return farthest;
        }

        // How far the drawn vertices lie from where the mesh has them, in its bind pose, in meters.
        public float FromBindVertices()
        {
            var renderer = FigureRenderer();
            if (baked == null)
            {
                baked = new Mesh { name = "GameViewFigure" };
            }

            renderer.BakeMesh(baked, true);
            var bound = renderer.sharedMesh.vertices;
            var now = baked.vertices;
            var farthest = 0f;
            for (var index = 0; index < bound.Length && index < now.Length; index++)
            {
                farthest = Mathf.Max(farthest, Vector3.Distance(bound[index], now[index]));
            }

            return farthest;
        }

        // Puts every object of the body on a layer, or back on its own.
        public void UseLayer(int layer)
        {
            var objects = body.GetComponentsInChildren<Transform>(true);
            layers = new int[objects.Length];
            for (var index = 0; index < objects.Length; index++)
            {
                layers[index] = objects[index].gameObject.layer;
                objects[index].gameObject.layer = layer;
            }
        }

        public void UseOwnLayers()
        {
            if (layers == null)
            {
                return;
            }

            var objects = body.GetComponentsInChildren<Transform>(true);
            for (var index = 0; index < objects.Length && index < layers.Length; index++)
            {
                objects[index].gameObject.layer = layers[index];
            }

            layers = null;
        }

        public void Dispose()
        {
            if (baked != null)
            {
                Object.Destroy(baked);
                baked = null;
            }

            if (body != null)
            {
                body.gameObject.SetActive(false);
                Object.Destroy(body.gameObject);
            }

            // After the body, which drew with them.
            readableMeshes?.Dispose();
            readableMeshes = null;
        }

        // The lids stay open, whatever turned them.
        private void KeepLidsOpen()
        {
            if (body.Eyelids != null)
            {
                EyelidScheduler.Unregister(body.Eyelids);
                body.Eyelids.Show(0f);
            }
        }

        // The renderer the drawn vertices are baked from: the body's own, or, when its shared mesh
        // keeps no copy on the CPU, a renderer that is never drawn with the same combination built
        // again, readable, from the same content.
        private SkinnedMeshRenderer FigureRenderer()
        {
            var combined = body.CombinedRenderer;
            if (combined == null)
            {
                throw new InvalidOperationException("The body " + body.name + " has no combined renderer.");
            }

            if (combined.sharedMesh != null && combined.sharedMesh.isReadable)
            {
                return combined;
            }

            if (figure == null)
            {
                readableMeshes = new PlayerBodyMeshes(false, body.Content);
                var readable = readableMeshes.For(parts, body.transform, body.Catalog, body.Worn, body.Held);
                var transforms = new Dictionary<string, Transform>(StringComparer.Ordinal);
                foreach (var child in body.GetComponentsInChildren<Transform>(true))
                {
                    transforms[child.name] = child;
                }

                var rendererObject = new GameObject(FigureRendererName);
                rendererObject.transform.SetParent(body.transform, false);
                figure = rendererObject.AddComponent<SkinnedMeshRenderer>();
                figure.sharedMesh = readable.Mesh;
                var bones = new Transform[readable.BoneNames.Count];
                for (var index = 0; index < bones.Length; index++)
                {
                    bones[index] = transforms[readable.BoneNames[index]];
                }

                figure.bones = bones;
                figure.rootBone = transforms.TryGetValue(readable.RootBoneName, out var root) ? root : body.transform;
                figure.updateWhenOffscreen = true;
                figure.forceRenderingOff = true;
            }

            return figure;
        }

        private Transform Bone(string name)
        {
            var bone = PlayerBodyMeshes.FindDescendant(body.transform, name);
            if (bone == null)
            {
                throw new InvalidOperationException("The body " + body.name + " has no bone " + name + ".");
            }

            return bone;
        }

        private AnimationClip ClipNamed(string clip)
        {
            foreach (var candidate in animator.runtimeAnimatorController.animationClips)
            {
                if (candidate.name == clip)
                {
                    return candidate;
                }
            }

            throw new InvalidOperationException("The Animator controller of " + body.name + " plays no clip " + clip + ".");
        }
    }
}
