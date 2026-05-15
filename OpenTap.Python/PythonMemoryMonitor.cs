using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
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
        /// <summary>Last reflected count at which the type histogram was logged.</summary>
        private int _lastHistogramReflected;

        // ─── Live-plan tracking (shared across all instances) ──────────
        // We need to know which TestPlan instances are currently executing
        // so we can identify *abandoned* plans (and their entire step graph)
        // for cycle-break eviction.  Without this set, the abandonment
        // predicate `step.Parent == null` matches almost nothing — every
        // step in an abandoned plan still references its parent container,
        // and those containers reference up to the abandoned TestPlan.
        // The only way to identify the whole island is to walk Parent → root
        // and check whether the root TestPlan is in this live set.
        private static readonly object _livePlansLock = new object();
        private static readonly List<WeakReference> _liveTestPlans = new List<WeakReference>();
        /// <summary>Reflection accessor for <c>TestPlanRun.plan</c> (private field).</summary>
        private static readonly FieldInfo _testPlanRunPlanField =
            typeof(TestPlanRun).GetField("plan", BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>Adds <paramref name="plan"/> to the live-plan set.</summary>
        private static void RegisterLivePlan(TestPlan plan)
        {
            if (plan == null) return;
            lock (_livePlansLock)
            {
                _liveTestPlans.Add(new WeakReference(plan));
            }
        }

        /// <summary>Removes <paramref name="plan"/> from the live-plan set
        /// and prunes dead weak refs.</summary>
        private static void UnregisterLivePlan(TestPlan plan)
        {
            lock (_livePlansLock)
            {
                for (int i = _liveTestPlans.Count - 1; i >= 0; i--)
                {
                    var t = _liveTestPlans[i].Target;
                    if (t == null || ReferenceEquals(t, plan))
                        _liveTestPlans.RemoveAt(i);
                }
            }
        }

        /// <summary>Returns true if <paramref name="plan"/> is in the live set.</summary>
        private static bool IsPlanLive(TestPlan plan)
        {
            if (plan == null) return false;
            lock (_livePlansLock)
            {
                foreach (var w in _liveTestPlans)
                    if (ReferenceEquals(w.Target, plan)) return true;
            }
            return false;
        }

        /// <summary>
        /// Determines whether <paramref name="inst"/> belongs to a TestPlan
        /// that is no longer live — i.e. it is an orphaned step / dynamic
        /// member / etc. that should be evicted.
        /// <para>
        /// Logic: walk up the Parent chain until we find a <see cref="TestPlan"/>
        /// (the natural root of any step graph) or run out.  If we end up at a
        /// TestPlan that is NOT in the live set → abandoned.  If we end up at
        /// any non-TestPlan root, it's a free-floating object (also abandoned).
        /// </para>
        /// </summary>
        private static bool IsAbandoned(object inst)
        {
            // Direct TestPlan: abandoned iff not live.
            if (inst is TestPlan plan) return !IsPlanLive(plan);

            // Steps & dynamic test-plan-attached objects: walk Parent chain.
            if (inst is ITestStepParent node)
            {
                // Bound the walk to defend against accidental cycles.
                for (int i = 0; i < 64 && node != null; i++)
                {
                    if (node is TestPlan tp) return !IsPlanLive(tp);
                    var next = node.Parent;
                    if (ReferenceEquals(next, node)) break;
                    node = next;
                }
                // Walked to a null root that wasn't a TestPlan → orphan.
                return true;
            }

            // Not a step-graph object — leave alone (could be a global resource,
            // ComponentSettings, etc.).
            return false;
        }
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

            // Register the actual TestPlan instance (private field on TestPlanRun)
            // so cleanup cycles know which step graph is "live" and must NOT be
            // evicted.  Anything else is fair game.
            try
            {
                if (_testPlanRunPlanField != null)
                {
                    var plan = _testPlanRunPlanField.GetValue(planRun) as TestPlan;
                    RegisterLivePlan(plan);
                }
            }
            catch (Exception ex) { Log.Debug($"[ENTER] Live-plan register failed: {ex.Message}"); }

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

            // Unregister the live plan BEFORE the final cleanup so the just-
            // finished plan's step graph is treated as abandoned and evicted.
            try
            {
                if (_testPlanRunPlanField != null)
                {
                    var plan = _testPlanRunPlanField.GetValue(planRun) as TestPlan;
                    UnregisterLivePlan(plan);
                }
            }
            catch (Exception ex) { Log.Debug($"[EXIT] Live-plan unregister failed: {ex.Message}"); }

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
        /// Five-phase cleanup targeting both managed and native memory.
        /// <list type="number">
        ///   <item>Drain pythonnet Finalizer queue (deferred Py_DecRef from .NET finalizers)</item>
        ///   <item>Break Python ↔ .NET cycle for orphaned Python-derived steps
        ///   (RemoveAnchors + EvictAbandonedObjects)</item>
        ///   <item>Python gc.collect() × 3 generations + clear type cache</item>
        ///   <item>.NET GC.Collect (accelerates finalizer triggering)</item>
        ///   <item>Drain Finalizer queue again (picks up Phase 3 finalizers)</item>
        ///   <item>malloc_trim on Linux (return freed native memory to OS)</item>
        /// </list>
        /// <para>
        /// The Python ↔ .NET cycle break in Phase 2 is required because
        /// <see cref="PythonTypeDataWrapper"/> anchors a strong <see cref="PyObject"/>
        /// in a <see cref="ConditionalWeakTable{TKey,TValue}"/> to keep the Python
        /// wrapper's <c>__dict__</c> alive across <c>CreateInstance</c>.  That anchor
        /// keeps an INCREF on the Python wrapper, which in turn holds a strong
        /// GCHandle back to the .NET step, producing a self-sustaining cycle that
        /// neither <see cref="GC"/> nor Python's <c>gc</c> can break on its own.
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

                    // Phase 1b: Break the Python ↔ .NET reference cycle for orphaned
                    // Python-derived test steps (steps whose parent has been cleared,
                    // i.e. they have been removed from any test plan).
                    //
                    // Required because PythonTypeDataWrapper anchors a strong PyObject
                    // in a CWT to keep the Python wrapper's __dict__ alive across
                    // CreateInstance.  That anchor INCREFs the Python wrapper, and the
                    // wrapper holds a strong GCHandle back to the .NET step — a cycle
                    // that neither GC can break alone.  Dropping the anchor releases
                    // the INCREF so EvictAbandonedObjects can finish the job.
                    int anchorsReleased = 0;
                    try
                    {
                        anchorsReleased = PythonTypeDataWrapper.RemoveAnchors(IsAbandoned);
                    }
                    catch (Exception ex) { Log.Debug($"[{tag}] RemoveAnchors: {ex.Message}"); }

                    // Phase 1c: Evict the now-collectable Python wrappers that still
                    // sit in reflectedObjects.  Without anchor INCREFs, the wrapper
                    // refcount drops to zero, tp_dealloc runs, and the GCHandle on
                    // the .NET step is released.
                    int evicted = 0;
                    try
                    {
                        evicted = Runtime.EvictAbandonedObjects(IsAbandoned);
                    }
                    catch (Exception ex) { Log.Debug($"[{tag}] EvictAbandonedObjects: {ex.Message}"); }

                    if (anchorsReleased > 0 || evicted > 0)
                        Log.Debug($"[{tag}] Cycle break: anchors released={anchorsReleased}, evicted={evicted}");

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

                // Diagnostic: dump per-type histogram of reflected objects
                // when growth is observed.  Helps identify which .NET type
                // is accumulating across plan runs.
                if (reflectedAfter > _lastHistogramReflected + 50 || reflectedAfter > 1000)
                {
                    _lastHistogramReflected = reflectedAfter;
                    try
                    {
                        IReadOnlyList<(string TypeName, int Count, long TotalRc, int PythonDerivedCount, int ParentlessStepCount)> hist;
                        using (Py.GIL())
                        {
                            hist = Runtime.DiagnoseTypeHistogram(topN: 15);
                        }
                        var sb = new System.Text.StringBuilder();
                        sb.Append($"[{tag}] Top reflected types:");
                        foreach (var row in hist)
                        {
                            sb.Append($"\n   {row.Count,5}× {row.TypeName} (rcSum={row.TotalRc}, pyDerived={row.PythonDerivedCount}, parentless={row.ParentlessStepCount})");
                        }
                        Log.Info(sb.ToString());
                    }
                    catch (Exception ex) { Log.Debug($"[{tag}] Histogram: {ex.Message}"); }
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[{tag}] Cleanup failed: {ex.Message}");
            }
        }
    }
}
