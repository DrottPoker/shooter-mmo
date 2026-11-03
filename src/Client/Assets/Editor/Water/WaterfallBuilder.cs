using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Mmorpg.Client.Editor
{
    // A waterfall's falling water in the space of its own object, and where it lands, the white
    // water that boils up along it, the mist that rises and drifts, and the droplets that fly.
    internal static class WaterfallBuilder
    {
        public const string BoilName = "WaterfallBoil";
        public const string MistName = "WaterfallMist";
        public const string SplashName = "WaterfallSplash";

        public static void BuildColumn(WaterfallModel waterfall, Mesh mesh)
        {
            var shape = waterfall.Shape;
            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var tangents = new List<Vector4>();
            var column = new List<Vector4>();
            var triangles = new List<int>();
            WaterfallColumn.Build(
                waterfall.Fall,
                shape.Approach,
                shape.PastLip,
                shape.SpreadWidth,
                shape.FootHalfThickness,
                vertices,
                normals,
                tangents,
                column,
                triangles);

            var transform = shape.transform;
            for (var index = 0; index < vertices.Count; index++)
            {
                vertices[index] = transform.InverseTransformPoint(vertices[index]);
                normals[index] = transform.InverseTransformDirection(normals[index]);
                var around = transform.InverseTransformDirection(tangents[index]);
                tangents[index] = new Vector4(around.x, around.y, around.z, tangents[index].w);
            }

            mesh.Clear(false);
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetTangents(tangents);
            mesh.SetUVs(0, column);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
        }

        // The spray emitters below the waterfall's object, kept where they are and set up anew
        // from its fall.
        public static void BuildSpray(WaterfallModel waterfall, Material material)
        {
            var parent = waterfall.Shape.transform;
            var fall = waterfall.Fall;
            var foot = new Vector3(fall.Landing.x, fall.LandingLevel, fall.Landing.y);
            var downstream = new Vector3(fall.Downstream.x, 0f, fall.Downstream.y);
            var boil = Particles(parent, BoilName, foot, downstream, material, 90, 30f, 0.75f);
            var boilMain = boil.main;
            boilMain.startLifetime = new ParticleSystem.MinMaxCurve(0.5f, 0.9f);
            boilMain.startSpeed = new ParticleSystem.MinMaxCurve(0.5f, 1.3f);
            boilMain.startSize = new ParticleSystem.MinMaxCurve(0.45f, 1f);
            boilMain.gravityModifier = 0.4f;
            var boilShape = boil.shape;
            boilShape.shapeType = ParticleSystemShapeType.Box;
            boilShape.scale = new Vector3((fall.HalfWidth + 0.3f) * 2f, 0.3f, 0.05f);
            var boilSize = boil.sizeOverLifetime;
            boilSize.enabled = true;
            boilSize.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0.7f, 1f, 1.3f));

            var mist = Particles(parent, MistName, foot, downstream, material, 100, 18f, 0.2f);
            var mistMain = mist.main;
            mistMain.startLifetime = new ParticleSystem.MinMaxCurve(1.4f, 2.2f);
            mistMain.startSpeed = new ParticleSystem.MinMaxCurve(0.3f, 0.8f);
            mistMain.startSize = new ParticleSystem.MinMaxCurve(0.8f, 1.6f);
            mistMain.gravityModifier = -0.01f;
            var mistShape = mist.shape;
            mistShape.shapeType = ParticleSystemShapeType.Hemisphere;
            mistShape.radius = fall.HalfWidth;
            var mistSize = mist.sizeOverLifetime;
            mistSize.enabled = true;
            mistSize.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0.6f, 1f, 1.5f));

            var splash = Particles(parent, SplashName, foot, downstream, material, 200, 60f, 0.85f);
            var splashMain = splash.main;
            splashMain.startLifetime = new ParticleSystem.MinMaxCurve(0.5f, 0.9f);
            splashMain.startSpeed = new ParticleSystem.MinMaxCurve(2f, 3.6f);
            splashMain.startSize = new ParticleSystem.MinMaxCurve(0.06f, 0.16f);
            splashMain.gravityModifier = 1f;
            var splashShape = splash.shape;
            splashShape.shapeType = ParticleSystemShapeType.Cone;
            splashShape.angle = 30f;
            splashShape.radius = fall.HalfWidth;
        }

        // A looping emitter of soft white particles that fade in and out, whose shape points
        // straight up, with its width across the fall.
        private static ParticleSystem Particles(
            Transform parent,
            string name,
            Vector3 position,
            Vector3 downstream,
            Material material,
            int maximumParticles,
            float rate,
            float alpha)
        {
            var existing = parent.Find(name);
            var host = existing != null ? existing.gameObject : new GameObject(name);
            host.transform.SetParent(parent, false);
            host.transform.SetPositionAndRotation(position, Quaternion.LookRotation(Vector3.up, downstream));
            var system = host.TryGetComponent<ParticleSystem>(out var found) ? found : host.AddComponent<ParticleSystem>();
            var main = system.main;
            main.loop = true;
            main.prewarm = true;
            main.playOnAwake = true;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = maximumParticles;
            main.startColor = Color.white;
            var emission = system.emission;
            emission.rateOverTime = rate;
            var gradient = new Gradient();
            gradient.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(alpha, 0.2f), new GradientAlphaKey(0f, 1f) });
            var color = system.colorOverLifetime;
            color.enabled = true;
            color.color = new ParticleSystem.MinMaxGradient(gradient);
            var renderer = host.GetComponent<ParticleSystemRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return system;
        }
    }
}
