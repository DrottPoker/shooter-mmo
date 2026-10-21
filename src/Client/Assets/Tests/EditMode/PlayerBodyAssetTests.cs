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
    // The human body that replaces the player capsule: the body prefab, its Animator
    // controller, and its place in the player prefabs. See docs/UNITY_CLIENT_ARCHITECTURE.md.
    public sealed class PlayerBodyAssetTests
    {
        private const string BodyPrefabPath = "Assets/Prefabs/Player/HumanMaleBody.prefab";
        private const string BodyModelPath = "Assets/Art/Models/Characters/Human_Male.fbx";
        private const string ControllerPath = "Assets/Art/Animation/HumanLocomotion.controller";
        private const string AnimateScript = "tools/blender/human_base/animate.py";

        private static readonly string[] PlayerPrefabPaths =
        {
            "Assets/Prefabs/Player/LocalPlayer.prefab",
            "Assets/Prefabs/Player/RemotePlayer.prefab",
        };

        [Test]
        public void TheBodyIsAVariantOfTheMaleModelWithItsAnimatorAndLook()
        {
            var body = AssetDatabase.LoadAssetAtPath<GameObject>(BodyPrefabPath);
            Assert.That(body, Is.Not.Null);
            Assert.That(PrefabUtility.GetPrefabAssetType(body), Is.EqualTo(PrefabAssetType.Variant));
            Assert.That(
                AssetDatabase.GetAssetPath(PrefabUtility.GetCorrespondingObjectFromOriginalSource(body)),
                Is.EqualTo(BodyModelPath));

            var animator = body.GetComponent<Animator>();
            Assert.That(animator, Is.Not.Null);
            Assert.That(AssetDatabase.GetAssetPath(animator.runtimeAnimatorController), Is.EqualTo(ControllerPath));
            Assert.That(animator.avatar, Is.Not.Null);
            Assert.That(animator.avatar.isHuman, Is.True);
            Assert.That(AssetDatabase.GetAssetPath(animator.avatar), Is.EqualTo(BodyModelPath));
            // The game moves the character; the clips play in place.
            Assert.That(animator.applyRootMotion, Is.False);
            Assert.That(animator.cullingMode, Is.EqualTo(AnimatorCullingMode.CullUpdateTransforms));
            Assert.That(body.GetComponent<CharacterLocomotionAnimator>(), Is.Not.Null);

            // The skin is the body and the face, not the eyes, teeth, or tongue.
            var appearance = body.GetComponent<PlayerBodyAppearance>();
            Assert.That(appearance, Is.Not.Null);
            Assert.That(
                appearance.SkinMaterials.Select(material => material.name),
                Is.EquivalentTo(new[] { "M_Body_Male", "M_Face_Male" }));
            Assert.That(body.GetComponentsInChildren<Collider>(true), Is.Empty);
        }

        [Test]
        public void ThePlayersShowTheBodyInPlaceOfTheCapsule()
        {
            var body = AssetDatabase.LoadAssetAtPath<GameObject>(BodyPrefabPath);
            foreach (var path in PlayerPrefabPaths)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                var visual = prefab.transform.Find("PlayerVisual");
                Assert.That(visual, Is.Not.Null, path);
                var bodies = prefab.GetComponentsInChildren<PlayerBodyAppearance>(true);
                Assert.That(bodies, Has.Length.EqualTo(1), path);
                var instance = bodies[0].gameObject;
                Assert.That(instance.transform.parent, Is.EqualTo(visual), path);
                Assert.That(PrefabUtility.GetCorrespondingObjectFromSource(instance), Is.EqualTo(body), path);
                Assert.That(instance.transform.localPosition, Is.EqualTo(Vector3.zero), path);
                Assert.That(instance.transform.localRotation, Is.EqualTo(Quaternion.identity), path);
                Assert.That(instance.transform.localScale, Is.EqualTo(Vector3.one), path);
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
                }));
            Assert.That(
                controller.parameters.Single(parameter => parameter.name == HumanLocomotion.RateParameter).defaultFloat,
                Is.EqualTo(1f));
            Assert.That(controller.layers, Has.Length.EqualTo(1));

            var machine = controller.layers[0].stateMachine;
            var states = machine.states.Select(child => child.state).ToDictionary(state => state.name);
            Assert.That(states.Keys, Is.EquivalentTo(new[] { HumanLocomotion.IdleState, HumanLocomotion.LocomotionState }));
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
            Assert.That(AssetDatabase.GetAssetPath(clip), Is.EqualTo(BodyModelPath), name);
        }

        private static void AssertTransition(AnimatorState from, AnimatorState to, AnimatorConditionMode mode)
        {
            var transitions = from.transitions.Where(transition => transition.destinationState == to).ToArray();
            Assert.That(transitions, Has.Length.EqualTo(1), from.name + " to " + to.name);
            var transition = transitions[0];
            Assert.That(transition.hasExitTime, Is.False, from.name);
            Assert.That(transition.hasFixedDuration, Is.True, from.name);
            Assert.That(transition.duration, Is.InRange(0.05f, 0.3f), from.name);
            Assert.That(transition.conditions, Has.Length.EqualTo(1), from.name);
            Assert.That(transition.conditions[0].mode, Is.EqualTo(mode), from.name);
            Assert.That(transition.conditions[0].parameter, Is.EqualTo(HumanLocomotion.MovingParameter), from.name);
        }
    }
}
