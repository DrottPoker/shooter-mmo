using System.Collections.Generic;
using Mmorpg.Client.Combat;
using Mmorpg.Client.Networking;
using Mmorpg.GameProtocol;
using UnityEngine;

namespace Mmorpg.Client.Ui
{
    // The world effect of a level-up: bright golden motes burst from a ring around a character's
    // feet, rise to about 2.4 m, and fade. It plays on the local character when its progression
    // says it gained a level, and on another character the client shows when the worker says it
    // reached a higher level; an entity the client does not show is ignored. Each character that
    // plays one gets a pooled particle system that follows it until the motes are gone, drawn
    // with the particle material the water spray uses. Presentation only.
    [DisallowMultipleComponent]
    public sealed class LevelUpEffects : MonoBehaviour
    {
        public const int Motes = 90;

        // The motes leave the ring over the burst, and each rises for the rise.
        public const float BurstSeconds = 0.5f;
        public const float RiseSeconds = 1.8f;
        public const float EffectSeconds = BurstSeconds + RiseSeconds;
        public const float RiseHeight = 2.4f;

        private const float RingRadius = 0.55f;
        private const float MinimumSize = 0.09f;
        private const float MaximumSize = 0.18f;
        private static readonly Color MoteColor = new Color(1f, 0.86f, 0.35f, 1f);

        private readonly Dictionary<ulong, Effect> playing = new Dictionary<ulong, Effect>();
        private readonly Stack<ParticleSystem> idle = new Stack<ParticleSystem>();
        private readonly List<ulong> finished = new List<ulong>();
        private ProgressionClientState progression;
        private IRealtimeProgressionChannel progressionChannel;
        private CombatantViewLocator views;
        private Material material;

        // How many characters play the effect now.
        public int PlayingCount
        {
            get { return playing.Count; }
        }

        public bool IsPlaying(ulong entityId)
        {
            return playing.ContainsKey(entityId);
        }

        // How many motes of the burst have left the ring by an age: all of them by its end.
        public static int MotesEmittedBy(float age)
        {
            if (age <= 0f)
            {
                return 0;
            }

            return age >= BurstSeconds ? Motes : Mathf.CeilToInt(Motes * (age / BurstSeconds));
        }

        // Called by the composition root before the component is activated. Without a material
        // the component shows nothing.
        public void Initialize(
            ProgressionClientState progressionState,
            IRealtimeProgressionChannel channel,
            CombatantViewLocator combatantViews,
            Material moteMaterial)
        {
            progression = progressionState;
            progressionChannel = channel;
            views = combatantViews;
            material = moteMaterial;
        }

        // Plays the effect on a character the client shows; false for one it does not. A
        // character that already plays one starts it over.
        public bool Play(ulong entityId)
        {
            if (material == null || views == null || !views.TryGetView(entityId, out var view))
            {
                return false;
            }

            if (!playing.TryGetValue(entityId, out var effect))
            {
                effect = new Effect(idle.Count > 0 ? idle.Pop() : CreateSystem());
            }

            effect.StartedAt = Time.time;
            effect.Emitted = 0;
            effect.System.gameObject.SetActive(true);
            effect.System.transform.position = view.Position;
            effect.System.Clear();
            // A system that its pool deactivated plays again.
            effect.System.Play();
            playing[entityId] = effect;
            return true;
        }

        private void OnEnable()
        {
            if (progression != null)
            {
                progression.Changed += OnProgressionChanged;
            }

            if (progressionChannel != null)
            {
                progressionChannel.EntityLeveledUp += OnEntityLeveledUp;
            }
        }

        private void OnDisable()
        {
            if (progression != null)
            {
                progression.Changed -= OnProgressionChanged;
            }

            if (progressionChannel != null)
            {
                progressionChannel.EntityLeveledUp -= OnEntityLeveledUp;
            }
        }

        private void OnProgressionChanged(RealtimeOwnerProgressionChanged change)
        {
            if (ExperienceBarPresentation.ShowsLevelUp(change) && views != null)
            {
                Play(views.ControlledEntityId);
            }
        }

        private void OnEntityLeveledUp(RealtimeEntityLeveledUp levelUp)
        {
            if (levelUp != null)
            {
                Play(levelUp.EntityId);
            }
        }

        // The motes follow their character and leave the ring through the burst; a character
        // that is gone ends its effect, and a finished effect goes back to the pool.
        private void LateUpdate()
        {
            if (playing.Count == 0)
            {
                return;
            }

            finished.Clear();
            var now = Time.time;
            foreach (var pair in playing)
            {
                var effect = pair.Value;
                var age = now - effect.StartedAt;
                if (age >= EffectSeconds || !views.TryGetView(pair.Key, out var view))
                {
                    finished.Add(pair.Key);
                    continue;
                }

                effect.System.transform.position = view.Position;
                var due = MotesEmittedBy(age);
                if (due > effect.Emitted)
                {
                    Emit(effect.System, due - effect.Emitted);
                    effect.Emitted = due;
                }
            }

            foreach (var entityId in finished)
            {
                var system = playing[entityId].System;
                playing.Remove(entityId);
                system.Clear();
                system.gameObject.SetActive(false);
                idle.Push(system);
            }
        }

        // Motes start on the ring around the feet and rise to the rise height within the rise.
        private static void Emit(ParticleSystem system, int count)
        {
            for (var index = 0; index < count; index++)
            {
                var angle = Random.value * Mathf.PI * 2f;
                var radius = RingRadius * Random.Range(0.85f, 1.1f);
                var lifetime = RiseSeconds * Random.Range(0.9f, 1f);
                var start = new Vector3(Mathf.Cos(angle) * radius, Random.Range(0f, 0.15f), Mathf.Sin(angle) * radius);
                system.Emit(
                    new ParticleSystem.EmitParams
                    {
                        position = start,
                        velocity = new Vector3(-start.x * 0.12f, (RiseHeight - start.y) / lifetime, -start.z * 0.12f),
                        startSize = Random.Range(MinimumSize, MaximumSize),
                        startLifetime = lifetime,
                        startColor = MoteColor,
                        applyShapeToPosition = false
                    },
                    1);
            }
        }

        private ParticleSystem CreateSystem()
        {
            var host = new GameObject("LevelUpMotes");
            host.transform.SetParent(transform, false);
            var system = host.AddComponent<ParticleSystem>();
            system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = system.main;
            main.playOnAwake = false;
            main.loop = true;
            // The motes move with their character.
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
            main.maxParticles = Motes;
            main.startSpeed = 0f;
            var emission = system.emission;
            emission.enabled = false;
            var shape = system.shape;
            shape.enabled = false;
            // Full from a quick fade-in until the last third of the rise, then out.
            var color = system.colorOverLifetime;
            color.enabled = true;
            var gradient = new Gradient();
            gradient.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.08f), new GradientAlphaKey(1f, 2f / 3f), new GradientAlphaKey(0f, 1f) });
            color.color = new ParticleSystem.MinMaxGradient(gradient);
            var renderer = host.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            system.Play();
            return system;
        }

        private sealed class Effect
        {
            public Effect(ParticleSystem system)
            {
                System = system;
            }

            public ParticleSystem System { get; }

            public float StartedAt { get; set; }

            // The motes of the burst that left the ring so far.
            public int Emitted { get; set; }
        }
    }
}
