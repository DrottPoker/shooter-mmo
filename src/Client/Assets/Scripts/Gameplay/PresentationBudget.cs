using System;
using System.Diagnostics;
using UnityEngine;

namespace Mmorpg.Client.Gameplay
{
    // The time a frame gives to putting things into view: the bodies of players, the views of world
    // actors, and the nameplates and quest markers of the actors that came into view. Every part takes
    // from the one budget of the frame, so what comes into view at once, as at a join among players
    // and Mobs, is spread over the frames that follow instead of stalling one. A part that paces
    // itself puts at least one thing into view a frame and stops once the budget is spent; a part
    // that follows what came into view, such as the nameplates, is charged and never waits, and what
    // a frame spends beyond its budget, at most one budget, the next frame has less of. Neither
    // timing nor charging allocates.
    public sealed class PresentationBudget
    {
        public const double MillisecondsPerFrame = 3.0;

        private readonly Stopwatch watch = new Stopwatch();
        private readonly double milliseconds;
        private readonly Func<int> frames;
        private int frame = int.MinValue;
        private double spent;
        private double available;
        private bool timing;

        public PresentationBudget()
            : this(MillisecondsPerFrame, null)
        {
        }

        // A budget of other milliseconds a frame and, for tests, another count of frames.
        internal PresentationBudget(double millisecondsPerFrame, Func<int> frameCount)
        {
            milliseconds = millisecondsPerFrame;
            frames = frameCount ?? (() => Time.frameCount);
            available = milliseconds;
        }

        public double Milliseconds
        {
            get { return milliseconds; }
        }

        // What this frame may spend: the budget, less what the frame before spent beyond its own.
        public double Available
        {
            get
            {
                Roll();
                return available;
            }
        }

        // What this frame spent so far, the work that is timed now included.
        public double Spent
        {
            get
            {
                Roll();
                return spent + (timing ? watch.Elapsed.TotalMilliseconds : 0.0);
            }
        }

        public bool IsSpent
        {
            get { return Spent >= Available; }
        }

        // Starts timing a part's work of this frame.
        public void Begin()
        {
            Roll();
            watch.Restart();
            timing = true;
        }

        // Ends the timing, which the frame has then spent.
        public void End()
        {
            if (!timing)
            {
                return;
            }

            spent += watch.Elapsed.TotalMilliseconds;
            watch.Stop();
            timing = false;
        }

        // The frame spent this much on work timed elsewhere.
        internal void Charge(double charged)
        {
            Roll();
            spent += charged;
        }

        private void Roll()
        {
            var now = frames();
            if (now == frame)
            {
                return;
            }

            // What the frame before spent beyond what it had, at most one budget, the next one has
            // less of, so a frame that a charged part overran evens out.
            var debt = now == frame + 1 ? Math.Min(milliseconds, Math.Max(0.0, spent - available)) : 0.0;
            frame = now;
            available = milliseconds - debt;
            spent = 0.0;
            if (timing)
            {
                watch.Restart();
            }
        }
    }
}
