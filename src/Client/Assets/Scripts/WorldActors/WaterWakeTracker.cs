using System.Collections.Generic;
using Mmorpg.GameSimulation;
using UnityEngine;

namespace Mmorpg.Client.WorldActors
{
    // Decides the wakes that players and Mobs leave in water, frame by frame: a ring that
    // spreads from a body now and then, often while it moves, and spray in proportion to the
    // distance it wades. The depth comes from the same baked water as the movement rules, so a
    // body leaves wakes only where it really stands in water. Presentation only.
    public sealed class WaterWakeTracker
    {
        // Water shallower than this leaves no wake.
        public const float MinimumDepth = 0.05f;

        // A body slower than this, in meters per second, stands still.
        public const float MovingSpeed = 0.4f;

        public const float MovingRingSeconds = 0.22f;
        public const float StillRingSeconds = 1.6f;
        public const float StillRingSize = 1.2f;

        // Droplets per meter waded in water at least SprayDepth deep, fewer in shallower water.
        public const float SprayPerMeter = 9f;
        public const float SprayDepth = 0.4f;

        // A body that moves this far in one frame was placed anew and is not wading.
        public const float TeleportDistance = 2f;

        private readonly Dictionary<object, Wader> waders = new Dictionary<object, Wader>();
        private readonly List<object> departed = new List<object>();
        private readonly CollisionQueryBuffer queryBuffer = new CollisionQueryBuffer();
        private int frame;

        // The bodies that stood in water in the current frame.
        public int WadingCount { get; private set; }

        public void BeginFrame()
        {
            frame++;
            WadingCount = 0;
        }

        // The wake of one body in the current frame. Returns false when the body stands in no
        // water; a ring size of zero means no ring this frame.
        public bool Visit(
            ICollisionWorld collisionWorld,
            object body,
            Vector3 position,
            float deltaTime,
            out WaterWake wake)
        {
            wake = default;
            if (!waders.TryGetValue(body, out var wader))
            {
                wader = new Wader(position);
            }

            var deltaX = position.x - wader.Position.x;
            var deltaZ = position.z - wader.Position.z;
            var travelled = Mathf.Sqrt((deltaX * deltaX) + (deltaZ * deltaZ));
            var speed = deltaTime > 0f ? travelled / deltaTime : 0f;
            wader.Position = position;
            wader.Frame = frame;
            if (!WaterRules.TryGetDepth(
                    collisionWorld,
                    new SimulationVector3(position.x, position.y, position.z),
                    queryBuffer,
                    out var depth,
                    out var bodyId)
                || depth < MinimumDepth)
            {
                wader.RingTimer = 0f;
                wader.Spray = 0f;
                waders[body] = wader;
                return false;
            }

            WadingCount++;
            var moving = speed >= MovingSpeed && travelled < TeleportDistance;
            var ringSize = 0f;

            // A body that starts to move leaves its next ring soon, not when a still one would.
            if (moving)
            {
                wader.RingTimer = Mathf.Min(wader.RingTimer, MovingRingSeconds);
            }

            wader.RingTimer -= deltaTime;
            if (wader.RingTimer <= 0f)
            {
                wader.RingTimer = moving ? MovingRingSeconds : StillRingSeconds;
                ringSize = moving ? Mathf.Lerp(1.4f, 2.2f, Mathf.Clamp01(speed / 5f)) : StillRingSize;
            }

            var spray = 0;
            var heading = Vector3.zero;
            if (moving)
            {
                wader.Spray += travelled * SprayPerMeter * Mathf.Clamp01(depth / SprayDepth);
                spray = Mathf.FloorToInt(wader.Spray);
                wader.Spray -= spray;
                heading = new Vector3(deltaX, 0f, deltaZ) / travelled;
            }

            waders[body] = wader;
            wake = new WaterWake(
                bodyId,
                new Vector3(position.x, position.y + depth, position.z),
                ringSize,
                spray,
                heading,
                moving ? speed : 0f);
            return true;
        }

        // Forgets the bodies that were not visited in the current frame.
        public void EndFrame()
        {
            departed.Clear();
            foreach (var pair in waders)
            {
                if (pair.Value.Frame != frame)
                {
                    departed.Add(pair.Key);
                }
            }

            foreach (var body in departed)
            {
                waders.Remove(body);
            }
        }

        private struct Wader
        {
            public Wader(Vector3 position)
            {
                Position = position;
                Frame = 0;
                RingTimer = 0f;
                Spray = 0f;
            }

            public Vector3 Position;
            public int Frame;
            public float RingTimer;
            public float Spray;
        }
    }

    // What one body leaves in the water in one frame: a ring of the given size around it, if
    // any, and droplets of spray thrown along its heading.
    public readonly struct WaterWake
    {
        public WaterWake(string bodyId, Vector3 surface, float ringSize, int spray, Vector3 heading, float speed)
        {
            BodyId = bodyId;
            Surface = surface;
            RingSize = ringSize;
            Spray = spray;
            Heading = heading;
            Speed = speed;
        }

        // The water body the body stands in.
        public string BodyId { get; }

        // The point on the water's still surface above the body's feet, before any waves.
        public Vector3 Surface { get; }

        public float RingSize { get; }

        public int Spray { get; }

        public Vector3 Heading { get; }

        public float Speed { get; }
    }
}
