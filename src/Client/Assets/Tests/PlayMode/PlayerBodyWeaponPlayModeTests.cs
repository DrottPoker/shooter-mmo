using System.Collections;
using Mmorpg.Client.Gameplay;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Mmorpg.Client.Tests.PlayMode
{
    // A body that holds a staff keeps its arm in the hold pose, plays Cast once over its body when
    // its attack starts, timed to its release, ends it when the attack is cancelled, and lets the arm
    // swing again when it puts the staff away. See docs/UNITY_CLIENT_ARCHITECTURE.md, Player Bodies.
    public sealed class PlayerBodyWeaponPlayModeTests
    {
        private const string BodyPrefabPath = "Assets/Prefabs/Player/HumanMaleBody.prefab";

        private GameObject body;
        private PlayerBodyMeshes meshes;
        private PlayerBodyMaterials materials;

        [TearDown]
        public void DestroyBody()
        {
            if (body != null)
            {
                Object.Destroy(body);
            }

            meshes?.Dispose();
            materials?.Dispose();
        }

        [UnityTest]
        public IEnumerator ABodyHoldingAStaffCastsOnceAndHoldsItAgain()
        {
            var appearance = Spawn();
            yield return null;
            appearance.ApplyLook(BodyLook.Default.WithAttachment("OakStaff", true), materials);
            var animator = appearance.GetComponent<Animator>();
            var locomotion = appearance.GetComponent<CharacterLocomotionAnimator>();
            var holdLayer = animator.GetLayerIndex(HumanLocomotion.MainHandHoldLayer);
            var actionLayer = animator.GetLayerIndex(HumanLocomotion.ActionLayer);
            // The body asks for the state in its Update; the Animator enters it when it runs later.
            yield return null;
            yield return null;
            Assert.That(animator.GetLayerWeight(holdLayer), Is.EqualTo(1f));
            Assert.That(animator.GetCurrentAnimatorStateInfo(holdLayer).shortNameHash,
                Is.EqualTo(Animator.StringToHash(HumanLocomotion.HoldStaffClip)));
            Assert.That(animator.GetLayerWeight(actionLayer), Is.Zero);

            // Released after the clip's own windup, the clip plays from its start.
            Assert.That(appearance.PlayAttack(false, appearance.Weapon.AttackRelease), Is.True);
            var start = Time.time;
            var strongest = 0f;
            while (Time.time - start < 2f && (strongest < 1f || locomotion.ActionWeight > 0f))
            {
                yield return null;
                strongest = Mathf.Max(strongest, locomotion.ActionWeight);
            }

            // Cast lasts 0.9 s: it reaches full weight and fades out again by itself.
            Assert.That(strongest, Is.EqualTo(1f));
            Assert.That(locomotion.ActionWeight, Is.Zero);
            Assert.That(Time.time - start, Is.InRange(0.8f, 1.5f));
            Assert.That(animator.GetLayerWeight(actionLayer), Is.Zero);
            Assert.That(animator.GetLayerWeight(holdLayer), Is.EqualTo(1f));

            // Put away, the staff no longer holds the arm, and nothing plays Cast.
            appearance.ApplyLook(BodyLook.Default, materials);
            yield return null;
            Assert.That(locomotion.HoldClip, Is.Empty);
            Assert.That(animator.GetLayerWeight(holdLayer), Is.Zero);
            Assert.That(appearance.PlayAttack(false, 0f), Is.False);
        }

        [UnityTest]
        public IEnumerator AnAttackWaitsForItsWindupAndEndsWhenItIsCancelled()
        {
            var appearance = Spawn();
            yield return null;
            appearance.ApplyLook(BodyLook.Default.WithAttachment("OakStaff", true), materials);
            var locomotion = appearance.GetComponent<CharacterLocomotionAnimator>();
            yield return null;

            // Released half a second later than the clip's own windup, Cast waits that long to start.
            var release = appearance.Weapon.AttackRelease;
            Assert.That(appearance.PlayAttack(false, release + 0.5f), Is.True);
            var start = Time.time;
            while (Time.time - start < 0.4f)
            {
                yield return null;
                Assert.That(locomotion.ActionWeight, Is.Zero, "the clip shows before its start");
            }

            while (Time.time - start < 1.5f && locomotion.ActionWeight < 1f)
            {
                yield return null;
            }

            Assert.That(locomotion.ActionWeight, Is.EqualTo(1f));
            Assert.That(locomotion.IsPlayingAttack(HumanLocomotion.CastClip, out var time), Is.True);
            Assert.That(time, Is.InRange(0f, 0.3f));

            // Cancelled, it fades out at once and is no longer played.
            appearance.StopAttack();
            Assert.That(locomotion.IsPlayingAttack(HumanLocomotion.CastClip, out _), Is.False);
            var cancelled = Time.time;
            while (Time.time - cancelled < 1f && locomotion.ActionWeight > 0f)
            {
                yield return null;
            }

            Assert.That(locomotion.ActionWeight, Is.Zero);
            Assert.That(Time.time - cancelled, Is.LessThan(HumanLocomotion.ActionFadeOutSeconds + 0.1f));
            Assert.That(locomotion.ActionClip, Is.Empty);
        }

        [UnityTest]
        public IEnumerator AnAttackingBodyFacesItsTargetAndTurnsBackAfter()
        {
            var appearance = Spawn();
            yield return null;
            appearance.ApplyLook(BodyLook.Default.WithAttachment("OakStaff", true), materials);
            var locomotion = appearance.GetComponent<CharacterLocomotionAnimator>();
            var target = new GameObject("Target");
            try
            {
                target.transform.position = body.transform.position + (body.transform.right * 5f);
                yield return null;

                // A target at the body's right: the body turns a quarter round toward it at once.
                Assert.That(appearance.PlayAttack(false, appearance.Weapon.AttackRelease, target.transform), Is.True);
                var start = Time.time;
                while (Time.time - start < 0.4f)
                {
                    yield return null;
                }

                Assert.That(locomotion.FacingDegrees, Is.EqualTo(90f).Within(1f));
                var toTarget = target.transform.position - body.transform.position;
                Assert.That(Vector3.Angle(Vector3.ProjectOnPlane(body.transform.forward, Vector3.up), toTarget), Is.LessThan(1f));

                // Once the attack ends and the hold passed, it faces its character's way again.
                while (Time.time - start < 0.9f + CharacterLocomotionAnimator.FacingHoldSeconds + 0.5f)
                {
                    yield return null;
                }

                Assert.That(locomotion.FacingDegrees, Is.Zero);
                Assert.That(Quaternion.Angle(body.transform.localRotation, Quaternion.identity), Is.LessThan(0.01f));
            }
            finally
            {
                Object.Destroy(target);
            }
        }

        private PlayerBodyAppearance Spawn()
        {
#if UNITY_EDITOR
            var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(BodyPrefabPath);
#else
            GameObject prefab = null;
#endif
            Assert.That(prefab, Is.Not.Null, BodyPrefabPath);
            body = Object.Instantiate(prefab);
            meshes = new PlayerBodyMeshes();
            materials = new PlayerBodyMaterials();
            body.GetComponent<Animator>().cullingMode = AnimatorCullingMode.AlwaysAnimate;
            var appearance = body.GetComponent<PlayerBodyAppearance>();
            appearance.CombineParts(meshes);
            return appearance;
        }
    }
}
