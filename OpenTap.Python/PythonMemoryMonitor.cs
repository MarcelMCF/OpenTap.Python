using System;
using Python.Runtime;

namespace OpenTap.Python
{
    /// <summary>
    /// Monitors CLR reflected object growth in Python.Runtime and evicts collectable
    /// entries after each test plan execution. This prevents unbounded memory growth
    /// in long-running sessions where test plans with Python steps are repeatedly loaded.
    /// <para>
    /// The root cause: every <c>TestPlan.Load()</c> that resolves Python step types
    /// creates CLR object wrappers via <c>CLRObject.Create()</c>, which are added to
    /// a static <c>reflectedObjects</c> tracking set. These entries are normally only
    /// removed when Python's garbage collector frees the object (refcount → 0), but
    /// Python type caches keep references alive indefinitely, so entries accumulate
    /// (~300 per plan load, ~64 MB/run).
    /// </para>
    /// <para>
    /// This monitor calls <see cref="Runtime.EvictReflectedObjects"/> after each plan
    /// run, which removes entries with refcount ≤ 1 (only tracked, not in use) and
    /// frees their GCHandles, allowing .NET to reclaim the pinned objects.
    /// </para>
    /// </summary>
    [Display("Python Memory Monitor",
        Group: "Python",
        Description: "Evicts leaked CLR reflected objects from Python.Runtime's " +
                     "tracking set after each test plan execution, preventing " +
                     "memory growth in long-running sessions.")]
    public class PythonMemoryMonitor : ComponentSettings<PythonMemoryMonitor>,
                                       ITestPlanRunMonitor
    {
        private static readonly TraceSource Log = OpenTap.Log.CreateSource("PythonMem");

        private long _heapBefore;

        /// <summary>Captures baseline metrics before test plan execution.</summary>
        public void EnterTestPlanRun(TestPlanRun planRun)
        {
            _heapBefore = GC.GetTotalMemory(forceFullCollection: false);

            if (!PythonEngine.IsInitialized) return;

            try
            {
                Log.Info($"[ENTER] Heap: {_heapBefore / (1024.0 * 1024.0):F1} MB");
            }
            catch (Exception ex)
            {
                Log.Warning($"[ENTER] Failed to read baseline: {ex.Message}");
            }
        }

        /// <summary>Evicts collectable CLR reflected objects after test plan execution.</summary>
        public void ExitTestPlanRun(TestPlanRun planRun)
        {
            if (!PythonEngine.IsInitialized) return;

            try
            {
                EvictResult result;
                using (Py.GIL())
                {
                    result = Runtime.EvictReflectedObjects();
                }

                var heapAfter = GC.GetTotalMemory(forceFullCollection: false);
                var deltaMb = (heapAfter - _heapBefore) / (1024.0 * 1024.0);

                Log.Info($"[EXIT] Evicted {result.TotalEvicted}/{result.TotalBefore}: " +
                         $"rc1={result.EvictedRc1}, zombies={result.EvictedZombies}, " +
                         $"invalid={result.EvictedInvalid}, alive={result.Alive}. " +
                         $"Reflected: {result.TotalBefore} → {result.TotalAfter}. " +
                         $"Heap: {heapAfter / (1024.0 * 1024.0):F1} MB " +
                         $"(delta: {deltaMb:+0.0;-0.0;0.0} MB)");
            }
            catch (Exception ex)
            {
                Log.Warning($"[EXIT] Cleanup failed: {ex.Message}");
            }
        }
    }
}
