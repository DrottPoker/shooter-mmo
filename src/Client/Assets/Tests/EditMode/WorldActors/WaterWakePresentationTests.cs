using Mmorpg.Client.Editor;
using Mmorpg.Client.WorldActors;
using Mmorpg.Client.Worlds;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Mmorpg.Client.Tests.EditMode
{
    // The wakes draw with one particle system for the rings and one for the spray, both in world
    // space and without motion of their own, so a ring grows where it formed. On a sea a ring
    // starts on the waves and rides them up and down.
    public sealed class WaterWakePresentationTests
    {
        private GameObject host;
        private GameObject sea;
        private Mesh seaMesh;

        [TearDown]
        public void DestroyHost()
        {
            foreach (var created in new Object[] { host, sea, seaMesh })
            {
                if (created != null)
                {
                    Object.DestroyImmediate(created);
                }
            }
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
            host.AddComponent<WaterWakePresentation>().Initialize(null, null, null, null, null, null);

            Assert.That(host.GetComponentsInChildren<ParticleSystem>(), Is.Empty);
        }

        [Test]
        public void ARingOnTheSeaRidesTheWavesAsItSpreads()
        {
            var surfaces = new WaterSurfaces(new[] { Sea() });
            host = new GameObject("Wakes");
            var wakes = host.AddComponent<WaterWakePresentation>();
            wakes.Initialize(
                null,
                null,
                null,
                surfaces,
                AssetDatabase.LoadAssetAtPath<Material>(WaterAssets.RingMaterialPath),
                AssetDatabase.LoadAssetAtPath<Material>(WaterAssets.SprayMaterialPath));
            Assert.That(surfaces.TryLocate("test.sea", 5f, 4f, out var point), Is.True);

            wakes.Leave(new WaterWake("test.sea", new Vector3(5f, 0f, 4f), 1.2f, 0, Vector3.zero, 0f), 100f);
            Assert.That(wakes.RingsOnWavesCount, Is.EqualTo(1));
            Assert.That(RingHeight(), Is.EqualTo(surfaces.Lift(point, 100f) + 0.02f).Within(0.0001f));

            wakes.RideWaves(102.5f);
            Assert.That(surfaces.Lift(point, 102.5f), Is.Not.EqualTo(surfaces.Lift(point, 100f)).Within(0.01f));
            Assert.That(RingHeight(), Is.EqualTo(surfaces.Lift(point, 102.5f) + 0.02f).Within(0.0001f));
        }

        private float RingHeight()
        {
            var rings = host.transform.Find("WaterRings").GetComponent<ParticleSystem>();
            var particles = new ParticleSystem.Particle[4];
            Assert.That(rings.GetParticles(particles), Is.EqualTo(1));
            return particles[0].position.y;
        }

        // A 10 by 10 m square of sea from the origin that the waves lift everywhere.
        private WaterBody Sea()
        {
            sea = new GameObject("Sea");
            seaMesh = new Mesh
            {
                vertices = new[]
                {
                    new Vector3(0f, 0f, 0f),
                    new Vector3(10f, 0f, 0f),
                    new Vector3(10f, 0f, 10f),
                    new Vector3(0f, 0f, 10f)
                },
                colors = new[] { Color.green, Color.green, Color.green, Color.green },
                triangles = new[] { 0, 3, 1, 1, 3, 2 }
            };
            sea.AddComponent<MeshFilter>().sharedMesh = seaMesh;
            sea.AddComponent<MeshRenderer>().sharedMaterial = AssetDatabase.LoadAssetAtPath<Material>(WaterAssets.SurfaceMaterialPath);
            var body = sea.AddComponent<WaterBody>();
            var serialized = new SerializedObject(body);
            serialized.FindProperty("bodyId").stringValue = "test.sea";
            serialized.FindProperty("kind").enumValueIndex = (int)WaterBodyKind.Sea;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return body;
        }
    }
}
