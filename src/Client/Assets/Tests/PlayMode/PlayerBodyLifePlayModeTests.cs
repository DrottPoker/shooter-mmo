using System.Collections;
using Mmorpg.Client.Gameplay;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Mmorpg.Client.Tests.PlayMode
{
    // The player body plays the handmade Death clip from its Animator when its character dies.
    // See docs/UNITY_CLIENT_ARCHITECTURE.md, Player Bodies.
    public sealed class PlayerBodyLifePlayModeTests
    {
        private const string BodyPrefabPath = "Assets/Prefabs/Player/HumanMaleBody.prefab";
        private static readonly int DeathState = Animator.StringToHash(HumanLocomotion.DeathState);
        private static readonly int IdleState = Animator.StringToHash(HumanLocomotion.IdleState);

        private GameObject body;

        [TearDown]
        public void DestroyBody()
        {
            if (body != null)
            {
                Object.Destroy(body);
            }
        }

        [UnityTest]
        public IEnumerator ABodyThatDiesInViewFallsIntoDeath()
        {
            var life = Spawn();
            yield return null;
            life.ShowLifeState(false);
            yield return null;
            life.ShowLifeState(true);
            yield return null;
            yield return null;

            var animator = life.GetComponent<Animator>();
            var entering = animator.IsInTransition(0)
                && animator.GetNextAnimatorStateInfo(0).shortNameHash == DeathState;
            var falling = animator.GetCurrentAnimatorStateInfo(0).shortNameHash == DeathState
                && animator.GetCurrentAnimatorStateInfo(0).normalizedTime < 0.5f;
            Assert.That(entering || falling, Is.True, "the body does not start to fall");
        }

        [UnityTest]
        public IEnumerator ABodyFirstSeenDeadLiesInTheEndPoseAtOnce()
        {
            var life = Spawn();
            yield return null;
            life.ShowLifeState(true);
            yield return null;
            yield return null;

            var state = life.GetComponent<Animator>().GetCurrentAnimatorStateInfo(0);
            Assert.That(state.shortNameHash, Is.EqualTo(DeathState));
            Assert.That(state.normalizedTime, Is.GreaterThanOrEqualTo(1f));
        }

        [UnityTest]
        public IEnumerator ADeadBodyLiesDownAgainAfterItsAnimatorStartsOver()
        {
            var life = Spawn();
            yield return null;
            life.ShowLifeState(true);
            yield return null;
            var animator = life.GetComponent<Animator>();
            animator.Rebind();
            yield return null;
            yield return null;
            yield return null;

            var state = animator.GetCurrentAnimatorStateInfo(0);
            Assert.That(state.shortNameHash, Is.EqualTo(DeathState));
            Assert.That(state.normalizedTime, Is.GreaterThanOrEqualTo(1f));
        }

        [UnityTest]
        public IEnumerator ARespawnedBodyStandsInIdleAtOnce()
        {
            var life = Spawn();
            yield return null;
            life.ShowLifeState(true);
            yield return null;
            life.ShowLifeState(false);
            yield return null;
            yield return null;

            var animator = life.GetComponent<Animator>();
            Assert.That(animator.GetCurrentAnimatorStateInfo(0).shortNameHash, Is.EqualTo(IdleState));
            Assert.That(animator.IsInTransition(0), Is.False);
            Assert.That(life.IsDead, Is.False);
        }

        private CharacterLocomotionAnimator Spawn()
        {
#if UNITY_EDITOR
            var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(BodyPrefabPath);
#else
            GameObject prefab = null;
#endif
            Assert.That(prefab, Is.Not.Null, BodyPrefabPath);
            body = Object.Instantiate(prefab);
            return body.GetComponent<CharacterLocomotionAnimator>();
        }
    }
}
