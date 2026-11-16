using System;
using System.Collections;
using System.Linq;
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
    // stays where it was put. See docs/UNITY_CLIENT_ARCHITECTURE.md, World Actor And Interaction
    // Client.
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
                entityId,
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
