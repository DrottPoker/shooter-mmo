using System.Collections.Generic;
using System.Linq;
using Mmorpg.Client.Gameplay;
using NUnit.Framework;
using UnityEngine;

namespace Mmorpg.Client.Tests.EditMode
{
    // The players that came into view get their bodies the nearest to the camera first, as
    // WorldSceneContext puts them on a few a frame. A frame measures each waiting player once and
    // takes them nearest first from a heap, without allocating, instead of scanning every waiting
    // player again for each body. See docs/UNITY_CLIENT_ARCHITECTURE.md, Crowd Performance.
    public sealed class WaitingBodiesTests
    {
        private const int PlayerCount = 200;

        private readonly List<GameObject> players = new List<GameObject>();

        [TearDown]
        public void DestroyPlayers()
        {
            foreach (var player in players)
            {
                Object.DestroyImmediate(player);
            }

            players.Clear();
        }

        [Test]
        public void TheNearestWaitingPlayersGetTheirBodiesFirstWithoutAllocating()
        {
            var eye = new Vector3(3f, 2f, -4f);
            var views = new Dictionary<ulong, RemotePlayerView>();
            for (var index = 0; index < PlayerCount; index++)
            {
                // Spread over a few rings around the eye, in an order unlike their distance, with
                // ties within each ring.
                var entityId = (ulong)(index + 1);
                var angle = index * 2.4f;
                var radius = 5f + ((index * 37) % 9);
                views[entityId] = Player(entityId, eye + new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius));
            }

            var waiting = new WaitingBodies();
            foreach (var entityId in views.Keys)
            {
                waiting.Set(entityId, null);
            }

            // A player that left without its body going on is forgotten.
            waiting.Set(PlayerCount + 1, null);

            waiting.OrderFrom(views, eye);
            Assert.That(waiting.Count, Is.EqualTo(PlayerCount));
            var taken = new List<RemotePlayerView>();
            Assert.That(waiting.TryTakeNext(out var first, out _), Is.True);
            taken.Add(first);

            // A body that went on some other way is not taken again.
            var other = views[views.Keys.First(entityId => entityId != first.NetworkEntityId)];
            waiting.Remove(other.NetworkEntityId);
            while (waiting.TryTakeNext(out var view, out _))
            {
                taken.Add(view);
            }

            Assert.That(waiting.Count, Is.Zero);
            Assert.That(taken.Count, Is.EqualTo(PlayerCount - 1));
            Assert.That(taken, Has.No.Member(other));
            for (var index = 1; index < taken.Count; index++)
            {
                var previous = (taken[index - 1].transform.position - eye).sqrMagnitude;
                var current = (taken[index].transform.position - eye).sqrMagnitude;
                Assert.That(current, Is.GreaterThanOrEqualTo(previous), "nearest first");
                if (current == previous)
                {
                    Assert.That(taken[index].NetworkEntityId, Is.GreaterThan(taken[index - 1].NetworkEntityId), "of equals the first found");
                }
            }

            Assert.That(
                ManagedAllocations.Of(() =>
                {
                    foreach (var entityId in views.Keys)
                    {
                        waiting.Set(entityId, null);
                    }

                    waiting.OrderFrom(views, eye);
                    while (waiting.TryTakeNext(out _, out _))
                    {
                    }
                }),
                Is.Zero);
        }

        private RemotePlayerView Player(ulong entityId, Vector3 position)
        {
            var player = new GameObject("RemotePlayer_" + entityId);
            players.Add(player);
            player.transform.position = position;
            var view = player.AddComponent<RemotePlayerView>();
            view.Initialize(entityId, "character-" + entityId, "Player " + entityId, "player.default", 30, 4, null, null);
            return view;
        }
    }
}
