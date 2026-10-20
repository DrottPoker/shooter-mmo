using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Mmorpg.Server.Common.Timing;

// Puts a dedicated loop thread to sleep for a short time with about a millisecond of
// precision. Ordinary waits on Windows round up to the 15.6 ms system timer tick, which made
// 30 Hz loops run their ticks up to a full tick late. A high-resolution waitable timer avoids
// that without raising the timer resolution of the whole process. Other platforms already
// wait with millisecond precision. One instance belongs to one thread.
public sealed class PreciseLoopTimer : IDisposable
{
    private readonly WaitableTimerHandle? timer;
    private readonly WaitHandle[] waitHandles = new WaitHandle[2];

    public PreciseLoopTimer()
    {
        if (OperatingSystem.IsWindows())
        {
            timer = WaitableTimerHandle.TryCreateHighResolution();
        }
    }

    // False only on Windows versions before 10 1803, where waits keep the system timer tick.
    public bool IsHighResolution => timer is not null || !OperatingSystem.IsWindows();

    // Blocks until the delay passed or the token was cancelled, and returns false when it
    // was cancelled.
    public bool Wait(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        if (delay <= TimeSpan.Zero)
        {
            return true;
        }

        if (OperatingSystem.IsWindows() && timer is not null)
        {
            timer.Start(delay);
            waitHandles[0] = timer;
            waitHandles[1] = cancellationToken.WaitHandle;
            return WaitHandle.WaitAny(waitHandles) == 0;
        }

        // A rounded-down timeout would return early and turn the caller into a spin loop.
        return !cancellationToken.WaitHandle.WaitOne(
            (int)Math.Min(int.MaxValue, Math.Ceiling(delay.TotalMilliseconds)));
    }

    public void Dispose()
    {
        timer?.Dispose();
    }

    [SupportedOSPlatform("windows")]
    private sealed class WaitableTimerHandle : WaitHandle
    {
        private const uint CreateWaitableTimerHighResolution = 0x00000002;
        private const uint TimerAllAccess = 0x001F0003;

        private WaitableTimerHandle(SafeWaitHandle handle)
        {
            SafeWaitHandle = handle;
        }

        public static WaitableTimerHandle? TryCreateHighResolution()
        {
            var handle = CreateWaitableTimerExW(
                IntPtr.Zero,
                null,
                CreateWaitableTimerHighResolution,
                TimerAllAccess);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                return null;
            }

            return new WaitableTimerHandle(handle);
        }

        public void Start(TimeSpan delay)
        {
            // A negative due time is relative, in 100 ns units like TimeSpan ticks.
            var dueTime = -Math.Max(1L, delay.Ticks);
            if (!SetWaitableTimer(SafeWaitHandle, in dueTime, 0, IntPtr.Zero, IntPtr.Zero, false))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeWaitHandle CreateWaitableTimerExW(
            IntPtr timerAttributes,
            string? timerName,
            uint flags,
            uint desiredAccess);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWaitableTimer(
            SafeWaitHandle timer,
            in long dueTime,
            int period,
            IntPtr completionRoutine,
            IntPtr completionRoutineArgument,
            [MarshalAs(UnmanagedType.Bool)] bool resume);
    }
}
