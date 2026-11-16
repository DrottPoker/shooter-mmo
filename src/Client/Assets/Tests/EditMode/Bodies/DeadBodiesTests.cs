using System.Collections.Generic;
using Mmorpg.Client.Gameplay;
using NUnit.Framework;
using UnityEngine;

namespace Mmorpg.Client.Tests.EditMode
{
    // A player corpse stays hidden while a body that shows its character dead lies within
    // PlayerCorpseBodies.OwnBodyRadius over the ground. The corpse presentation collects the dead
    // bodies once a frame and checks each corpse against them, instead of walking every player
    // for every corpse. See docs/UNITY_CLIENT_ARCHITECTURE.md, Corpse Client State And
    // Presentation.
    public sealed class DeadBodiesTests
    {
        [Test]
        public void ACorpseIsNearADeadBodyWithinTheRadiusOverTheGround()
        {
            var radius = PlayerCorpseBodies.OwnBodyRadius;
            var corpse = new Vector3(10f, 2f, -4f);
            var bodies = new List<Vector3>();
            Assert.That(WorldSceneContext.AnyNear(bodies, corpse, radius), Is.False);

            bodies.Add(corpse + new Vector3(radius + 0.01f, 0f, 0f));
            bodies.Add(corpse + new Vector3(0f, 0f, -(radius + 0.5f)));
            Assert.That(WorldSceneContext.AnyNear(bodies, corpse, radius), Is.False);

            // The height between them does not count, as on a slope or a step.
            bodies.Add(corpse + new Vector3(0f, 3f, radius - 0.01f));
            Assert.That(WorldSceneContext.AnyNear(bodies, corpse, radius), Is.True);
        }

        [Test]
        public void AWorldWithoutPlayersHasNoDeadBodies()
        {
            var context = new GameObject("WorldSceneContext").AddComponent<WorldSceneContext>();
            try
            {
                var bodies = new List<Vector3> { Vector3.one };
                context.CollectDeadBodies(bodies);
                Assert.That(bodies, Is.EqualTo(new[] { Vector3.one }), "it only adds");
            }
            finally
            {
                Object.DestroyImmediate(context.gameObject);
            }
        }
    }
}
