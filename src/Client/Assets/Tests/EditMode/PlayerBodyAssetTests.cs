using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Mmorpg.Client.Gameplay;
using Mmorpg.WorldData.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Mmorpg.Client.Tests.EditMode
{
    // The human bodies that replace the player capsule: the body prefabs, their Animator
    // controller, the body models, and how the player prefabs show them. See
    // docs/UNITY_CLIENT_ARCHITECTURE.md.
    public sealed class PlayerBodyAssetTests
    {
        private const string MaleBodyPrefabPath = "Assets/Prefabs/Player/HumanMaleBody.prefab";
        private const string FemaleBodyPrefabPath = "Assets/Prefabs/Player/HumanFemaleBody.prefab";
        private const string BodyModelsPath = "Assets/Content/Player/PlayerBodyModels.asset";
        private const string ControllerPath = "Assets/Art/Animation/HumanLocomotion.controller";
        private const string ActionsModelPath = "Assets/Art/Models/Animations/Human_Actions.fbx";
        private const string AnimateScript = "tools/blender/human_base/animate.py";

        private static readonly string[] PlayerPrefabPaths =
        {
            "Assets/Prefabs/Player/LocalPlayer.prefab",
            "Assets/Prefabs/Player/RemotePlayer.prefab",
        };

        [TestCase(MaleBodyPrefabPath, "Human_Male", "Male")]
        [TestCase(FemaleBodyPrefabPath, "Human_Female", "Female")]
        public void TheBodyIsAVariantOfItsModelWithItsAnimatorAndLook(string bodyPath, string modelName, string materialSuffix)
        {
            var modelPath = "Assets/Art/Models/Characters/" + modelName + ".fbx";
            var body = AssetDatabase.LoadAssetAtPath<GameObject>(bodyPath);
            Assert.That(body, Is.Not.Null);
            Assert.That(PrefabUtility.GetPrefabAssetType(body), Is.EqualTo(PrefabAssetType.Variant));
            Assert.That(
                AssetDatabase.GetAssetPath(PrefabUtility.GetCorrespondingObjectFromOriginalSource(body)),
                Is.EqualTo(modelPath));

            var animator = body.GetComponent<Animator>();
            Assert.That(animator, Is.Not.Null);
            Assert.That(AssetDatabase.GetAssetPath(animator.runtimeAnimatorController), Is.EqualTo(ControllerPath));
            Assert.That(animator.avatar, Is.Not.Null);
            Assert.That(animator.avatar.isHuman, Is.True);
            Assert.That(AssetDatabase.GetAssetPath(animator.avatar), Is.EqualTo(modelPath));
            // The game moves the character; the clips play in place.
            Assert.That(animator.applyRootMotion, Is.False);
            Assert.That(animator.cullingMode, Is.EqualTo(AnimatorCullingMode.CullUpdateTransforms));
            Assert.That(body.GetComponent<CharacterLocomotionAnimator>(), Is.Not.Null);
            Assert.That(body.GetComponent<ClothChainSolver>(), Is.Not.Null);

            // The skin is the body and the face, not the eyes, teeth, or tongue.
            var appearance = body.GetComponent<PlayerBodyAppearance>();
            Assert.That(appearance, Is.Not.Null);
            Assert.That(
                appearance.SkinMaterials.Select(material => material.name),
                Is.EquivalentTo(new[] { "M_Body_" + materialSuffix, "M_Face_" + materialSuffix }));
            Assert.That(
                AssetDatabase.GetAssetPath(appearance.Catalog),
                Is.EqualTo("Assets/Art/Appearance/" + modelName + "_Appearance.asset"));
            Assert.That(body.GetComponentsInChildren<Collider>(true), Is.Empty);
        }

        [Test]
        public void TheBodyModelsAreTheMaleBodyFirstAndTheFemaleBody()
        {
            var models = AssetDatabase.LoadAssetAtPath<PlayerBodyModels>(BodyModelsPath);
            Assert.That(models, Is.Not.Null);
            Assert.That(models.Models.Select(model => model.Name), Is.EqualTo(new[] { "Human_Male", "Human_Female" }));
            Assert.That(
                models.Models.Select(model => AssetDatabase.GetAssetPath(model.Body)),
                Is.EqualTo(new[] { MaleBodyPrefabPath, FemaleBodyPrefabPath }));
            Assert.That(models.Default, Is.SameAs(models.Models[0]));
        }

        [Test]
        public void ThePlayersShowABodyModelInPlaceOfTheCapsule()
        {
            var models = AssetDatabase.LoadAssetAtPath<PlayerBodyModels>(BodyModelsPath);
            foreach (var path in PlayerPrefabPaths)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                var visual = prefab.transform.Find("PlayerVisual");
                Assert.That(visual, Is.Not.Null, path);
                var slot = visual.GetComponent<PlayerBodySlot>();
                Assert.That(slot, Is.Not.Null, path);
                Assert.That(slot.Models, Is.SameAs(models), path);
                // A player instantiates only the body it shows.
                Assert.That(prefab.GetComponentsInChildren<PlayerBodyAppearance>(true), Is.Empty, path);
                Assert.That(
                    prefab.GetComponentsInChildren<MeshRenderer>(true),
                    Is.Empty,
                    path + " still has a primitive body part");
            }
        }

        [Test]
        public void TheControllerBlendsWalkAndJogBySpeedAndStandsInIdle()
        {
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
            Assert.That(controller, Is.Not.Null);
            Assert.That(
                controller.parameters.Select(parameter => (parameter.name, parameter.type)),
                Is.EquivalentTo(new[]
                {
                    (HumanLocomotion.MovingParameter, AnimatorControllerParameterType.Bool),
                    (HumanLocomotion.SpeedParameter, AnimatorControllerParameterType.Float),
                    (HumanLocomotion.RateParameter, AnimatorControllerParameterType.Float),
                    (HumanLocomotion.DeadParameter, AnimatorControllerParameterType.Bool),
                }));
            Assert.That(
                controller.parameters.Single(parameter => parameter.name == HumanLocomotion.RateParameter).defaultFloat,
                Is.EqualTo(1f));
            // The locomotion, then a grip layer for each hand.
            Assert.That(controller.layers, Has.Length.EqualTo(3));

            var machine = controller.layers[0].stateMachine;
            var states = machine.states.Select(child => child.state).ToDictionary(state => state.name);
            // The tools' clips have states of their own, which the body enters while it uses a tool.
            Assert.That(
                states.Keys,
                Is.EquivalentTo(new[]
                {
                    HumanLocomotion.IdleState, HumanLocomotion.LocomotionState, HumanLocomotion.DeathState,
                    HumanLocomotion.MineClip, HumanLocomotion.ChopClip,
                }));
            var idle = states[HumanLocomotion.IdleState];
            var locomotion = states[HumanLocomotion.LocomotionState];
            Assert.That(machine.defaultState, Is.SameAs(idle));
            AssertClip(idle.motion, HumanLocomotion.IdleClip);

            Assert.That(locomotion.speedParameterActive, Is.True);
            Assert.That(locomotion.speedParameter, Is.EqualTo(HumanLocomotion.RateParameter));
            var tree = locomotion.motion as BlendTree;
            Assert.That(tree, Is.Not.Null);
            Assert.That(tree.blendType, Is.EqualTo(BlendTreeType.Simple1D));
            Assert.That(tree.blendParameter, Is.EqualTo(HumanLocomotion.SpeedParameter));
            Assert.That(tree.useAutomaticThresholds, Is.False);
            Assert.That(tree.children, Has.Length.EqualTo(2));
            AssertClip(tree.children[0].motion, HumanLocomotion.WalkClip);
            Assert.That(tree.children[0].threshold, Is.EqualTo(HumanLocomotion.WalkSpeed));
            AssertClip(tree.children[1].motion, HumanLocomotion.JogClip);
            Assert.That(tree.children[1].threshold, Is.EqualTo(HumanLocomotion.JogSpeed));

            AssertTransition(idle, locomotion, AnimatorConditionMode.If);
            AssertTransition(locomotion, idle, AnimatorConditionMode.IfNot);
        }

        [Test]
        public void EveryStateFallsIntoTheHandmadeDeathWhileDead()
        {
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
            var machine = controller.layers[0].stateMachine;
            var states = machine.states.Select(child => child.state).ToDictionary(state => state.name);
            var death = states[HumanLocomotion.DeathState];
            var clip = death.motion as AnimationClip;
            Assert.That(clip, Is.Not.Null);
            Assert.That(clip.name, Is.EqualTo(HumanLocomotion.DeathClip));
            Assert.That(AssetDatabase.GetAssetPath(clip), Is.EqualTo(ActionsModelPath));
            Assert.That(clip.isHumanMotion, Is.True);
            // Death holds its last pose, lying on the ground.
            Assert.That(clip.isLooping, Is.False);

            var into = machine.anyStateTransitions.Where(transition => transition.destinationState == death).ToArray();
            Assert.That(into, Has.Length.EqualTo(1));
            Assert.That(machine.anyStateTransitions, Has.Length.EqualTo(1));
            Assert.That(into[0].canTransitionToSelf, Is.False);
            Assert.That(into[0].hasExitTime, Is.False);
            Assert.That(into[0].hasFixedDuration, Is.True);
            Assert.That(into[0].duration, Is.EqualTo(HumanLocomotion.DeathCrossfadeSeconds));
            AssertCondition(into[0], AnimatorConditionMode.If, HumanLocomotion.DeadParameter);

            var back = death.transitions.Single();
            Assert.That(back.destinationState, Is.SameAs(states[HumanLocomotion.IdleState]));
            Assert.That(back.hasExitTime, Is.False);
            AssertCondition(back, AnimatorConditionMode.IfNot, HumanLocomotion.DeadParameter);
        }

        [TestCase(1, HumanLocomotion.MainHandGripLayer, AvatarMaskBodyPart.RightFingers)]
        [TestCase(2, HumanLocomotion.OffHandGripLayer, AvatarMaskBodyPart.LeftFingers)]
        public void TheFingersOfAHandThatHoldsSomethingCloseAroundItsGrip(int index, string name, AvatarMaskBodyPart fingers)
        {
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
            var layer = controller.layers[index];
            Assert.That(layer.name, Is.EqualTo(name));
            // PlayerBodyAppearance turns the layer on while the hand's socket holds an attachment.
            Assert.That(layer.defaultWeight, Is.EqualTo(0f));
            Assert.That(layer.blendingMode, Is.EqualTo(AnimatorLayerBlendingMode.Override));
            Assert.That(layer.iKPass, Is.False);
            Assert.That(layer.avatarMask, Is.Not.Null, name);
            foreach (AvatarMaskBodyPart part in System.Enum.GetValues(typeof(AvatarMaskBodyPart)))
            {
                if (part != AvatarMaskBodyPart.LastBodyPart)
                {
                    Assert.That(layer.avatarMask.GetHumanoidBodyPartActive(part), Is.EqualTo(part == fingers), name + " " + part);
                }
            }

            var state = layer.stateMachine.states.Single().state;
            Assert.That(state.name, Is.EqualTo(HumanLocomotion.GripState));
            Assert.That(layer.stateMachine.defaultState, Is.SameAs(state));
            AssertClip(state.motion, HumanLocomotion.GripClip);
            var clip = (AnimationClip)state.motion;
            Assert.That(clip.isHumanMotion, Is.True);
            Assert.That(clip.isLooping, Is.True);
        }

        [Test]
        public void TheLocomotionSpeedsAreTheSpeedsTheClipsWereAuthoredFor()
        {
            var script = File.ReadAllText(Path.Combine(RepositoryLayout.FindRoot(), AnimateScript));
            Assert.That(AuthoredSpeed(script, "WALK_SPEED"), Is.EqualTo(HumanLocomotion.WalkSpeed));
            Assert.That(AuthoredSpeed(script, "JOG_SPEED"), Is.EqualTo(HumanLocomotion.JogSpeed));
        }

        private static float AuthoredSpeed(string script, string name)
        {
            var match = Regex.Match(script, "^" + name + @" = ([0-9.]+)\s*$", RegexOptions.Multiline);
            Assert.That(match.Success, Is.True, AnimateScript + " defines no " + name);
            return float.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        }

        private static void AssertClip(Motion motion, string name)
        {
            var clip = motion as AnimationClip;
            Assert.That(clip, Is.Not.Null, name);
            Assert.That(clip.name, Is.EqualTo(name));
            // Every clip comes from the animation library, which no rebuild of the body writes.
            Assert.That(AssetDatabase.GetAssetPath(clip), Is.EqualTo(ActionsModelPath), name);
        }

        private static void AssertTransition(AnimatorState from, AnimatorState to, AnimatorConditionMode mode)
        {
            var transitions = from.transitions.Where(transition => transition.destinationState == to).ToArray();
            Assert.That(transitions, Has.Length.EqualTo(1), from.name + " to " + to.name);
            var transition = transitions[0];
            Assert.That(transition.hasExitTime, Is.False, from.name);
            Assert.That(transition.hasFixedDuration, Is.True, from.name);
            Assert.That(transition.duration, Is.InRange(0.05f, 0.3f), from.name);
            AssertCondition(transition, mode, HumanLocomotion.MovingParameter);
        }

        private static void AssertCondition(AnimatorStateTransition transition, AnimatorConditionMode mode, string parameter)
        {
            Assert.That(transition.conditions, Has.Length.EqualTo(1), parameter);
            Assert.That(transition.conditions[0].mode, Is.EqualTo(mode), parameter);
            Assert.That(transition.conditions[0].parameter, Is.EqualTo(parameter), parameter);
        }
    }
}
