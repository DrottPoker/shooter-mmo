using System;
using UnityEngine.Profiling;

namespace Mmorpg.Client.Tests.EditMode
{
    // Counts the managed allocations of code on the test's thread, from the profiler's GC.Alloc
    // samples, for the hot paths that must allocate nothing. GC.GetAllocatedBytesForCurrentThread
    // reads 0 in the Editor's runtime, so it cannot show them.
    internal static class ManagedAllocations
    {
        // The allocations of the action's second run; the first warms up what a first call
        // allocates once, such as a compiled method or a list that grows to its size.
        public static int Of(Action action)
        {
            action();
            var recorder = Recorder.Get("GC.Alloc");
            recorder.FilterToCurrentThread();
            recorder.enabled = false;
            recorder.enabled = true;
            action();
            recorder.enabled = false;
            var count = recorder.sampleBlockCount;
            recorder.CollectFromAllThreads();
            return count;
        }
    }
}
