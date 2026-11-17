using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Mmorpg.SimulationWorker.Realtime;

// The CPU time of the thread that reads it, so the realtime loop can tell how long a tick ran
// and how long it waited to run: wall time minus CPU time is time the thread did not run, such
// as when other work preempted it. A read is one system call, 0.35 to 0.5 microseconds on
// Windows, and allocates nothing. Reads that are compared must come from the same thread.
internal abstract class ThreadCpuClock
{
    // What the clock reads, for the worker's start log line.
    public abstract string Description { get; }

    // The CPU time of the calling thread so far, in the clock's own units.
    public abstract long Read();

    // The CPU time between two reads of one thread in milliseconds, at most the wall time
    // between them.
    public abstract double Milliseconds(long start, long end, double wallMilliseconds);

    // On Windows the thread's cycle count, on 64-bit Linux CLOCK_THREAD_CPUTIME_ID, and null
    // elsewhere or when the system does not answer.
    public static ThreadCpuClock? ForCurrentPlatform()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return WindowsThreadCycleClock.TryCreate();
            }

            if (OperatingSystem.IsLinux() && Environment.Is64BitProcess)
            {
                return LinuxThreadCpuClock.TryCreate();
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
        }

        return null;
    }

    // Windows counts the cycles a thread ran at the rate of the processor's time stamp counter,
    // which does not follow the clock speed. The rate comes from short spins when the first clock
    // is made, the fastest of them, since a spin that was preempted only shows a lower one; after
    // that every span of at least two milliseconds may raise it, since no span runs faster than
    // the counter.
    [SupportedOSPlatform("windows")]
    private sealed class WindowsThreadCycleClock : ThreadCpuClock
    {
        private const double MinimumRateSpanMilliseconds = 2d;
        private static readonly nint CurrentThread = -2;
        private static readonly Lazy<double> CalibratedCyclesPerMillisecond = new(Calibrate);
        private double cyclesPerMillisecond;

        private WindowsThreadCycleClock(double cyclesPerMillisecond)
        {
            this.cyclesPerMillisecond = cyclesPerMillisecond;
        }

        public override string Description =>
            $"the thread's cycle count at {cyclesPerMillisecond / 1_000_000d:0.000} GHz";

        public static WindowsThreadCycleClock? TryCreate()
        {
            if (QueryThreadCycleTime(CurrentThread, out _) == 0)
            {
                return null;
            }

            var rate = CalibratedCyclesPerMillisecond.Value;
            return rate > 0d ? new WindowsThreadCycleClock(rate) : null;
        }

        public override long Read()
        {
            QueryThreadCycleTime(CurrentThread, out var cycles);
            return unchecked((long)cycles);
        }

        public override double Milliseconds(long start, long end, double wallMilliseconds)
        {
            var cycles = Math.Max(0L, end - start);
            if (wallMilliseconds >= MinimumRateSpanMilliseconds)
            {
                var rate = cycles / wallMilliseconds;
                if (rate > cyclesPerMillisecond)
                {
                    cyclesPerMillisecond = rate;
                }
            }

            return Math.Min(Math.Max(0d, wallMilliseconds), cycles / cyclesPerMillisecond);
        }

        private static double Calibrate()
        {
            var fastest = 0d;
            for (var spin = 0; spin < 10; spin++)
            {
                QueryThreadCycleTime(CurrentThread, out var startCycles);
                var started = Stopwatch.GetTimestamp();
                while (Stopwatch.GetElapsedTime(started).TotalMilliseconds < 1d)
                {
                }

                QueryThreadCycleTime(CurrentThread, out var endCycles);
                var milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                fastest = Math.Max(fastest, (endCycles - startCycles) / milliseconds);
            }

            return fastest;
        }

        [DllImport("kernel32.dll")]
        private static extern int QueryThreadCycleTime(nint threadHandle, out ulong cycleTime);
    }

    [SupportedOSPlatform("linux")]
    private sealed class LinuxThreadCpuClock : ThreadCpuClock
    {
        private const int ClockThreadCpuTimeId = 3;

        public override string Description => "CLOCK_THREAD_CPUTIME_ID";

        public static LinuxThreadCpuClock? TryCreate()
        {
            return ClockGetTime(ClockThreadCpuTimeId, out _) == 0 ? new LinuxThreadCpuClock() : null;
        }

        public override long Read()
        {
            ClockGetTime(ClockThreadCpuTimeId, out var time);
            return (time.Seconds * 1_000_000_000L) + time.Nanoseconds;
        }

        public override double Milliseconds(long start, long end, double wallMilliseconds)
        {
            return Math.Min(Math.Max(0d, wallMilliseconds), Math.Max(0L, end - start) / 1_000_000d);
        }

        [DllImport("libc.so.6", EntryPoint = "clock_gettime")]
        private static extern int ClockGetTime(int clockId, out Timespec time);

        [StructLayout(LayoutKind.Sequential)]
        private struct Timespec
        {
            public long Seconds;
            public long Nanoseconds;
        }
    }
}
