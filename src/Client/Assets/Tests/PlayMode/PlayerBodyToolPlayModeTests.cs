using System.Collections;
using System.Linq;
using Mmorpg.Client.Gameplay;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Mmorpg.Client.Tests.PlayMode
{
    // A body that holds a tool plays the tool's clip while it stands, walks without it, and pins
    // its other hand on the haft after the Animator posed it. See docs/UNITY_CLIENT_ARCHITECTURE.md,
    // Player Bodies.
    public sealed class PlayerBodyToolPlayModeTests
    {
        private const string BodyPrefabPath = "Assets/Prefabs/Player/HumanFemaleBody.prefab";
        private static readonly int MineState = Animator.StringToHash(HumanLocomotion.MineClip);
        private static readonly int IdleState = Animator.StringToHash(HumanLocomotion.IdleState);
        private static readonly int LocomotionState = Animator.StringToHash(HumanLocomotion.LocomotionState);

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
        public IEnumerator ABodyHoldingAToolSwingsItWhileItStandsAndHoldsItWithBothHands()
        {
            var appearance = Spawn();
            yield return null;
            appearance.ApplyLook(BodyLook.Default.WithAttachment("MiningPickaxe", true), materials);
            var animator = appearance.GetComponent<Animator>();
            var locomotion = appearance.GetComponent<CharacterLocomotionAnimator>();
            Assert.That(locomotion.UseClip, Is.EqualTo(HumanLocomotion.MineClip));
            // The crossfade takes a fixed time, however short the frames are.
            var start = Time.time;
            while (locomotion.UseWeight < 1f && Time.time - start < 2f)
            {
                yield return null;
            }

            Assert.That(animator.GetCurrentAnimatorStateInfo(0).shortNameHash, Is.EqualTo(MineState));
            Assert.That(locomotion.UseWeight, Is.EqualTo(1f));

            // LateUpdate pinned the other hand on the haft this frame.
            yield return new WaitForEndOfFrame();
            var mainSocket = Bone(HumanLocomotion.MainHandSocketBone);
            var offSocket = Bone(HumanLocomotion.OffHandSocketBone);
            OffHandToolGrip.Target(mainSocket, appearance.Tool.OffHandGrip, out var position, out var rotation);
            Assert.That((offSocket.position - position).magnitude, Is.LessThan(0.001f));
            Assert.That(Quaternion.Angle(offSocket.rotation, rotation), Is.LessThan(1f));

            // Walking away, the body leaves the swing while it still holds the tool.
            for (var frame = 0; frame < 20; frame++)
            {
                body.transform.position += Vector3.forward * (HumanLocomotion.WalkSpeed * Time.deltaTime);
                yield return null;
            }

            Assert.That(NextOrCurrent(animator), Is.EqualTo(LocomotionState));

            // Standing again with the tool put away, it stands in Idle.
            appearance.ApplyLook(BodyLook.Default, materials);
            Assert.That(locomotion.UseClip, Is.Empty);
            // Its smoothed speed falls below walking within a fixed time.
            start = Time.time;
            while (NextOrCurrent(animator) != IdleState && Time.time - start < 2f)
            {
                yield return null;
            }

            Assert.That(NextOrCurrent(animator), Is.EqualTo(IdleState));
            Assert.That(locomotion.UseWeight, Is.EqualTo(0f));
        }

        private static int NextOrCurrent(Animator animator)
        {
            return animator.IsInTransition(0)
                ? animator.GetNextAnimatorStateInfo(0).shortNameHash
                : animator.GetCurrentAnimatorStateInfo(0).shortNameHash;
        }

        private Transform Bone(string name)
        {
            return body.GetComponentsInChildren<Transform>().Single(candidate => candidate.name == name);
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
