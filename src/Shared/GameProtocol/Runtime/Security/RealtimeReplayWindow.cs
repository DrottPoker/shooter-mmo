using System;

namespace Mmorpg.GameProtocol
{
    // The counters of one direction that a receiver has accepted: the highest one, and of the
    // Size counters up to it which arrived. A counter seen before, or older than the window, is
    // a replay. Check only reads; Accept records a counter once its datagram authenticated, so a
    // forged datagram never moves the window. Not thread-safe.
    public sealed class RealtimeReplayWindow
    {
        public const int Size = 1024;

        private readonly ulong[] seen = new ulong[Size / 64];
        private ulong highest;
        private bool any;

        public bool Check(ulong counter)
        {
            if (!any || counter > highest)
            {
                return true;
            }

            if (highest - counter >= Size)
            {
                return false;
            }

            return (seen[Slot(counter)] & Bit(counter)) == 0;
        }

        public void Accept(ulong counter)
        {
            if (!any)
            {
                any = true;
                highest = counter;
                seen[Slot(counter)] |= Bit(counter);
                return;
            }

            if (counter > highest)
            {
                // The slots of the counters that enter the window held counters that leave it.
                var advance = counter - highest;
                if (advance >= Size)
                {
                    Array.Clear(seen, 0, seen.Length);
                }
                else
                {
                    // Counted by steps, not up to the counter: the peer chooses its counters, and
                    // a loop up to the largest counter would never end.
                    for (ulong step = 1; step <= advance; step++)
                    {
                        var next = highest + step;
                        seen[Slot(next)] &= ~Bit(next);
                    }
                }

                highest = counter;
            }

            seen[Slot(counter)] |= Bit(counter);
        }

        private static int Slot(ulong counter)
        {
            return (int)((counter % Size) / 64);
        }

        private static ulong Bit(ulong counter)
        {
            return 1UL << (int)(counter % 64);
        }
    }
}
