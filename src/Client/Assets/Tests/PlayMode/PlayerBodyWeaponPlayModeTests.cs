using System.Collections;
using Mmorpg.Client.Gameplay;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Mmorpg.Client.Tests.PlayMode
{
    // A body that holds a staff keeps its arm in the hold pose, plays Cast once over its upper body
    // when its attack releases, and lets the arm swing again when it puts the staff away. See
    // docs/UNITY_CLIENT_ARCHITECTURE.md, Player Bodies.
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

            Assert.That(appearance.PlayAttack(), Is.True);
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
            Assert.That(appearance.PlayAttack(), Is.False);
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
