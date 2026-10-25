using System;
using System.Collections.Generic;
using System.Linq;
using Mmorpg.Client.Gameplay;
using UnityEngine;

namespace Mmorpg.Client.Editor
{
    // The motions loose cloth is measured and reviewed in: the clips as the game plays them, and
    // stress motions that move and turn the body's root over them, as a fast turn, the spin of an
    // attack, a stop, a zig-zag, a body seen over the network, jittery frame times, a teleport, and
    // hard corrections. They need no clip of their own: the root follows the motion's course and
    // the Animator plays the locomotion clips at its speed. ClothMotionMeter measures the cloth in
    // them, ClothMeasurement runs them all, and the Wardrobe plays them. See
    // docs/EQUIPMENT_VISUALS_DESIGN.md.
    public sealed class ClothStressMotion
    {
        // The network the remote bodies are seen through: the worker's ticks and snapshots a second.
        public const int NetworkTickRateHz = 30;
        public const int NetworkSnapshotRateHz = 15;

        // The course is sampled this often, and the root takes it between samples.
        private const float SampleSeconds = 0.001f;

        // A hold's motion is steady once its start is this far behind.
        private const float SteadyAfter = 0.5f;

        private readonly Vector3[] positions;
        private readonly float[] yaws;
        private readonly float[] speeds;
        private readonly float deadFrom;

        private ClothStressMotion(string name, Course course, float deadFrom = float.PositiveInfinity, bool network = false,
            bool jitteryFrames = false)
        {
            Name = name;
            Seconds = course.Seconds;
            Steady = course.Steady;
            Settle = course.Settle;
            Spin = course.Spin;
            Jumps = course.Jumps;
            Network = network;
            JitteryFrames = jitteryFrames;
            this.deadFrom = deadFrom;
            course.Sample(SampleSeconds, out positions, out yaws, out speeds);
        }

        // Where the root stands, which way it faces in degrees around up, the speed the Animator
        // plays the clips at, and whether the body is dead.
        public readonly struct Pose
        {
            public Pose(Vector3 position, float yaw, float speed, bool dead)
            {
                Position = position;
                Yaw = yaw;
                Speed = speed;
                Dead = dead;
            }

            public Vector3 Position { get; }

            public float Yaw { get; }

            public float Speed { get; }

            public bool Dead { get; }
        }

        public string Name { get; }

        public float Seconds { get; }

        // The phases of steady motion, from their start to their end in seconds, where the cloth
        // should move smoothly.
        public IReadOnlyList<(float Start, float End)> Steady { get; }

        // When the body stands still after a stop or a spin, from the start to the end in seconds,
        // and the cloth should settle.
        public (float Start, float End)? Settle { get; }

        // When the body spins, from the start to the end in seconds.
        public (float Start, float End)? Spin { get; }

        // When the root jumps, as in a teleport or a hard correction.
        public IReadOnlyList<float> Jumps { get; }

        // Whether the root is seen as a remote body: through RemoteMovementPresenter, from snapshots
        // of the course at NetworkSnapshotRateHz.
        public bool Network { get; }

        // Whether the frame times jump between 1/240 s and 1/20 s.
        public bool JitteryFrames { get; }

        public static IReadOnlyList<ClothStressMotion> All { get; } = Build();

        public static ClothStressMotion Named(string name)
        {
            foreach (var motion in All)
            {
                if (motion.Name == name)
                {
                    return motion;
                }
            }

            throw new ArgumentException("No cloth stress motion is named " + name + ".");
        }

        // The pose at a time of the motion, clamped to its length.
        public Pose At(float seconds)
        {
            var place = Mathf.Clamp(seconds, 0f, Seconds) / SampleSeconds;
            var index = Mathf.Min(Mathf.FloorToInt(place), positions.Length - 1);
            var next = Mathf.Min(index + 1, positions.Length - 1);
            var share = Mathf.Clamp01(place - index);
            // A jump lands at once at its sample, not across the step before it.
            if ((positions[next] - positions[index]).sqrMagnitude > 1f || Mathf.Abs(Mathf.DeltaAngle(yaws[index], yaws[next])) > 45f)
            {
                share = 0f;
            }

            return new Pose(Vector3.Lerp(positions[index], positions[next], share), Mathf.LerpAngle(yaws[index], yaws[next], share),
                speeds[index], seconds >= deadFrom);
        }

        // The frame times of the motion: 1/60 s, or for jittery frames a seeded run between 1/240 s
        // and 1/20 s, so a run can be repeated.
        public IEnumerable<float> Frames(float fixedSeconds = 1f / 60f, int seed = 1)
        {
            var random = new System.Random(seed);
            for (var time = 0f; time < Seconds;)
            {
                var step = JitteryFrames ? Mathf.Lerp(1f / 240f, 1f / 20f, (float)random.NextDouble()) : fixedSeconds;
                time += step;
                yield return step;
            }
        }

        private static IReadOnlyList<ClothStressMotion> Build()
        {
            var walk = HumanLocomotion.WalkSpeed;
            var jog = HumanLocomotion.JogSpeed;
            var motions = new List<ClothStressMotion>
            {
                new ClothStressMotion("Idle", new Course().Hold(4f, 0f)),
                new ClothStressMotion("Walk", new Course().Hold(6f, walk)),
                new ClothStressMotion("Jog", new Course().Hold(6f, jog)),
                new ClothStressMotion("Death", new Course().Hold(0.5f, 0f).Hold(3.5f, 0f), deadFrom: 0.5f),
            };
            foreach (var rate in new[] { 720f, 1440f, 2880f })
            {
                motions.Add(new ClothStressMotion("Spin " + rate, new Course().Hold(1f, 0f).SpinFor(1.5f, rate).Rest(3f)));
            }

            foreach (var angle in new[] { 90f, 180f })
            {
                motions.Add(new ClothStressMotion("Turn " + angle, new Course().Hold(2f, jog).Turn(angle, 720f, jog).Hold(2f, jog)));
            }

            motions.Add(new ClothStressMotion("Jog stop jog", new Course().Hold(2f, jog).Rest(2f).Hold(2f, jog)));
            motions.Add(new ClothStressMotion("Zig-zag", ZigZag()));
            motions.Add(new ClothStressMotion("Network 15 Hz", ZigZag(), network: true));
            motions.Add(new ClothStressMotion("Jittery frames", new Course().Hold(6f, jog), jitteryFrames: true));
            motions.Add(new ClothStressMotion("Teleport", new Course().Hold(2f, jog).Jump(new Vector3(0f, 0f, 100f), 0f).Hold(2f, jog)));
            motions.Add(new ClothStressMotion("Snap 3 m", new Course().Hold(2f, jog).Jump(new Vector3(3f, 0f, 0f), 0f).Hold(2f, jog)));
            motions.Add(new ClothStressMotion("Snap 180", new Course().Hold(2f, 0f).Jump(Vector3.zero, 180f).Rest(2f)));
            return motions;
        }

        // Jogging in sharp corners of 90 degrees, turning at the simulation's 720 degrees a second.
        private static Course ZigZag()
        {
            var course = new Course().Hold(1f, HumanLocomotion.JogSpeed);
            var start = course.Seconds;
            for (var corner = 0; corner < 8; corner++)
            {
                course.Turn(corner % 2 == 0 ? 90f : -90f, 720f, HumanLocomotion.JogSpeed).Hold(0.35f, HumanLocomotion.JogSpeed);
            }

            return course.SteadyBetween(start, course.Seconds);
        }

        // A course of segments: holding a speed straight ahead, turning at a rate while moving,
        // spinning on the spot, and jumping.
        private sealed class Course
        {
            private readonly List<(float Seconds, float Speed, float TurnRate, Vector3 Jump, float JumpYaw)> segments =
                new List<(float, float, float, Vector3, float)>();

            private readonly List<(float Start, float End)> steady = new List<(float, float)>();
            private readonly List<float> jumps = new List<float>();

            public float Seconds { get; private set; }

            public IReadOnlyList<(float Start, float End)> Steady => steady;

            public (float Start, float End)? Settle { get; private set; }

            public (float Start, float End)? Spin { get; private set; }

            public IReadOnlyList<float> Jumps => jumps;

            // Holding a speed straight ahead; from half a second in, a hold of a second or more is steady.
            public Course Hold(float seconds, float speed)
            {
                if (seconds >= 1f)
                {
                    steady.Add((Seconds + SteadyAfter, Seconds + seconds));
                }

                return Add(seconds, speed, 0f);
            }

            // Standing still after moving, where the cloth settles; its last two thirds are steady.
            public Course Rest(float seconds)
            {
                Settle = (Seconds, Seconds + seconds);
                steady.Add((Seconds + seconds / 3f, Seconds + seconds));
                return Add(seconds, 0f, 0f);
            }

            public Course Turn(float degrees, float rate, float speed)
            {
                return Add(Mathf.Abs(degrees) / rate, speed, Mathf.Sign(degrees) * rate);
            }

            public Course SpinFor(float seconds, float rate)
            {
                Spin = (Seconds, Seconds + seconds);
                return Add(seconds, 0f, rate);
            }

            public Course Jump(Vector3 offset, float yaw)
            {
                jumps.Add(Seconds);
                segments.Add((0f, 0f, 0f, offset, yaw));
                return this;
            }

            public Course SteadyBetween(float start, float end)
            {
                steady.Add((start, end));
                return this;
            }

            // The course every step from its start: a whole number of steps per segment, so a turn,
            // a spin, and a jump start exactly when their segment does, and a jump moves the samples
            // from its time on.
            public void Sample(float step, out Vector3[] positions, out float[] yaws, out float[] speeds)
            {
                var steps = segments.Select(segment => Mathf.RoundToInt(segment.Seconds / step)).ToArray();
                var count = steps.Sum() + 1;
                positions = new Vector3[count];
                yaws = new float[count];
                speeds = new float[count];
                var position = Vector3.zero;
                var yaw = 0f;
                var index = 0;
                for (var segment = 0; segment < segments.Count; segment++)
                {
                    var (_, speed, turnRate, jump, jumpYaw) = segments[segment];
                    position += Quaternion.Euler(0f, yaw, 0f) * jump;
                    yaw += jumpYaw;
                    for (var taken = 0; taken < steps[segment]; taken++, index++)
                    {
                        positions[index] = position;
                        yaws[index] = yaw;
                        speeds[index] = speed;
                        yaw += turnRate * step;
                        position += Quaternion.Euler(0f, yaw, 0f) * Vector3.forward * (speed * step);
                    }
                }

                positions[index] = position;
                yaws[index] = yaw;
                speeds[index] = segments.Count > 0 ? segments[segments.Count - 1].Speed : 0f;
            }

            private Course Add(float seconds, float speed, float turnRate)
            {
                segments.Add((seconds, speed, turnRate, Vector3.zero, 0f));
                Seconds += seconds;
                return this;
            }
        }
    }
}
