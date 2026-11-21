using System.Collections;
using System.Linq;
using Mmorpg.Client.Gameplay;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Mmorpg.Client.Tests.PlayMode
{
    // The player body plays the handmade Death clip from its Animator when its character dies, and
    // closes its eyes. See docs/UNITY_CLIENT_ARCHITECTURE.md, Player Bodies and Eyelids.
    public sealed class PlayerBodyLifePlayModeTests
    {
        private const string BodyPrefabPath = "Assets/Prefabs/Player/HumanMaleBody.prefab";
        private static readonly int DeathState = Animator.StringToHash(HumanLocomotion.DeathState);
        private static readonly int IdleState = Animator.StringToHash(HumanLocomotion.IdleState);

        private GameObject body;
        private PlayerBodyMeshes meshes;

        [TearDown]
        public void DestroyBody()
        {
            if (body != null)
            {
                Object.Destroy(body);
            }

            meshes?.Dispose();
            meshes = null;
        }

        [UnityTest]
        public IEnumerator ABodyThatDiesClosesItsEyesAndOpensThemWhenItStandsUp()
        {
            var life = SpawnCombined();
            var lids = life.GetComponent<PlayerBodyAppearance>().Eyelids;
            Assert.That(lids, Is.Not.Null);
            Assert.That(EyelidScheduler.Holds(lids), Is.True, "the body's lids are in play");
            yield return null;
            life.ShowLifeState(false);
            yield return null;
            Assert.That(lids.Share, Is.EqualTo(0f), "no view is near, so the living body does not blink");

            // It dies in view: the eyes close at the blink's closing speed and stay closed.
            life.ShowLifeState(true);
            var start = Time.time;
            while (lids.Share < 1f && Time.time - start < 1f)
            {
                yield return null;
            }

            Assert.That(lids.Share, Is.EqualTo(1f));
            AssertLids(lids, 1f);

            // The Animator plays Death and starts over without touching the lids.
            var animator = life.GetComponent<Animator>();
            animator.Rebind();
            yield return null;
            yield return null;
            AssertLids(lids, 1f);

            // It stands up: the eyes open at once.
            life.ShowLifeState(false);
            Assert.That(lids.Share, Is.EqualTo(0f));
            AssertLids(lids, 0f);
        }

        [UnityTest]
        public IEnumerator ABodyFirstSeenDeadHasItsEyesClosedAtOnce()
        {
            var life = SpawnCombined();
            var lids = life.GetComponent<PlayerBodyAppearance>().Eyelids;
            yield return null;
            life.ShowLifeState(true);
            Assert.That(lids.Share, Is.EqualTo(1f));
            yield return null;
            yield return null;
            AssertLids(lids, 1f);
        }

        [UnityTest]
        public IEnumerator ADeadBodyThatIsEnabledAgainKeepsItsEyesClosed()
        {
            var life = SpawnCombined();
            var lids = life.GetComponent<PlayerBodyAppearance>().Eyelids;
            yield return null;
            life.ShowLifeState(true);
            body.SetActive(false);
            Assert.That(EyelidScheduler.Holds(lids), Is.False, "a disabled body leaves the scheduler");
            yield return null;
            body.SetActive(true);
            Assert.That(EyelidScheduler.Holds(lids), Is.True);
            yield return null;
            Assert.That(lids.Share, Is.EqualTo(1f));
            AssertLids(lids, 1f);
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

        [UnityTest]
        public IEnumerator APlayerCorpseIsTheBodyLyingInTheLastPoseOfDeath()
        {
            Assert.That(PlayerCorpseBodies.IsPlayerCorpse(PlayerCorpseBodies.PresentationKey), Is.True);
            // The key a Mob corpse sends keeps the generic capsule.
            Assert.That(PlayerCorpseBodies.IsPlayerCorpse("corpse.mob"), Is.False);
            var prefab = Spawn().GetComponent<PlayerBodyAppearance>();
            yield return null;
            using (var content = new PlayerBodyContent())
            using (var meshes = new PlayerBodyMeshes(false, content))
            using (var bodyMaterials = new PlayerBodyMaterials(content))
            using (var corpses = new PlayerCorpseBodies())
            {
                var plainKey = PlayerCorpseBodies.KeyOf("Human_Male", BodyLook.Default);
                // A lying body waits for the content of its look.
                Assert.That(corpses.TryGet(plainKey, prefab, BodyLook.Default, meshes, bodyMaterials, out _, out _, out var loading), Is.False);
                Assert.That(loading, Is.True);
                yield return PlayModeBodyContent.Load(content, prefab.Catalog, BodyLook.Default);
                Assert.That(corpses.TryGet(plainKey, prefab, BodyLook.Default, meshes, bodyMaterials, out var mesh, out var materials), Is.True);
                Assert.That(corpses.TryGet(plainKey, prefab, BodyLook.Default, meshes, bodyMaterials, out var again, out _), Is.True);
                Assert.That(again, Is.SameAs(mesh), "every player corpse of one look shares one baked body");
                Assert.That(materials, Is.Not.Empty);
                // The corpse wears the body's underwear, which is paint over its skin.
                Assert.That(materials.Any(material => material.HasProperty("_PaintCount") && material.GetFloat("_PaintCount") >= 1f),
                    Is.True);

                // Lying on the ground: low and long, not standing.
                var bounds = mesh.bounds;
                Assert.That(bounds.max.y, Is.LessThan(0.6f));
                Assert.That(bounds.min.y, Is.GreaterThan(-0.1f));
                Assert.That(Mathf.Max(bounds.size.x, bounds.size.z), Is.GreaterThan(1.3f));

                // Another look, such as a corpse still wearing a cloak, bakes a body of its own,
                // and a look no corpse shows any more is destroyed.
                var cloaked = BodyLook.Default.WithGarment("WoolCloak", true);
                var cloakedKey = PlayerCorpseBodies.KeyOf("Human_Male", cloaked);
                yield return PlayModeBodyContent.Load(content, prefab.Catalog, cloaked);
                Assert.That(corpses.TryGet(cloakedKey, prefab, cloaked, meshes, bodyMaterials, out var wrapped, out _), Is.True);
                Assert.That(wrapped, Is.Not.SameAs(mesh));
                Assert.That(wrapped.vertexCount, Is.GreaterThan(mesh.vertexCount));
                Assert.That(corpses.Count, Is.EqualTo(2));
                corpses.KeepOnly(new[] { cloakedKey });
                yield return null;
                Assert.That(corpses.Count, Is.EqualTo(1));
                Assert.That(mesh == null, Is.True);

                // A lying body holds the content of its look while it exists, and lets go of it
                // when it goes.
                var cloak = prefab.Catalog.GarmentNamed("WoolCloak").Address;
                content.ReleaseUnused();
                Assert.That(content.PieceOf(cloak), Is.Not.Null);
                corpses.KeepOnly(new string[0]);
                content.ReleaseUnused();
                Assert.That(content.PieceOf(cloak), Is.Null);
            }
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

        // A body combined as a World combines it, which finds its lids. Its Animator poses it
        // every frame, also unseen, so a test sees that the Animator leaves the lids alone.
        private CharacterLocomotionAnimator SpawnCombined()
        {
            var life = Spawn();
            body.GetComponent<Animator>().cullingMode = AnimatorCullingMode.AlwaysAnimate;
            meshes = new PlayerBodyMeshes();
            body.GetComponent<PlayerBodyAppearance>().CombineParts(meshes);
            return life;
        }

        // Every lid bone is turned by the share of its closed turn.
        private static void AssertLids(BodyEyelids lids, float share)
        {
            for (var index = 0; index < lids.Bones.Count; index++)
            {
                Assert.That(Quaternion.Angle(lids.Bones[index].localRotation, lids.RotationAt(index, share)), Is.LessThan(0.01f),
                    lids.Bones[index].name);
            }
        }
    }
}
