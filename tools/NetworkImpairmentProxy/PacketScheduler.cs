using System.Buffers;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Mmorpg.Tools.NetworkImpairmentProxy;

// A packet that waits in the proxy until its due time. The buffer is rented and goes back to the
// pool once the packet left or was discarded.
public sealed class DelayedPacket(object flow, byte[] buffer, int length, long arrivalTicks, long dueTicks)
{
    public object Flow { get; } = flow;

    public byte[] Buffer { get; } = buffer;

    public int Length { get; } = length;

    public long ArrivalTicks { get; } = arrivalTicks;

    public long DueTicks { get; } = dueTicks;

    public static DelayedPacket Copy(object flow, ReadOnlySpan<byte> payload, long arrivalTicks, long dueTicks)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(1, payload.Length));
        payload.CopyTo(buffer);
        return new DelayedPacket(flow, buffer, payload.Length, arrivalTicks, dueTicks);
    }

    public ReadOnlySpan<byte> Payload => Buffer.AsSpan(0, Length);

    public void Release()
    {
        ArrayPool<byte>.Shared.Return(Buffer);
    }
}

// The packets of one direction ordered by due time; packets due at the same time keep the order
// in which they arrived, so a profile without jitter never reorders. Not thread-safe.
public sealed class DelayQueue
{
    private readonly PriorityQueue<DelayedPacket, (long Due, long Sequence)> queue = new();
    private long sequence;

    public int Count => queue.Count;

    public long? NextDueTicks => queue.TryPeek(out _, out var priority) ? priority.Due : null;

    public void Add(DelayedPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        queue.Enqueue(packet, (packet.DueTicks, sequence++));
    }

    public bool TryTakeDue(long nowTicks, out DelayedPacket packet)
    {
        if (queue.TryPeek(out var next, out var priority) && priority.Due <= nowTicks)
        {
            queue.Dequeue();
            packet = next;
            return true;
        }

        packet = null!;
        return false;
    }

    public List<DelayedPacket> TakeAll()
    {
        var all = new List<DelayedPacket>(queue.Count);
        while (queue.TryDequeue(out var packet, out _))
        {
            all.Add(packet);
        }

        return all;
    }
}

// Sends the packets of one direction at their due time on a dedicated thread. Accuracy is what the
// proxy is for: an ordinary wait on Windows rounds up to the 15.6 ms timer tick, which would
// measure the proxy instead of the profile. The thread sleeps on a high-resolution waitable timer,
// as the worker's loop does, until shortly before the next due time, and spins the rest. A new
// packet wakes it, because jitter can make it due before the packet it waits for.
public sealed class PacketScheduler : IDisposable
{
    // The last stretch before a due time that the thread spins instead of sleeping. The waitable
    // timer wakes within it almost always, and a longer stretch made the thread spin through the
    // gaps between the packets of a busy run: at 6,000 packets per second and direction, 1 ms
    // cost a core more for no better p99.
    public static readonly TimeSpan DefaultSpin = TimeSpan.FromMilliseconds(0.3);

    // A proxy that falls this far behind drops instead of growing without bound.
    public const int DefaultMaximumQueuedPackets = 200_000;

    private readonly ConcurrentQueue<DelayedPacket> incoming = new();
    private readonly DelayQueue queue = new();
    private readonly Func<DelayedPacket, bool> send;
    private readonly DirectionCounters counters;
    private readonly Func<long> clock;
    private readonly long spinTicks;
    private readonly int maximumQueued;
    private readonly AutoResetEvent wake = new(false);
    private Thread? thread;
    private int pending;
    private int sleeping;
    private volatile bool stopping;

    public PacketScheduler(
        Func<DelayedPacket, bool> send,
        DirectionCounters counters,
        Func<long>? clock = null,
        TimeSpan? spin = null,
        int maximumQueued = DefaultMaximumQueuedPackets)
    {
        this.send = send ?? throw new ArgumentNullException(nameof(send));
        this.counters = counters ?? throw new ArgumentNullException(nameof(counters));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumQueued);
        this.clock = clock ?? Stopwatch.GetTimestamp;
        spinTicks = ProxyTime.Ticks((spin ?? DefaultSpin).TotalMilliseconds);
        this.maximumQueued = maximumQueued;
    }

    // The packets that wait, received or queued.
    public int Pending => Volatile.Read(ref pending);

    public void Start(string name)
    {
        if (thread is not null)
        {
            throw new InvalidOperationException("The scheduler runs already.");
        }

        thread = new Thread(Run)
        {
            IsBackground = true,
            Name = name,
            Priority = ThreadPriority.Highest
        };
        thread.Start();
    }

    // Takes a packet from any thread; false when the proxy fell so far behind that it drops.
    public bool Schedule(DelayedPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (Interlocked.Increment(ref pending) > maximumQueued)
        {
            Interlocked.Decrement(ref pending);
            packet.Release();
            counters.RecordOverflow();
            return false;
        }

        incoming.Enqueue(packet);
        if (Volatile.Read(ref sleeping) == 1)
        {
            wake.Set();
        }

        return true;
    }

    // Moves the received packets into the queue and sends every packet that is due by the clock,
    // each stamped with the clock when it left. The thread calls it in its loop; tests call it
    // with a fake clock instead of starting the thread.
    public int Pump()
    {
        while (incoming.TryDequeue(out var packet))
        {
            queue.Add(packet);
        }

        var sent = 0;
        var now = clock();
        while (queue.TryTakeDue(now, out var due))
        {
            Interlocked.Decrement(ref pending);
            try
            {
                if (send(due))
                {
                    var left = clock();
                    counters.RecordSent(
                        due.Length,
                        ProxyTime.Milliseconds(left - due.ArrivalTicks),
                        ProxyTime.Milliseconds(left - due.DueTicks));
                    sent++;
                }
                else
                {
                    counters.RecordSendFailure();
                }
            }
            finally
            {
                due.Release();
            }

            now = clock();
        }

        return sent;
    }

    public long? NextDueTicks => queue.NextDueTicks;

    public void Dispose()
    {
        stopping = true;
        wake.Set();
        thread?.Join();
        var discarded = 0;
        while (incoming.TryDequeue(out var packet))
        {
            packet.Release();
            discarded++;
        }

        foreach (var packet in queue.TakeAll())
        {
            packet.Release();
            discarded++;
        }

        Interlocked.Add(ref pending, -discarded);
        counters.RecordDiscardedAtStop(discarded);
        wake.Dispose();
    }

    private void Run()
    {
        using var waiter = new PreciseWaiter();
        while (!stopping)
        {
            Pump();
            if (queue.NextDueTicks is not { } due)
            {
                Sleep(waiter, null);
                continue;
            }

            var remaining = due - clock();
            if (remaining <= 0)
            {
                continue;
            }

            if (remaining > spinTicks)
            {
                Sleep(waiter, TimeSpan.FromTicks((long)((remaining - spinTicks) * (double)TimeSpan.TicksPerSecond / Stopwatch.Frequency)));
            }
            else
            {
                // Spins briefly and looks again, so a new packet is queued while it spins.
                Thread.SpinWait(20);
            }
        }
    }

    // Sleeps until the delay passed or a packet arrives. The flag is set before the queue is
    // checked, and Interlocked makes both a full fence, so a packet that arrives meanwhile
    // either is seen here or sets the event.
    private void Sleep(PreciseWaiter waiter, TimeSpan? delay)
    {
        Interlocked.Exchange(ref sleeping, 1);
        try
        {
            if (!incoming.IsEmpty || stopping)
            {
                return;
            }

            if (delay is { } wait)
            {
                waiter.Wait(wait, wake);
            }
            else
            {
                wake.WaitOne(TimeSpan.FromMilliseconds(100));
            }
        }
        finally
        {
            Interlocked.Exchange(ref sleeping, 0);
        }
    }
}

// Sleeps a dedicated thread for a short time with sub-millisecond precision, or until a wake
// handle is set. On Windows it uses a high-resolution waitable timer, like the worker's
// PreciseLoopTimer, without raising the timer resolution of the whole machine; elsewhere waits
// are precise to the millisecond already. One instance belongs to one thread.
public sealed class PreciseWaiter : IDisposable
{
    private readonly WaitableTimer? timer;
    private readonly WaitHandle[] handles = new WaitHandle[2];

    public PreciseWaiter()
    {
        if (OperatingSystem.IsWindows())
        {
            timer = WaitableTimer.TryCreateHighResolution();
        }
    }

    public bool IsHighResolution => timer is not null || !OperatingSystem.IsWindows();

    // Returns true when the time passed and false when the wake handle was set.
    public bool Wait(TimeSpan delay, WaitHandle wake)
    {
        ArgumentNullException.ThrowIfNull(wake);
        if (delay <= TimeSpan.Zero)
        {
            return true;
        }

        if (OperatingSystem.IsWindows() && timer is not null)
        {
            timer.Start(delay);
            handles[0] = timer;
            handles[1] = wake;
            return WaitHandle.WaitAny(handles) == 0;
        }

        // A rounded-down timeout would return early and spin.
        return !wake.WaitOne((int)Math.Min(int.MaxValue, Math.Ceiling(delay.TotalMilliseconds)));
    }

    public void Dispose()
    {
        timer?.Dispose();
    }

    [SupportedOSPlatform("windows")]
    private sealed class WaitableTimer : WaitHandle
    {
        private const uint CreateWaitableTimerHighResolution = 0x00000002;
        private const uint TimerAllAccess = 0x001F0003;

        private WaitableTimer(SafeWaitHandle handle)
        {
            SafeWaitHandle = handle;
        }

        public static WaitableTimer? TryCreateHighResolution()
        {
            var handle = CreateWaitableTimerExW(IntPtr.Zero, null, CreateWaitableTimerHighResolution, TimerAllAccess);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                return null;
            }

            return new WaitableTimer(handle);
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
