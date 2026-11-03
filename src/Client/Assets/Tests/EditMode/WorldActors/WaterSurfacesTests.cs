using Mmorpg.Client.Editor;
using Mmorpg.Client.Worlds;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Mmorpg.Client.Tests.EditMode
{
    // Presentation puts wakes on the water as it is drawn: the waves lift each vertex of a sea by
    // the waves at its position times the green of its color, and the triangles between follow,
    // as the water shader draws them. The water's clock wraps once an hour without a jump.
    public sealed class WaterSurfacesTests
    {
        private const string BodyId = "test.sea";

        private static readonly float[] Moving = { 1f, 0.5f, 0f, 1f };

        private GameObject host;
        private Mesh mesh;
        private Material material;

        [TearDown]
        public void DestroyScene()
        {
            foreach (var created in new Object[] { host, mesh, material })
            {
                if (created != null)
                {
                    Object.DestroyImmediate(created);
                }
            }
        }

        [Test]
        public void TheWavesLiftEachVertexByItsWeightAndTheTrianglesBetweenFollow()
        {
            var surfaces = new WaterSurfaces(new[] { Sea(0.12f, Moving) });
            const float clock = 1234.5f;
            var waves = new WaterWaveSettings(0.12f, 14f, 2f);

            Assert.That(surfaces.TryLocate(BodyId, 0f, 0f, out var corner), Is.True);
            Assert.That(surfaces.Lift(corner, clock), Is.EqualTo(WaterWaves.Height(0f, 0f, clock, waves)).Within(0.0001f));
            Assert.That(surfaces.TryLocate(BodyId, 10f, 0f, out var half), Is.True);
            Assert.That(surfaces.Lift(half, clock), Is.EqualTo(WaterWaves.Height(10f, 0f, clock, waves) * 0.5f).Within(0.0001f));

            // Halfway along the edge between those corners the lift is the mean of theirs.
            Assert.That(surfaces.TryLocate(BodyId, 5f, 0f, out var between), Is.True);
            Assert.That(
                surfaces.Lift(between, clock),
                Is.EqualTo((surfaces.Lift(corner, clock) + surfaces.Lift(half, clock)) * 0.5f).Within(0.0001f));
        }

        [Test]
        public void PointsOffTheSurfaceAndOtherBodiesHaveNothingToFollow()
        {
            var surfaces = new WaterSurfaces(new[] { Sea(0.12f, Moving) });

            Assert.That(surfaces.TryLocate(BodyId, 20f, 5f, out _), Is.False);
            Assert.That(surfaces.TryLocate("test.lake", 1f, 1f, out _), Is.False);
            Assert.That(surfaces.TryLocate(null, 1f, 1f, out _), Is.False);
        }

        [Test]
        public void WaterThatNoWaveLiftsIsNotFollowed()
        {
            Assert.That(new WaterSurfaces(new[] { Sea(0f, Moving) }).TryLocate(BodyId, 1f, 1f, out _), Is.False);
            DestroyScene();
            Assert.That(new WaterSurfaces(new[] { Sea(0.12f, new float[4]) }).TryLocate(BodyId, 1f, 1f, out _), Is.False);
        }

        [Test]
        public void TheWaterClockRunsOnWithoutAJumpWhenItWraps()
        {
            const float rate = 1.89f;
            var period = (float)WaterShaderGlobals.ClockPeriod;
            var before = WaterShaderGlobals.Cycles(rate, period - 0.01f);
            var after = WaterShaderGlobals.Cycles(rate, 0.01f);

            var step = Mathf.Repeat(after - before, 1f);
            Assert.That(step, Is.EqualTo(rate * 0.02f).Within(0.002f));
            Assert.That(WaterShaderGlobals.Wrap((WaterShaderGlobals.ClockPeriod * 30d) + 12.5d), Is.EqualTo(12.5f).Within(0.001f));
        }

        // A 10 by 10 m square of sea in two triangles from the origin, whose corners the waves
        // lift by the given weights.
        private WaterBody Sea(float waveAmplitude, float[] weights)
        {
            host = new GameObject("Sea");
            mesh = new Mesh
            {
                vertices = new[]
                {
                    new Vector3(0f, 0f, 0f),
                    new Vector3(10f, 0f, 0f),
                    new Vector3(10f, 0f, 10f),
                    new Vector3(0f, 0f, 10f)
                },
                colors = new[]
                {
                    new Color(0f, weights[0], 0f, 1f),
                    new Color(0f, weights[1], 0f, 1f),
                    new Color(0f, weights[2], 0f, 1f),
                    new Color(0f, weights[3], 0f, 1f)
                },
                triangles = new[] { 0, 3, 1, 1, 3, 2 }
            };
            host.AddComponent<MeshFilter>().sharedMesh = mesh;
            material = new Material(AssetDatabase.LoadAssetAtPath<Material>(WaterAssets.SurfaceMaterialPath));
            material.SetFloat("_WaveAmplitude", waveAmplitude);
            material.SetFloat("_WaveLength", 14f);
            material.SetFloat("_WaveSpeed", 2f);
            host.AddComponent<MeshRenderer>().sharedMaterial = material;
            var body = host.AddComponent<WaterBody>();
            var serialized = new SerializedObject(body);
            serialized.FindProperty("bodyId").stringValue = BodyId;
            serialized.FindProperty("kind").enumValueIndex = (int)WaterBodyKind.Sea;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return body;
        }
    }
}
