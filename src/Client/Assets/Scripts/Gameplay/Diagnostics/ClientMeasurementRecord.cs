using System;
using System.Collections.Generic;

namespace Mmorpg.Client.Gameplay
{
    // The distribution of a measured value: its count, average, p50, p95, p99, and maximum.
    // JsonUtility writes it, so its fields are public.
    [Serializable]
    public sealed class ClientMeasurementStatistics
    {
        public int count;
        public float average;
        public float p50;
        public float p95;
        public float p99;
        public float maximum;
    }

    // The reliable intents of one kind and the seconds from each to its result.
    [Serializable]
    public sealed class ClientIntentStatistics
    {
        public string kind;
        public int sent;
        public int answered;
        public ClientMeasurementStatistics seconds;
    }

    // One line of a measurement file: an interval, or the summary of the whole recording.
    [Serializable]
    public sealed class ClientMeasurementRecord
    {
        public const string IntervalKind = "interval";
        public const string SummaryKind = "summary";

        public string kind;
        public string label;
        public float startSeconds;
        public float durationSeconds;

        // How long of it the local player was in the World, which the rates per minute count.
        public float worldSeconds;

        // How long frames took.
        public ClientMeasurementStatistics frameMilliseconds;

        // The round trip that the transport reported, once a frame in the World.
        public ClientMeasurementStatistics roundTripMilliseconds;

        // Owner snapshots: the oldest the newest one became, the frames received and missing,
        // and the silences longer than a second, such as an outage of the line.
        public float largestSnapshotAgeSeconds;
        public long ownerSnapshotFrames;
        public long ownerSnapshotFramesMissing;
        public float ownerSnapshotLossPercent;
        public int silences;
        public float longestSilenceSeconds;

        // How far owner snapshots corrected the prediction, and the corrections of 3 m and more
        // that the presentation shows as a snap.
        public int corrections;
        public int hardSnaps;
        public float hardSnapsPerMinute;
        public ClientMeasurementStatistics correctionMeters;

        // How far the presented local player moved between two frames beyond what its walk speed
        // explains, which a player sees as rubber banding.
        public float largestUnexplainedJumpMeters;
        public int framesWithUnexplainedJump;
        public float unexplainedJumpMeters;

        // The largest correction and jump in the two seconds after a silence ended.
        public float largestCorrectionAfterSilenceMeters;
        public float largestJumpAfterSilenceMeters;

        // Remote entities in view, in entity-seconds, and how long they ran out of snapshots
        // while they moved.
        public float remoteObservedEntitySeconds;
        public float remoteStarvedEntitySeconds;
        public float remoteStarvedPercent;
        public int remoteStarvations;
        public float longestRemoteStarvationSeconds;

        // Reliable intents and the seconds to their results, over all kinds and per kind.
        public int intentsSent;
        public int intentsAnswered;
        public ClientMeasurementStatistics intentSeconds;
        public ClientIntentStatistics[] intents;
    }

    // Measured values for percentiles. Percentiles are of the sorted values, rank rounded up.
    public sealed class MeasurementSamples
    {
        private readonly List<float> values = new List<float>();
        private readonly List<float> sorted = new List<float>();
        private double sum;
        private float maximum;

        public int Count
        {
            get { return values.Count; }
        }

        public float Maximum
        {
            get { return values.Count == 0 ? 0f : maximum; }
        }

        public void Add(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                return;
            }

            maximum = values.Count == 0 ? value : Math.Max(maximum, value);
            values.Add(value);
            sum += value;
        }

        public void Clear()
        {
            values.Clear();
            sum = 0d;
            maximum = 0f;
        }

        public float Percentile(float fraction)
        {
            if (values.Count == 0)
            {
                return 0f;
            }

            sorted.Clear();
            sorted.AddRange(values);
            sorted.Sort();
            return sorted[Rank(Math.Max(0f, Math.Min(1f, fraction)), sorted.Count)];
        }

        public ClientMeasurementStatistics ToStatistics()
        {
            var statistics = new ClientMeasurementStatistics { count = values.Count };
            if (values.Count == 0)
            {
                return statistics;
            }

            sorted.Clear();
            sorted.AddRange(values);
            sorted.Sort();
            statistics.average = (float)(sum / values.Count);
            statistics.p50 = At(0.50f);
            statistics.p95 = At(0.95f);
            statistics.p99 = At(0.99f);
            statistics.maximum = maximum;
            return statistics;
        }

        private float At(float fraction)
        {
            return sorted[Rank(fraction, sorted.Count)];
        }

        // The index of the value at the fraction, the rank rounded up. The fraction is taken as
        // the decimal it was written as, because 0.99f times 100 is a little above 99 and would
        // take the 100th value.
        private static int Rank(float fraction, int count)
        {
            var index = (int)Math.Ceiling((decimal)fraction * count) - 1;
            return Math.Max(0, Math.Min(count - 1, index));
        }
    }

    // Finds rubber banding in the presented position of the local player: the part of a frame's
    // horizontal movement that its walk speed does not explain. A frame without a bound player
    // or with another player starts over, so a join or a reconnect that places the player anew
    // counts no jump.
    public sealed class RenderedMotionTracker
    {
        private bool hasPrevious;
        private object previousOwner;
        private float previousX;
        private float previousZ;

        // Returns the unexplained meters of this frame, 0 for a frame that starts over.
        public float Track(object owner, bool isBound, float x, float z, float walkSpeed, float deltaSeconds)
        {
            if (!isBound || owner == null || !ReferenceEquals(owner, previousOwner) || !hasPrevious)
            {
                hasPrevious = isBound && owner != null;
                previousOwner = owner;
                previousX = x;
                previousZ = z;
                return 0f;
            }

            var dx = x - previousX;
            var dz = z - previousZ;
            previousX = x;
            previousZ = z;
            var moved = (float)Math.Sqrt((dx * dx) + (dz * dz));
            var explained = Math.Max(0f, walkSpeed) * Math.Max(0f, deltaSeconds);
            return Math.Max(0f, moved - explained);
        }

        public void Reset()
        {
            hasPrevious = false;
            previousOwner = null;
        }
    }
}
