using System;
using System.Diagnostics;
using System.Threading;
using Python.Runtime;

namespace OpenTap.Python
{
    /// <summary>
    /// Periodically evicts CLR reflected objects and reclaims memory during
    /// long-running (potentially infinite-loop) test plan executions.
    /// <para>
    /// <b>Root cause:</b> <c>ITestPlanRunMonitor.ExitTestPlanRun</c> never fires
    /// for infinite-loop plans ("Main Loop" patterns), so cleanup that only ran at
    /// plan completion would never execute. This monitor uses a dedicated background
    /// thread (NOT a <c>System.Threading.Timer</c>) to run the full 6-phase cleanup
    /// every <see cref="CleanupIntervalSeconds"/> seconds <i>during</i> execution.
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
        Description: "Periodically evicts leaked CLR reflected objects and " +
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
            ThreadPool.GetMinThreads(out int minW, out int minIO);
            ThreadPool.GetAvailableThreads(out int curAvailW, out int curAvailIO);
            int curBusyW = maxWorker - curAvailW;
            Log.Info($"[ENTER] ThreadPool capped: max={maxWorker}/{maxIO}, " +
                     $"min={minW}/{minIO}, busy={curBusyW}");

            try
            {
                Log.Info($"[ENTER] Heap: {_heapAtPlanStart / (1024.0 * 1024.0):F1} MB  " +
                         $"| Cleanup every {CleanupIntervalSeconds}s " +
                         $"| Process threads: {Process.GetCurrentProcess().Threads.Count}");
            }
            catch (Exception ex)
            {
                Log.Warning($"[ENTER] Failed to read baseline: {ex.Message}");
            }

            // Start dedicated cleanup thread instead of System.Threading.Timer.
            // Timer callbacks run on ThreadPool — blocking on GIL causes the pool to
            // inject new threads that are never retired, leaking ~3-4 threads/iteration.
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
            // Signal the cleanup thread to stop and wait up to 60 s for it to exit.
            _stopSignal.Set();
            if (_cleanupThread != null)
            {
                _cleanupThread.Join(TimeSpan.FromSeconds(60));
                _cleanupThread = null;
            }

            if (!PythonEngine.IsInitialized) return;

            // One final cleanup pass.
            RunCleanupCycle("EXIT-FINAL");

            Log.Info($"[EXIT] Peak process threads during plan: {_peakThreadCount}");
        }

        /// <summary>Dedicated thread loop — sleeps then cleans, exits when
        /// <see cref="_stopSignal"/> is set. Never touches the ThreadPool.</summary>
        private void CleanupLoop(int intervalSec)
        {
            // Wait one full interval before first tick.
            if (_stopSignal.Wait(TimeSpan.FromSeconds(intervalSec)))
                return; // Stop signalled during initial wait.

            while (true)
            {
                if (!PythonEngine.IsInitialized) break;

                _cleanupCount++;
                RunCleanupCycle($"PERIODIC-{_cleanupCount}");

                // Wait for next interval (or early termination).
                if (_stopSignal.Wait(TimeSpan.FromSeconds(intervalSec)))
                    break;
            }
        }

        /// <summary>
        /// Six-phase cleanup targeting both managed and native memory.
        /// <list type="number">
        ///   <item>Evict CLR reflected objects (free GCHandles, Py_DecRef phantom refs; __pyobj__ left intact)</item>
        ///   <item>Drain pythonnet Finalizer queue</item>
        ///   <item>Python gc.collect() × 3 generations + clear type cache</item>
        ///   <item>.NET GC.Collect (safe: evicted PythonDerived objects retain Strong GCHandles)</item>
        ///   <item>Drain Finalizer queue again (picks up .NET GC'd items)</item>
        ///   <item>malloc_trim on Linux (return freed native memory to OS)</item>
        /// </list>
        /// </summary>
        private void RunCleanupCycle(string tag)
        {
            try
            {
                var heapBefore = GC.GetTotalMemory(forceFullCollection: false);
                EvictResult result;
                int pyGcCollected = 0;

                // ── Phases 1-3: inside GIL ──────────────────────────────
                // Acquiring the GIL blocks until any running Python step releases it.
                // This is safe — the step finishes its current Python C API call, we
                // run cleanup, then the step (or next step) re-acquires the GIL.
                using (Py.GIL())
                {
                    // Phase 1: Aggressive eviction — release phantom references for
                    // non-baseline objects via Py_DecRef (rc>1), free GCHandles for rc≤1.
                    // PythonDerived objects keep their __pyobj__ intact so they remain
                    // usable if the test plan is reused across multiple runs.
                    result = Runtime.EvictReflectedObjects(maxRefcount: long.MaxValue);

                    // Phase 2: Drain the Finalizer queue — objects finalized by .NET GC
                    // have deferred Py_DecRef calls queued. Process them now.
                    try { Finalizer.Instance.Collect(); }
                    catch (Exception ex) { Log.Debug($"[{tag}] Finalizer drain 1: {ex.Message}"); }

                    // Phase 3: Full Python GC sweep (all 3 generations) + clear type cache.
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

                // ── Phase 4: .NET GC (outside GIL) ─────────────────────
                // Safe because evicted PythonDerived objects with rc>1 still have
                // Strong GCHandles (tp_dealloc doesn't fire when rc stays ≥ 1),
                // so they won't be collected. Objects with rc≤1 had their GCHandle
                // freed in Pass 2a and can be collected — their finalizers handle
                // deferred cleanup via the Finalizer queue (Phase 5).
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();

                // ── Phases 5-6: re-acquire GIL ──────────────────────────
                using (Py.GIL())
                {
                    // Phase 5: Drain Finalizer queue again — .NET GC may have finalized
                    // more PyObject wrappers whose Py_DecRef was deferred.
                    try { Finalizer.Instance.Collect(); }
                    catch (Exception ex) { Log.Debug($"[{tag}] Finalizer drain 2: {ex.Message}"); }

                    // Phase 6: Return freed native memory to the OS (Linux only).
                    // malloc_trim(0) forces glibc to release arena pages back to the OS.
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

                // ── Thread diagnostics ──────────────────────────────────
                ThreadPool.GetAvailableThreads(out int availW, out int availIO);
                ThreadPool.GetMaxThreads(out int maxW, out int maxIO);
                int busyW = maxW - availW;
                int busyIO = maxIO - availIO;
                int processThreads = 0;
                try { processThreads = Process.GetCurrentProcess().Threads.Count; }
                catch { /* may fail on some platforms */ }
                if (processThreads > _peakThreadCount)
                    _peakThreadCount = processThreads;

                Log.Info($"[{tag}] Evicted {result.TotalEvicted}/{result.TotalBefore}: " +
                         $"rc1={result.EvictedRc1}, alive={result.EvictedAlive}, " +
                         $"zombies={result.EvictedZombies}, invalid={result.EvictedInvalid}, " +
                         $"remaining={result.Alive}. " +
                         $"PyGC: {pyGcCollected}. " +
                         $"Reflected: {result.TotalBefore} → {result.TotalAfter}. " +
                         $"Heap: {heapAfter / (1024.0 * 1024.0):F1} MB " +
                         $"(cycle: {deltaMb:+0.0;-0.0;0.0} MB, " +
                         $"total: {totalDeltaMb:+0.0;-0.0;0.0} MB) " +
                         $"| Threads: process={processThreads}, " +
                         $"pool(busy={busyW}w/{busyIO}io, max={maxW}), " +
                         $"peak={_peakThreadCount}");
            }
            catch (Exception ex)
            {
                Log.Warning($"[{tag}] Cleanup failed: {ex.Message}");
            }
        }
    }
}
