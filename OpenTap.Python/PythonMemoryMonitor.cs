using System;
using System.Diagnostics;
using System.Threading;
using Python.Runtime;

namespace OpenTap.Python
{
    /// <summary>
    /// Periodically reclaims managed and native memory during long-running
    /// (potentially infinite-loop) test plan executions.
    /// <para>
    /// <b>Why this is needed:</b> pythonnet defers <c>Py_DecRef</c> calls from
    /// .NET finalizer threads into a queue (<c>Finalizer.Instance</c>) because they
    /// don't hold the Python GIL.  The queue is normally drained opportunistically
    /// every 200 <c>PyObject</c> constructions (<c>ThrottledCollect</c>), but in
    /// long-running plans — especially infinite-loop patterns where
    /// <c>ExitTestPlanRun</c> never fires — the queue can grow faster than it is
    /// drained, and native memory freed by Python's allocator is not returned to
    /// the OS (malloc fragmentation on Linux).
    /// </para>
    /// <para>
    /// <b>reflectedObjects cleanup:</b> The reflected-objects tracking set is now
    /// maintained at the source in pythonnet:
    /// <c>ClassBase.tp_clear</c> removes entries for regular CLR wrappers when
    /// they are deallocated, and <c>ClassDerivedObject.tp_dealloc</c> /
    /// <c>ToPython</c> manage entries for Python-derived types across their
    /// dealloc/resurrection lifecycle.  No external eviction sweep is needed.
    /// </para>
    /// <para>
    /// This monitor uses a dedicated background thread (NOT a
    /// <c>System.Threading.Timer</c>) to run a 5-phase cleanup cycle every
    /// <see cref="CleanupIntervalSeconds"/> seconds <i>during</i> execution.
    /// </para>
    /// <para>
    /// <b>Why a dedicated thread?</b> The cleanup acquires the Python GIL, which can
    /// block for the duration of a running Python step. Using <c>Timer</c>
    /// (ThreadPool-based) caused .NET's hill-climbing algorithm to inject new pool
    /// threads each time the GIL-blocked callback was detected as "stuck", resulting
    /// in unbounded ThreadPool growth (8 000+ leaked threads on ARM/Linux).
    /// A dedicated <c>IsBackground = true</c> thread avoids ThreadPool entirely.
    /// </para>
    /// </summary>
    [Display("Python Memory Monitor",
        Group: "Python",
        Description: "Periodically drains the pythonnet finalizer queue and " +
                     "reclaims memory during test plan execution, preventing " +
                     "memory growth in long-running sessions.")]
    public class PythonMemoryMonitor : ComponentSettings<PythonMemoryMonitor>,
                                       ITestPlanRunMonitor
    {
        private static readonly TraceSource Log = OpenTap.Log.CreateSource("PythonMem");

        /// <summary>Dedicated background thread for periodic cleanup — avoids
        /// ThreadPool thread accumulation when GIL acquisition blocks.</summary>
        private Thread _cleanupThread;
        /// <summary>Signals the cleanup thread to stop.</summary>
        private readonly ManualResetEventSlim _stopSignal = new ManualResetEventSlim(false);

        private long _heapAtPlanStart;
        private int _cleanupCount;
        private int _peakThreadCount;

        /// <summary>
        /// Interval in seconds between periodic cleanup sweeps during test plan
        /// execution. Default 30 s keeps memory flat for ~10 s loop iterations.
        /// </summary>
        [Display("Cleanup Interval (s)",
            Description: "Seconds between periodic memory cleanup sweeps during " +
                         "test plan execution.",
            Order: 1)]
        public int CleanupIntervalSeconds { get; set; } = 30;

        /// <summary>Captures baseline and starts the periodic cleanup thread.</summary>
        public void EnterTestPlanRun(TestPlanRun planRun)
        {
            _heapAtPlanStart = GC.GetTotalMemory(forceFullCollection: false);
            _cleanupCount = 0;
            _peakThreadCount = 0;

            if (!PythonEngine.IsInitialized) return;

            // Cap ThreadPool growth — prevents the pool from expanding to thousands
            // of threads when GIL contention blocks pool threads in other codepaths.
            // Default .NET max is 32 767 which is way too high for a 4-core Pi.
            int maxWorker = Math.Max(Environment.ProcessorCount * 16, 64);
            int maxIO = Math.Max(Environment.ProcessorCount * 16, 64);
            ThreadPool.SetMaxThreads(maxWorker, maxIO);

            try
            {
                Log.Info($"[ENTER] Heap: {_heapAtPlanStart / (1024.0 * 1024.0):F1} MB  " +
                         $"| Cleanup every {CleanupIntervalSeconds}s " +
                         $"| Reflected objects: {Runtime.ReflectedObjectCount} " +
                         $"| Process threads: {Process.GetCurrentProcess().Threads.Count}");
            }
            catch (Exception ex)
            {
                Log.Warning($"[ENTER] Failed to read baseline: {ex.Message}");
            }

            // Start dedicated cleanup thread instead of System.Threading.Timer.
            _stopSignal.Reset();
            var intervalSec = Math.Max(CleanupIntervalSeconds, 5);
            _cleanupThread = new Thread(() => CleanupLoop(intervalSec))
            {
                Name = "PythonMemCleanup",
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal,
            };
            _cleanupThread.Start();
        }

        /// <summary>Stops the cleanup thread and runs one final cleanup.</summary>
        public void ExitTestPlanRun(TestPlanRun planRun)
        {
            _stopSignal.Set();
            if (_cleanupThread != null)
            {
                _cleanupThread.Join(TimeSpan.FromSeconds(60));
                _cleanupThread = null;
            }

            if (!PythonEngine.IsInitialized) return;

            RunCleanupCycle("EXIT-FINAL");

            Log.Info($"[EXIT] Peak process threads during plan: {_peakThreadCount}");
        }

        /// <summary>Dedicated thread loop — sleeps then cleans, exits when
        /// <see cref="_stopSignal"/> is set. Never touches the ThreadPool.</summary>
        private void CleanupLoop(int intervalSec)
        {
            if (_stopSignal.Wait(TimeSpan.FromSeconds(intervalSec)))
                return;

            while (true)
            {
                if (!PythonEngine.IsInitialized) break;

                _cleanupCount++;
                RunCleanupCycle($"PERIODIC-{_cleanupCount}");

                if (_stopSignal.Wait(TimeSpan.FromSeconds(intervalSec)))
                    break;
            }
        }

        /// <summary>
        /// Four-phase cleanup targeting both managed and native memory.
        /// <list type="number">
        ///   <item>Drain pythonnet Finalizer queue (deferred Py_DecRef from .NET finalizers)</item>
        ///   <item>Python gc.collect() × 3 generations + clear type cache</item>
        ///   <item>.NET GC.Collect (accelerates finalizer triggering)</item>
        ///   <item>Drain Finalizer queue again (picks up Phase 3 finalizers)</item>
        ///   <item>malloc_trim on Linux (return freed native memory to OS)</item>
        /// </list>
        /// <para>
        /// <b>Note:</b> Previous versions included an EvictAbandonedObjects phase
        /// to work around a phantom-reference leak in <c>InvokeCtor</c>. That root
        /// cause has been fixed — <c>InvokeCtor</c> now properly releases the
        /// <c>NewReference</c> after <c>__init__</c>, so <c>tp_dealloc</c> fires
        /// naturally and the dealloc/resurrection lifecycle manages wrapper lifetimes.
        /// </para>
        /// </summary>
        private void RunCleanupCycle(string tag)
        {
            try
            {
                var heapBefore = GC.GetTotalMemory(forceFullCollection: false);
                int reflectedBefore;
                int pyGcCollected = 0;

                // ── Phases 1-2: inside GIL ──────────────────────────────
                using (Py.GIL())
                {
                    reflectedBefore = Runtime.ReflectedObjectCount;

                    // Phase 1: Drain the Finalizer queue — objects finalized by .NET GC
                    // have deferred Py_DecRef calls queued.  Processing them now allows
                    // Python wrappers to reach rc=0 → tp_dealloc → reflectedObjects cleanup.
                    try { Finalizer.Instance.Collect(); }
                    catch (Exception ex) { Log.Debug($"[{tag}] Finalizer drain 1: {ex.Message}"); }

                    // Phase 2: Full Python GC sweep (all 3 generations) + clear type cache.
                    try
                    {
                        using var gc = PyModule.Import("gc");
                        using var sys = PyModule.Import("sys");

                        for (int gen = 0; gen < 3; gen++)
                        {
                            using var arg = new PyInt(gen);
                            using var r = gc.InvokeMethod("collect", arg);
                            if (r != null) pyGcCollected += r.As<int>();
                        }

                        try { sys.InvokeMethod("_clear_type_cache"); }
                        catch { /* not available on all builds */ }
                    }
                    catch (Exception ex) { Log.Debug($"[{tag}] Python gc.collect: {ex.Message}"); }
                }

                // ── Phase 3: .NET GC (outside GIL) ─────────────────────
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();

                // ── Phases 4-5: re-acquire GIL ──────────────────────────
                int reflectedAfter;
                using (Py.GIL())
                {
                    // Phase 4: Drain Finalizer queue again.
                    try { Finalizer.Instance.Collect(); }
                    catch (Exception ex) { Log.Debug($"[{tag}] Finalizer drain 2: {ex.Message}"); }

                    reflectedAfter = Runtime.ReflectedObjectCount;

                    // Phase 5: Return freed native memory to the OS (Linux only).
                    try
                    {
                        using var ctypes = PyModule.Import("ctypes");
                        using var libcName = new PyString("libc.so.6");
                        using var cdll = ctypes.GetAttr("CDLL");
                        using var libc = cdll.Invoke(libcName);
                        using var trimFn = libc.GetAttr("malloc_trim");
                        using var zero = new PyInt(0);
                        trimFn.Invoke(zero);
                    }
                    catch
                    {
                        // Not Linux/glibc — skip silently
                    }
                }

                var heapAfter = GC.GetTotalMemory(forceFullCollection: false);
                var deltaMb = (heapAfter - heapBefore) / (1024.0 * 1024.0);
                var totalDeltaMb = (heapAfter - _heapAtPlanStart) / (1024.0 * 1024.0);

                int processThreads = 0;
                try { processThreads = Process.GetCurrentProcess().Threads.Count; }
                catch { /* may fail on some platforms */ }
                if (processThreads > _peakThreadCount)
                    _peakThreadCount = processThreads;

                Log.Info($"[{tag}] PyGC: {pyGcCollected}. " +
                         $"Reflected: {reflectedBefore} → {reflectedAfter}. " +
                         $"Heap: {heapAfter / (1024.0 * 1024.0):F1} MB " +
                         $"(cycle: {deltaMb:+0.0;-0.0;0.0} MB, " +
                         $"total: {totalDeltaMb:+0.0;-0.0;0.0} MB) " +
                         $"| Threads: process={processThreads}, " +
                         $"peak={_peakThreadCount}");
            }
            catch (Exception ex)
            {
                Log.Warning($"[{tag}] Cleanup failed: {ex.Message}");
            }
        }
    }
}
