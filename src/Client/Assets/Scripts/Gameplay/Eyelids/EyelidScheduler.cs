using System;
using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;

namespace Mmorpg.Client.Gameplay
{
    // Blinks the eyes of every body in play and closes those of the dead, from one place: one
    // EyelidSchedule of the lids of every enabled body, stepped once a frame after the Animator posed
    // the bodies and the camera moved. A frame touches only the bodies whose blink is due or under
    // way. A body blinks when its time comes only while it lives, its combined renderer was drawn by
    // some camera, and a camera the cloth is laid for, the main view or an added view such as the
    // character preview's, is within MaxDistance of its eyes; otherwise it only gets its next time.
    // The random numbers are the scheduler's own and of the presentation only. The scheduler puts
    // itself in play with the first body; out of play the lids stay open. See
    // docs/UNITY_CLIENT_ARCHITECTURE.md, Eyelids.
    [DefaultExecutionOrder(PresentationExecutionOrder.Eyelids)]
    public sealed class EyelidScheduler : MonoBehaviour, EyelidSchedule.IViews
    {
        // Beyond this distance from every view a body does not blink, in meters.
        public const float MaxDistance = 25f;

        private static readonly ProfilerMarker StepMarker = new ProfilerMarker("EyelidScheduler.Step");
        private static EyelidScheduler runner;

        private readonly EyelidSchedule schedule = new EyelidSchedule();
        private readonly Dictionary<BodyEyelids, EyelidSchedule.Body> bodies = new Dictionary<BodyEyelids, EyelidSchedule.Body>();
        // The cameras the cloth is laid for, collected once in a step in which a body's time came.
        private readonly List<Camera> views = new List<Camera>();
        private readonly System.Random random = new System.Random();
        private bool viewsCollected;

        // How many bodies' lids are in play.
        public static int Count
        {
            get { return runner != null ? runner.schedule.Count : 0; }
        }

        // The scheduler in play, or null before the first body.
        internal static EyelidScheduler Running
        {
            get { return runner; }
        }

        // Whether a camera drew the renderer in the last frame. Renderer.isVisible is true when any
        // camera draws it, a shadow pass and the Scene view included; the tests give their own.
        internal Func<Renderer, bool> IsDrawn { get; set; } = drawn => drawn.isVisible;

        // Whether the lids are in play.
        public static bool Holds(BodyEyelids lids)
        {
            return lids != null && runner != null && runner.bodies.ContainsKey(lids);
        }

        // The lids of a body that is enabled in play: closed at once when it lies dead, and otherwise
        // open, blinking first within EyelidSchedule.FirstSpreadSeconds.
        internal static void Register(BodyEyelids lids, bool dead)
        {
            if (lids == null || !Application.isPlaying)
            {
                return;
            }

            if (runner == null)
            {
                var host = new GameObject(nameof(EyelidScheduler));
                DontDestroyOnLoad(host);
                runner = host.AddComponent<EyelidScheduler>();
            }

            runner.Add(lids, dead, Time.timeAsDouble);
        }

        // The body is disabled or destroyed.
        internal static void Unregister(BodyEyelids lids)
        {
            if (lids != null && runner != null && runner.bodies.TryGetValue(lids, out var body))
            {
                runner.schedule.Remove(body);
                runner.bodies.Remove(lids);
            }
        }

        // The body shows its character's life change: it falls and closes its eyes at the blink's
        // closing speed, lies dead with them closed at once, or stands up with them open.
        internal static void ShowLifeChange(BodyEyelids lids, HumanLocomotion.LifeChange change)
        {
            if (lids == null || runner == null || !runner.bodies.TryGetValue(lids, out var body))
            {
                return;
            }

            var now = Time.timeAsDouble;
            switch (change)
            {
                case HumanLocomotion.LifeChange.Fall:
                    runner.schedule.Die(body, now, true);
                    break;
                case HumanLocomotion.LifeChange.LieDead:
                    runner.schedule.Die(body, now, false);
                    break;
                case HumanLocomotion.LifeChange.StandUp:
                    runner.schedule.Live(body, now, runner);
                    break;
            }
        }

        // The lids come into this scheduler's schedule at a time, once.
        internal void Add(BodyEyelids lids, bool dead, double now)
        {
            if (lids != null && !bodies.ContainsKey(lids))
            {
                var body = new EyelidSchedule.Body(lids);
                bodies.Add(lids, body);
                schedule.Add(body, now, dead, this);
            }
        }

        // Where the lids are in the schedule, or null when they are not in it.
        internal EyelidSchedule.Body BodyOf(BodyEyelids lids)
        {
            return lids != null && bodies.TryGetValue(lids, out var body) ? body : null;
        }

        // One step of the schedule at a time, which collects the views again for its bodies that
        // come due.
        internal void Step(double now)
        {
            viewsCollected = false;
            schedule.Step(now, this);
        }

        // Play without a domain reload keeps statics; the runner of the last play is gone.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            runner = null;
        }

        private void LateUpdate()
        {
            if (schedule.Count == 0)
            {
                return;
            }

            using (StepMarker.Auto())
            {
                Step(Time.timeAsDouble);
            }
        }

        bool EyelidSchedule.IViews.MayBlink(EyelidSchedule.Body body)
        {
            var lids = body.Lids;
            var drawn = lids != null ? lids.Renderer : null;
            if (drawn == null || !IsDrawn(drawn))
            {
                return false;
            }

            if (!viewsCollected)
            {
                ClothChainScheduler.CollectViews(views);
                viewsCollected = true;
            }

            return IsNear(lids.Position, views);
        }

        // Whether one of the cameras is within MaxDistance of the eyes.
        public static bool IsNear(Vector3 eyes, IReadOnlyList<Camera> cameras)
        {
            for (var index = 0; index < cameras.Count; index++)
            {
                if ((cameras[index].transform.position - eyes).sqrMagnitude <= MaxDistance * MaxDistance)
                {
                    return true;
                }
            }

            return false;
        }

        float EyelidSchedule.IViews.NextRandom()
        {
            return (float)random.NextDouble();
        }

        private void OnDestroy()
        {
            if (runner == this)
            {
                runner = null;
            }
        }
    }
}
