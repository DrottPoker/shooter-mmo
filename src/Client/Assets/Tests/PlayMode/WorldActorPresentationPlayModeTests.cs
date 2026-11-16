using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Mmorpg.Client.Combat;
using Mmorpg.Client.Gameplay;
using Mmorpg.Client.WorldActors;
using Mmorpg.GameProtocol;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Mmorpg.Client.Tests.PlayMode
{
    // A change of one world actor applies only that actor's view, not every actor in view, and a
    // clear, as at the end of a session, takes every view away. Every view is moved off its pose
    // first, so a view that an actor change applies stands at its pose again and every other view
    // stays where it was put. Actors that come into view at once get their views over the frames
    // that follow, the nearest first, and an actor whose view waits has none for anyone who asks.
    // See docs/UNITY_CLIENT_ARCHITECTURE.md, World Actor And Interaction Client.
    public sealed class WorldActorPresentationPlayModeTests
    {
        private const int ActorCount = 24;

        // No registry maps this archetype, so every actor shows the development fallback, which
        // needs no prefab load.
        private const string Archetype = "npc.presentation_test";

        private static readonly Vector3 Away = new Vector3(500f, 0f, 500f);

        private GameObject host;

        [TearDown]
        public void DestroyHost()
        {
            if (host != null)
            {
                Object.Destroy(host);
            }
        }

        [UnityTest]
        [Timeout(30000)]
        public IEnumerator AnActorChangeAppliesOnlyThatActorsView()
        {
            host = new GameObject("WorldActorPresentationTest");
            host.SetActive(false);
            var actors = host.AddComponent<WorldActorClientController>();
            var presentation = host.AddComponent<WorldActorPresentationController>();
            presentation.Initialize(actors, null, null);
            var runtimeIds = new Guid[ActorCount + 1];
            for (var entityId = 1; entityId <= ActorCount; entityId++)
            {
                runtimeIds[entityId] = Guid.NewGuid();
                Assert.That(actors.State.TryApplySpawn(Spawn((ulong)entityId, runtimeIds[entityId]), out var error), Is.True, error);
            }

            host.SetActive(true);

            // The registry loads before the first rebuild presents the actors.
            var deadline = Time.realtimeSinceStartup + 20f;
            while (presentation.Views.Count < ActorCount && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            Assert.That(presentation.Views.Count, Is.EqualTo(ActorCount));
            foreach (var view in presentation.Views)
            {
                view.transform.position = Away;
            }

            Assert.That(actors.State.TryApplyState(State(5, 2, 3f), out var stateError), Is.True, stateError);
            Assert.That(presentation.TryGetView(5, out var changed), Is.True);
            Assert.That(changed.transform.position, Is.EqualTo(new Vector3(3f, 0f, -1f)));
            Assert.That(
                presentation.Views.Where(view => view.TargetEntityId != 5).Select(view => view.transform.position),
                Is.All.EqualTo(Away),
                "A change of one actor applied other actors too.");

            var spawned = Guid.NewGuid();
            Assert.That(actors.State.TryApplySpawn(Spawn(ActorCount + 1, spawned), out var spawnError), Is.True, spawnError);
            Assert.That(actors.State.TryRemove(new RealtimeWorldActorDespawn(7, runtimeIds[7], "left"), out var removeError), Is.True, removeError);
            yield return null;
            Assert.That(presentation.TryGetView(ActorCount + 1, out _), Is.True);
            Assert.That(presentation.TryGetView(7, out _), Is.False);
            Assert.That(presentation.Views.Count, Is.EqualTo(ActorCount));
            Assert.That(
                presentation.Views
                    .Where(view => view.TargetEntityId != 5 && view.TargetEntityId != ActorCount + 1)
                    .Select(view => view.transform.position),
                Is.All.EqualTo(Away));

            actors.State.Clear();
            yield return null;
            Assert.That(presentation.Views, Is.Empty);
        }

        // A hundred actors that come into view at once get their views over the frames that follow,
        // the nearest to the eye first: with a budget of nothing, one a frame.
        [UnityTest]
        [Timeout(60000)]
        public IEnumerator ActorsThatComeTogetherGetTheirViewsOverFramesNearestFirst()
        {
            const int arriving = 100;
            var presentation = Host(new PresentationBudget(0.0, null), out var actors);
            for (var entityId = 1; entityId <= arriving; entityId++)
            {
                // Distances from 1 to 100 m in a mixed order of entity ids.
                var distance = ((entityId * 37) % arriving) + 1;
                Assert.That(actors.State.TryApplySpawn(Spawn((ulong)entityId, Guid.NewGuid(), distance), out var error), Is.True, error);
            }

            host.SetActive(true);
            var deadline = Time.realtimeSinceStartup + 40f;
            var made = new List<float>();
            var known = new HashSet<ulong>();
            var frames = 0;
            var mostInAFrame = 0;
            while (known.Count < arriving && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
                var newInFrame = 0;
                foreach (var view in presentation.Views.OrderBy(view => view.transform.position.magnitude))
                {
                    if (known.Add(view.TargetEntityId))
                    {
                        made.Add(view.transform.position.magnitude);
                        newInFrame++;
                    }
                }

                frames += newInFrame > 0 ? 1 : 0;
                mostInAFrame = Mathf.Max(mostInAFrame, newInFrame);
            }

            Assert.That(known.Count, Is.EqualTo(arriving));
            Assert.That(frames, Is.GreaterThan(1), "every view was made in one frame");
            Assert.That(mostInAFrame, Is.EqualTo(1), "a budget of nothing makes one view a frame");
            Assert.That(made, Is.Ordered, "the views were not made nearest first");
            Assert.That(Enumerable.Range(1, arriving).Any(id => presentation.IsWaiting((ulong)id)), Is.False);
        }

        // A consumer that asks for an actor whose view waits is told it has none yet, and finds it
        // once it is made.
        [UnityTest]
        [Timeout(30000)]
        public IEnumerator AnActorWhoseViewWaitsHasNoneYet()
        {
            var presentation = Host(new PresentationBudget(0.0, null), out var actors);
            for (var entityId = 1; entityId <= 3; entityId++)
            {
                Assert.That(actors.State.TryApplySpawn(Spawn((ulong)entityId, Guid.NewGuid(), entityId * 10f), out var error), Is.True, error);
            }

            host.SetActive(true);
            var deadline = Time.realtimeSinceStartup + 20f;
            while (presentation.Views.Count == 0 && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            Assert.That(presentation.Views.Count, Is.EqualTo(1));
            Assert.That(presentation.TryGetView(1, out _), Is.True, "the nearest comes first");
            const ulong farthest = 3;
            Assert.That(presentation.IsWaiting(farthest), Is.True);
            Assert.That(presentation.TryGetView(farthest, out var waitingView), Is.False);
            Assert.That(waitingView, Is.Null);
            Assert.That(presentation.Views.Any(view => view.TargetEntityId == farthest), Is.False);
            var locator = new CombatantViewLocator(null, null, presentation);
            Assert.That(locator.TryGetView(farthest, out _), Is.False);
            Assert.That(locator.TryGetMob(farthest, out _), Is.False);
            Assert.That(locator.IsMob(farthest), Is.False);
            Assert.That(locator.WorldActors.Any(view => view.TargetEntityId == farthest), Is.False);

            while (!presentation.TryGetView(farthest, out _) && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            Assert.That(presentation.TryGetView(farthest, out var madeView), Is.True);
            Assert.That(madeView.TargetEntityId, Is.EqualTo(farthest));
            Assert.That(presentation.IsWaiting(farthest), Is.False);
        }

        private WorldActorPresentationController Host(PresentationBudget budget, out WorldActorClientController actors)
        {
            host = new GameObject("WorldActorPresentationTest");
            host.SetActive(false);
            actors = host.AddComponent<WorldActorClientController>();
            var presentation = host.AddComponent<WorldActorPresentationController>();
            presentation.Initialize(actors, null, null, null, budget);
            return presentation;
        }

        private static RealtimeWorldActorState State(ulong entityId, long revision, float positionX)
        {
            return new RealtimeWorldActorState(
                entityId,
                revision,
                1,
                true,
                positionX,
                0f,
                -1f,
                90f,
                RealtimeWorldActorActivityTier.EventDriven);
        }

        private static RealtimeWorldActorSpawn Spawn(ulong entityId, Guid runtimeActorId)
        {
            return Spawn(entityId, runtimeActorId, entityId);
        }

        private static RealtimeWorldActorSpawn Spawn(ulong entityId, Guid runtimeActorId, float positionX)
        {
            return new RealtimeWorldActorSpawn(
                entityId,
                runtimeActorId,
                "npc.presentation_test",
                "local.presentation_test_" + entityId,
                RealtimeWorldActorKind.Npc,
                "city",
                RealtimeWorldActorDisposition.Friendly,
                0,
                RealtimeWorldActorAggression.None,
                Archetype,
                1,
                1,
                true,
                positionX,
                0f,
                -1f,
                270f,
                RealtimeWorldActorActivityTier.EventDriven,
                0,
                false,
                0f,
                1f,
                0f,
                0.8f,
                2f,
                0.8f,
                new RealtimeWorldActorMenuOption[0]);
        }
    }
}
