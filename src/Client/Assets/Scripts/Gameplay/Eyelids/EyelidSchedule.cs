using System.Collections.Generic;
using UnityEngine;

namespace Mmorpg.Client.Gameplay
{
    // When the eyes of the bodies in play blink, and how far their lids are closed, without a scene:
    // the bodies wait in a binary heap by the time of their next blink, so a frame touches only the
    // bodies whose time came and the few in the middle of a blink or closing in death, and none in
    // between. A body whose time comes blinks when the views say it may, and otherwise only gets its
    // next time. A blink takes BlinkSeconds, sometimes twice in a row, and the next comes 2 to 6 s
    // after it ends. A dying body closes its lids at the blink's closing speed and keeps them closed
    // without work; one that lives again opens them at once and blinks as before. The clock and the
    // random numbers come from outside, so the planning is exact in tests; EyelidScheduler steps it
    // once a frame in play. See docs/UNITY_CLIENT_ARCHITECTURE.md, Eyelids.
    public sealed class EyelidSchedule
    {
        // The time from the end of a blink to the start of the next, evenly spread.
        public const float MinIntervalSeconds = 2f;
        public const float MaxIntervalSeconds = 6f;

        // A body that comes into play blinks first within this time, so a crowd does not blink
        // together.
        public const float FirstSpreadSeconds = 6f;

        // A blink: the lids close, stay closed, and open, each eased.
        public const float CloseSeconds = 0.08f;
        public const float ClosedSeconds = 0.03f;
        public const float OpenSeconds = 0.14f;
        public const float BlinkSeconds = CloseSeconds + ClosedSeconds + OpenSeconds;

        // How often a second blink follows the first, and how long after the first ends.
        public const float DoubleChance = 0.15f;
        public const float DoubleGapSeconds = 0.12f;

        // The waiting bodies, a binary heap with the earliest blink first.
        private readonly List<Body> heap = new List<Body>();
        // The bodies that blink or close their lids in death.
        private readonly List<Body> moving = new List<Body>();

        // What the planning asks of the world: whether a body may blink now, as when it is seen up
        // close, and a random number from 0 up to 1, of the presentation only.
        public interface IViews
        {
            bool MayBlink(Body body);

            float NextRandom();
        }

        public enum BodyState
        {
            // Not in the schedule.
            None,
            // Waits for its next blink.
            Waiting,
            Blinking,
            // Closes its lids in death.
            Closing,
            // Lies dead with closed lids, without work.
            Closed,
        }

        // One body in the schedule, with the lids it turns, or none, as in the tests.
        public sealed class Body
        {
            internal int HeapIndex = -1;
            internal int MovingIndex = -1;
            internal double Started;
            internal float From;

            public Body(BodyEyelids lids = null)
            {
                Lids = lids;
            }

            public BodyEyelids Lids { get; }

            public BodyState State { get; internal set; }

            // How far the lids are closed, from 0, open, to 1, closed.
            public float Share { get; private set; }

            // When its next blink comes, while it waits.
            public double Due { get; internal set; }

            // Whether its blink is a double blink.
            public bool Twice { get; internal set; }

            internal void Show(float share)
            {
                Share = share;
                if (Lids != null)
                {
                    Lids.Show(share);
                }
            }
        }

        // How many bodies the schedule holds.
        public int Count { get; private set; }

        // How many of them wait for their next blink.
        public int Waiting
        {
            get { return heap.Count; }
        }

        // How many of them blink or close their lids in death.
        public int Moving
        {
            get { return moving.Count; }
        }

        // How many bodies the last Step touched.
        public int Touched { get; private set; }

        // A body comes into play: one that lies dead has its lids closed at once, and every other
        // has them open and blinks first within FirstSpreadSeconds.
        public void Add(Body body, double now, bool dead, IViews views)
        {
            if (body == null || body.State != BodyState.None)
            {
                return;
            }

            Count++;
            // Room for every body in both lists, so no step allocates.
            if (heap.Capacity < Count)
            {
                heap.Capacity = Mathf.Max(Count, heap.Capacity * 2);
                moving.Capacity = heap.Capacity;
            }

            if (dead)
            {
                body.State = BodyState.Closed;
                body.Show(1f);
                return;
            }

            body.Show(0f);
            Schedule(body, FirstBlinkAt(now, views.NextRandom()));
        }

        // A body leaves play; a blink in progress opens its lids and a closing one closes them.
        public void Remove(Body body)
        {
            if (body == null || body.State == BodyState.None)
            {
                return;
            }

            if (body.State == BodyState.Blinking)
            {
                body.Show(0f);
            }
            else if (body.State == BodyState.Closing)
            {
                body.Show(1f);
            }

            Unschedule(body);
            StopMoving(body);
            body.State = BodyState.None;
            Count--;
        }

        // The body's character dies: it closes its lids from where they are at the blink's closing
        // speed when it dies in view, or at once, and keeps them closed.
        public void Die(Body body, double now, bool gradually)
        {
            if (body == null || body.State == BodyState.None || body.State == BodyState.Closed
                || (gradually && body.State == BodyState.Closing))
            {
                return;
            }

            Unschedule(body);
            if (gradually)
            {
                body.State = BodyState.Closing;
                body.Started = now;
                body.From = body.Share;
                StartMoving(body);
                return;
            }

            StopMoving(body);
            body.State = BodyState.Closed;
            body.Show(1f);
        }

        // The body's character lives again: its lids open at once, and it blinks as before.
        public void Live(Body body, double now, IViews views)
        {
            if (body == null || body.State == BodyState.None || body.State == BodyState.Waiting
                || body.State == BodyState.Blinking)
            {
                return;
            }

            StopMoving(body);
            body.Show(0f);
            Schedule(body, NextBlinkAt(now, views.NextRandom()));
        }

        // Moves the lids of the bodies that blink or close, and starts the blinks whose time came.
        public void Step(double now, IViews views)
        {
            var touched = 0;
            // From the end, so a body that stops moving swaps in one already moved.
            for (var index = moving.Count - 1; index >= 0; index--)
            {
                var body = moving[index];
                touched++;
                var time = (float)(now - body.Started);
                if (body.State == BodyState.Closing)
                {
                    body.Show(ClosingShareAt(body.From, time));
                    if (time >= CloseSeconds)
                    {
                        body.State = BodyState.Closed;
                        StopMoving(body);
                    }

                    continue;
                }

                var length = LengthOf(body.Twice);
                if (time < length)
                {
                    body.Show(ShareAt(time, body.Twice));
                    continue;
                }

                body.Show(0f);
                StopMoving(body);
                Schedule(body, NextBlinkAt(body.Started + length, views.NextRandom()));
            }

            while (heap.Count > 0 && heap[0].Due <= now)
            {
                var body = heap[0];
                RemoveFromHeap(0);
                touched++;
                if (views.MayBlink(body))
                {
                    body.State = BodyState.Blinking;
                    body.Started = now;
                    body.Twice = IsDouble(views.NextRandom());
                    StartMoving(body);
                }
                else
                {
                    Schedule(body, NextBlinkAt(now, views.NextRandom()));
                }
            }

            Touched = touched;
        }

        // When a body that comes into play blinks first.
        public static double FirstBlinkAt(double now, float random)
        {
            return now + Mathf.Clamp01(random) * FirstSpreadSeconds;
        }

        // When the next blink comes after a blink that ended, or after a time that came without one.
        public static double NextBlinkAt(double after, float random)
        {
            return after + Mathf.Lerp(MinIntervalSeconds, MaxIntervalSeconds, random);
        }

        public static bool IsDouble(float random)
        {
            return random < DoubleChance;
        }

        // How long a blink lasts, a double blink with the gap between its two.
        public static float LengthOf(bool twice)
        {
            return twice ? BlinkSeconds + DoubleGapSeconds + BlinkSeconds : BlinkSeconds;
        }

        // How far the lids are closed a time into a blink.
        public static float ShareAt(float time, bool twice)
        {
            if (twice && time >= BlinkSeconds + DoubleGapSeconds)
            {
                time -= BlinkSeconds + DoubleGapSeconds;
            }

            if (time <= 0f)
            {
                return 0f;
            }

            if (time < CloseSeconds)
            {
                return Ease(time / CloseSeconds);
            }

            time -= CloseSeconds;
            if (time < ClosedSeconds)
            {
                return 1f;
            }

            time -= ClosedSeconds;
            return time < OpenSeconds ? 1f - Ease(time / OpenSeconds) : 0f;
        }

        // How far the lids of a dying body are closed a time after it began to close them from a
        // share, at the blink's closing speed.
        public static float ClosingShareAt(float from, float time)
        {
            return time >= CloseSeconds ? 1f : Mathf.Lerp(from, 1f, Ease(time / CloseSeconds));
        }

        // Smoothstep: slow at both ends.
        public static float Ease(float share)
        {
            share = Mathf.Clamp01(share);
            return share * share * (3f - 2f * share);
        }

        private void Schedule(Body body, double due)
        {
            body.State = BodyState.Waiting;
            body.Due = due;
            body.HeapIndex = heap.Count;
            heap.Add(body);
            SiftUp(body.HeapIndex);
        }

        private void Unschedule(Body body)
        {
            if (body.HeapIndex >= 0)
            {
                RemoveFromHeap(body.HeapIndex);
            }
        }

        private void RemoveFromHeap(int index)
        {
            var removed = heap[index];
            var last = heap.Count - 1;
            if (index != last)
            {
                var moved = heap[last];
                heap[index] = moved;
                moved.HeapIndex = index;
            }

            heap.RemoveAt(last);
            removed.HeapIndex = -1;
            if (index < heap.Count)
            {
                SiftDown(index);
                SiftUp(index);
            }
        }

        private void SiftUp(int index)
        {
            while (index > 0)
            {
                var parent = (index - 1) / 2;
                if (heap[parent].Due <= heap[index].Due)
                {
                    return;
                }

                Swap(index, parent);
                index = parent;
            }
        }

        private void SiftDown(int index)
        {
            while (true)
            {
                var first = index * 2 + 1;
                if (first >= heap.Count)
                {
                    return;
                }

                var earliest = first + 1 < heap.Count && heap[first + 1].Due < heap[first].Due ? first + 1 : first;
                if (heap[index].Due <= heap[earliest].Due)
                {
                    return;
                }

                Swap(index, earliest);
                index = earliest;
            }
        }

        private void Swap(int a, int b)
        {
            var first = heap[a];
            var second = heap[b];
            heap[a] = second;
            heap[b] = first;
            second.HeapIndex = a;
            first.HeapIndex = b;
        }

        private void StartMoving(Body body)
        {
            if (body.MovingIndex < 0)
            {
                body.MovingIndex = moving.Count;
                moving.Add(body);
            }
        }

        private void StopMoving(Body body)
        {
            var index = body.MovingIndex;
            if (index < 0)
            {
                return;
            }

            var last = moving.Count - 1;
            if (index != last)
            {
                var moved = moving[last];
                moving[index] = moved;
                moved.MovingIndex = index;
            }

            moving.RemoveAt(last);
            body.MovingIndex = -1;
        }
    }
}
