using Mmorpg.Client.Editor;
using Mmorpg.Client.WorldActors;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Mmorpg.Client.Tests.EditMode
{
    // The wakes draw with one particle system for the rings and one for the spray, both in world
    // space and without motion of their own, so a ring grows where it formed.
    public sealed class WaterWakePresentationTests
    {
        private GameObject host;

        [TearDown]
        public void DestroyHost()
        {
            Object.DestroyImmediate(host);
        }

        [Test]
        public void TheWakeParticlesStayWhereTheyFormUnlessGivenAVelocity()
        {
            host = new GameObject("Wakes");
            var wakes = host.AddComponent<WaterWakePresentation>();
            wakes.Initialize(
                null,
                null,
                null,
                AssetDatabase.LoadAssetAtPath<Material>(WaterAssets.RingMaterialPath),
                AssetDatabase.LoadAssetAtPath<Material>(WaterAssets.SprayMaterialPath));

            var systems = host.GetComponentsInChildren<ParticleSystem>();
            Assert.That(systems, Has.Length.EqualTo(2));
            foreach (var system in systems)
            {
                var main = system.main;
                Assert.That(main.startSpeed.mode, Is.EqualTo(ParticleSystemCurveMode.Constant), system.name);
                Assert.That(main.startSpeed.constant, Is.Zero, system.name);
                Assert.That(main.simulationSpace, Is.EqualTo(ParticleSystemSimulationSpace.World), system.name);
                Assert.That(system.emission.enabled, Is.False, system.name);
            }
        }

        [Test]
        public void WithoutMaterialsTheWakesCreateNothing()
        {
            host = new GameObject("Wakes");
            host.AddComponent<WaterWakePresentation>().Initialize(null, null, null, null, null);

            Assert.That(host.GetComponentsInChildren<ParticleSystem>(), Is.Empty);
        }
    }
}
