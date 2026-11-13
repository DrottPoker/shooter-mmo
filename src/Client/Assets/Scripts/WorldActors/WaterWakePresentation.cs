using System.Collections.Generic;
using Mmorpg.Client.Gameplay;
using Mmorpg.Client.Networking;
using Mmorpg.Client.Worlds;
using Mmorpg.GameSimulation;
using UnityEngine;

namespace Mmorpg.Client.WorldActors
{
    // Draws the wakes that WaterWakeTracker decides for the local player, the other players,
    // and the Mobs: rings that lie flat on the water and grow, and droplets of spray that fall
    // back under gravity. On a sea that moves in waves, the wakes start on the waves, and every
    // ring rides them up and down as it grows. One particle system draws all rings and another
    // all spray, so a crowd in the water costs two draws. Presentation only.
    [DisallowMultipleComponent]
    public sealed class WaterWakePresentation : MonoBehaviour
    {
        private const int MaximumRings = 384;
        private const int MaximumSpray = 768;
        private const float RingLifetime = 1.4f;

        // Wakes lie this far above the surface, so the water never covers them.
        private const float SurfaceOffset = 0.02f;

        private readonly WaterWakeTracker tracker = new WaterWakeTracker();
        private readonly Dictionary<uint, RingOnWaves> ringsOnWaves = new Dictionary<uint, RingOnWaves>();
        private readonly List<uint> finishedRings = new List<uint>();
        private readonly ParticleSystem.Particle[] ringParticles = new ParticleSystem.Particle[MaximumRings];
        private IRealtimeMovementChannel realtimeMovement;
        private WorldSceneContext sceneContext;
        private WorldActorPresentationController actorPresentation;
        private WaterSurfaces waterSurfaces;
        private ParticleSystem rings;
        private ParticleSystem spray;
        private uint nextRingSeed;

        // Called by its owner before the component is activated. Without both materials the
        // component shows nothing; without the scene's water surfaces the wakes stay on the
        // still water.
        public void Initialize(
            IRealtimeMovementChannel movement,
            WorldSceneContext worldSceneContext,
            WorldActorPresentationController worldActorPresentation,
            WaterSurfaces sceneWaterSurfaces,
            Material ringMaterial,
            Material sprayMaterial)
        {
            realtimeMovement = movement;
            sceneContext = worldSceneContext;
            actorPresentation = worldActorPresentation;
            waterSurfaces = sceneWaterSurfaces;
            if (ringMaterial != null && sprayMaterial != null)
            {
                rings = CreateRings(transform, ringMaterial);
                spray = CreateSpray(transform, sprayMaterial);
            }
        }

        // The bodies that stood in water in the last frame.
        public int WadingCount
        {
            get { return tracker.WadingCount; }
        }

        // The rings that ride the waves of a sea.
        public int RingsOnWavesCount
        {
            get { return ringsOnWaves.Count; }
        }

        private void LateUpdate()
        {
            var session = realtimeMovement != null ? realtimeMovement.MovementSession : null;
            if (rings == null || session == null || Time.deltaTime <= 0f)
            {
                return;
            }

            var clock = WaterShaderGlobals.Clock;
            tracker.BeginFrame();
            if (sceneContext != null)
            {
                if (sceneContext.LocalPlayer != null)
                {
                    Visit(session.CollisionWorld, sceneContext.LocalPlayer.transform, clock);
                }

                foreach (var player in sceneContext.RemotePlayers)
                {
                    Visit(session.CollisionWorld, player.transform, clock);
                }
            }

            if (actorPresentation != null)
            {
                foreach (var view in actorPresentation.Views)
                {
                    if (view is MobView mob && mob.IsTargetActive)
                    {
                        Visit(session.CollisionWorld, mob.transform, clock);
                    }
                }
            }

            tracker.EndFrame();
            RideWaves(clock);
        }

        private void Visit(ICollisionWorld collisionWorld, Transform body, float clock)
        {
            if (tracker.Visit(collisionWorld, body, body.position, Time.deltaTime, out var wake))
            {
                Leave(wake, clock);
            }
        }

        // Puts the wake of one body on the water as it is drawn at the clock.
        internal void Leave(in WaterWake wake, float clock)
        {
            var still = wake.Surface;
            var point = default(WaterSurfacePoint);
            var onWaves = waterSurfaces != null && waterSurfaces.TryLocate(wake.BodyId, still.x, still.z, out point);
            var surface = new Vector3(
                still.x,
                still.y + (onWaves ? waterSurfaces.Lift(point, clock) : 0f) + SurfaceOffset,
                still.z);
            if (wake.RingSize > 0f)
            {
                var seed = ++nextRingSeed;
                rings.Emit(
                    new ParticleSystem.EmitParams
                    {
                        position = surface,
                        velocity = Vector3.zero,
                        startSize = wake.RingSize,
                        startLifetime = RingLifetime,
                        randomSeed = seed,
                        applyShapeToPosition = false
                    },
                    1);
                if (onWaves)
                {
                    ringsOnWaves[seed] = new RingOnWaves(still, point, Time.timeAsDouble + RingLifetime);
                }
            }

            for (var droplet = 0; droplet < wake.Spray; droplet++)
            {
                EmitSpray(surface, wake.Heading, wake.Speed);
            }
        }

        // Moves every ring on a sea to the height the waves lift the surface to under it.
        internal void RideWaves(float clock)
        {
            if (ringsOnWaves.Count == 0)
            {
                return;
            }

            var count = rings.GetParticles(ringParticles);
            for (var index = 0; index < count; index++)
            {
                if (ringsOnWaves.TryGetValue(ringParticles[index].randomSeed, out var ring))
                {
                    ringParticles[index].position = new Vector3(
                        ring.Still.x,
                        ring.Still.y + waterSurfaces.Lift(ring.Point, clock) + SurfaceOffset,
                        ring.Still.z);
                }
            }

            rings.SetParticles(ringParticles, count);
            finishedRings.Clear();
            var now = Time.timeAsDouble;
            foreach (var pair in ringsOnWaves)
            {
                if (pair.Value.EndsAt <= now)
                {
                    finishedRings.Add(pair.Key);
                }
            }

            foreach (var seed in finishedRings)
            {
                ringsOnWaves.Remove(seed);
            }
        }

        // A droplet thrown up and forward around the legs.
        private void EmitSpray(Vector3 surface, Vector3 heading, float speed)
        {
            var around = Random.insideUnitCircle * 0.35f;
            spray.Emit(
                new ParticleSystem.EmitParams
                {
                    position = surface + new Vector3(around.x, 0f, around.y) + (heading * 0.2f),
                    velocity = (heading * (speed * Random.Range(0.25f, 0.5f)))
                        + new Vector3(around.x * 2f, Random.Range(1.4f, 2.6f), around.y * 2f),
                    startSize = Random.Range(0.07f, 0.16f),
                    startLifetime = Random.Range(0.45f, 0.7f),
                    applyShapeToPosition = false
                },
                1);
        }

        // Rings lie flat on the water, grow, and fade.
        private static ParticleSystem CreateRings(Transform parent, Material material)
        {
            var system = CreateSystem(parent, "WaterRings", material, MaximumRings, ParticleSystemRenderMode.HorizontalBillboard);
            var size = system.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, 0.35f, 1f, 1.6f));
            var color = system.colorOverLifetime;
            color.enabled = true;
            color.color = Fade(0.55f);
            return system;
        }

        // Spray falls back onto the water under gravity and fades as it lands.
        private static ParticleSystem CreateSpray(Transform parent, Material material)
        {
            var system = CreateSystem(parent, "WaterSpray", material, MaximumSpray, ParticleSystemRenderMode.Billboard);
            var main = system.main;
            main.gravityModifier = 1f;
            var color = system.colorOverLifetime;
            color.enabled = true;
            color.color = Fade(0.9f);
            return system;
        }

        private static ParticleSystem CreateSystem(
            Transform parent,
            string name,
            Material material,
            int maximumParticles,
            ParticleSystemRenderMode renderMode)
        {
            var host = new GameObject(name);
            host.transform.SetParent(parent, false);
            var system = host.AddComponent<ParticleSystem>();
            system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = system.main;
            main.playOnAwake = false;
            main.loop = true;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = maximumParticles;
            main.startColor = Color.white;

            // A new system moves its particles at 5 m/s; rings stay where they formed, and spray
            // gets its own velocity when it is emitted.
            main.startSpeed = 0f;
            var emission = system.emission;
            emission.enabled = false;
            var shape = system.shape;
            shape.enabled = false;
            var renderer = host.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = renderMode;
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            system.Play();
            return system;
        }

        private static ParticleSystem.MinMaxGradient Fade(float alpha)
        {
            var gradient = new Gradient();
            gradient.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(alpha, 0.12f), new GradientAlphaKey(0f, 1f) });
            return new ParticleSystem.MinMaxGradient(gradient);
        }

        // A ring on a sea: where it formed on the still surface, the triangle of the drawn
        // surface below it, and when it fades out.
        private readonly struct RingOnWaves
        {
            public RingOnWaves(Vector3 still, WaterSurfacePoint point, double endsAt)
            {
                Still = still;
                Point = point;
                EndsAt = endsAt;
            }

            public Vector3 Still { get; }

            public WaterSurfacePoint Point { get; }

            public double EndsAt { get; }
        }
    }
}
