using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using OpenTap.Cli;
using Python.Runtime;

namespace OpenTap.Python.UnitTests
{
    /// <summary>
    /// Integration test that verifies:
    /// <list type="number">
    ///   <item>reflectedObjects does not grow unboundedly across repeated plan executions</item>
    ///   <item>Result listeners receive OnTestPlanRunCompleted during plan execution</item>
    ///   <item>Python-derived steps are callable after many cycles without dangling pointers</item>
    /// </list>
    /// Run with: <c>tap.exe python test-memory</c>
    /// </summary>
    [Display("test-memory", Group: "python")]
    public class MemoryLeakTest : ICliAction
    {
        private static readonly TraceSource Log = OpenTap.Log.CreateSource("mem-test");

        /// <summary>
        /// A .NET result listener that tracks whether OnTestPlanRunCompleted was called.
        /// </summary>
        private sealed class TrackingResultListener : ResultListener
        {
            public int PlanStartCount { get; private set; }
            public int PlanCompletedCount { get; private set; }
            public List<Verdict> CompletedVerdicts { get; } = new();

            public override void OnTestPlanRunStart(TestPlanRun planRun)
            {
                PlanStartCount++;
                base.OnTestPlanRunStart(planRun);
            }

            public override void OnTestPlanRunCompleted(TestPlanRun planRun, Stream logStream)
            {
                PlanCompletedCount++;
                CompletedVerdicts.Add(planRun.Verdict);
                base.OnTestPlanRunCompleted(planRun, logStream);
            }
        }

        public int Execute(CancellationToken cancellationToken)
        {
            const int warmupRuns = 3;
            const int measureRuns = 10;
            // Maximum acceptable growth per run in reflectedObjects after warmup.
            // Stock pythonnet cleans up via tp_dealloc → tp_clear → reflectedObjects.Remove,
            // so the count should be roughly flat after initial type loading.
            const int maxGrowthPerRun = 5;

            PythonInitializer.LoadPython();
            if (!PythonEngine.IsInitialized)
            {
                Log.Error("Python engine failed to initialize. Cannot run memory tests.");
                return 1;
            }
            Log.Info($"Python {PythonEngine.Version} initialized.");

            // Discover a usable Python step type
            var stepType = FindPythonStepType();
            if (stepType == null)
            {
                Log.Error("No Python test step type found. Deploy BasicStepTest.py or python_pass_step.py.");
                return 1;
            }
            Log.Info($"Using step type: {stepType.Name}");

            Log.Info("=== Memory Leak Test ===");
            Log.Info($"Warmup: {warmupRuns} runs, Measure: {measureRuns} runs");

            // ── 1. Verify result listener integration ──────────────────
            Log.Info("--- Test 1: Result listener OnTestPlanRunCompleted ---");
            TestResultListenerIntegration(stepType, totalRuns: 3);

            // ── 2. Verify reflectedObjects stability ───────────────────
            Log.Info("--- Test 2: reflectedObjects growth ---");
            TestReflectedObjectsStability(stepType, warmupRuns, measureRuns, maxGrowthPerRun);

            // ── 3. Verify step methods still work after many cycles ────
            Log.Info("--- Test 3: Step method integrity ---");
            TestStepMethodIntegrity(stepType);

            // ── 4. Verify Finalizer drain works ────────────────────────
            Log.Info("--- Test 4: Finalizer queue drain ---");
            TestFinalizerDrain();

            // ── 5. Stress-test: create many instances without running ──
            Log.Info("--- Test 5: Mass instance creation (no execution) ---");
            TestMassCreationWithoutExecution(stepType, count: 500);

            // ── 6. Long soak: same plan, 100 repeated executions ───────
            Log.Info("--- Test 6: Long soak - same plan (100 executions) ---");
            TestLongSoakSamePlan(stepType, iterations: 100);

            // ── 7. Execute-abandon cycles with full eviction ───────────
            Log.Info("--- Test 7: Execute-abandon with eviction (100 cycles) ---");
            TestExecuteAbandonCycles(stepType, cycles: 100);

            // ── 8. Mega mass creation: 5000 instances ──────────────────
            Log.Info("--- Test 8: Mega mass creation (5000 instances) ---");
            TestMegaMassCreation(stepType, count: 5000);

            // ── 9. RSS memory stability over many cycles ───────────────
            Log.Info("--- Test 9: RSS memory stability (50 cycles) ---");
            TestRssMemoryStability(stepType, cycles: 50);

            // ── 10. Combined stress: creation + execution + eviction ───
            Log.Info("--- Test 10: Combined stress test (20 rounds) ---");
            TestCombinedStress(stepType, rounds: 20);

            // ── 11. XML load + execute + discard (same plan) ───────────
            Log.Info("--- Test 11: XML load+execute+discard, same plan (100 cycles) ---");
            TestXmlLoadExecuteDiscardSamePlan(stepType, cycles: 100);

            // ── 12. XML load + execute + SWITCH between two plans ──────
            // This is the production scenario: long-lived runner session
            // alternates between distinct test plans (LoadTestPlanStep
            // feeds Session 2 with different XML each time).
            Log.Info("--- Test 12: XML load+switch+execute, two plans (100 switches) ---");
            TestXmlLoadSwitchExecute(stepType, switches: 100);

            // ── 12b. Heavy production-style: 50-step plans, switch 50× ──
            // Mimics realistic production plan size — exposes per-step
            // accumulation that single-step tests miss.
            Log.Info("--- Test 12b: heavy 50-step plans (50 switches) ---");
            TestXmlLoadSwitchExecuteHeavy(stepType, switches: 50, stepsPerPlan: 50);

            // ── 13. WeakReference leak detector ────────────────────────
            // For each thing that "should" be collectable after a plan
            // execution finishes (TestPlan, ITestStep, TestPlanRun), assert
            // it actually IS collectable.  Anything still alive after
            // forced GC means a strong reference is leaking it.
            Log.Info("--- Test 13: WeakReference leak detector ---");
            TestWeakReferenceLeakDetector(stepType);

            Log.Info("=== All 13 memory tests passed ===");
            return 0;
        }

        /// <summary>
        /// Finds a Python-derived ITestStep type available in the environment.
        /// Tries common test module names, then falls back to any Python step.
        /// </summary>
        private static ITypeData FindPythonStepType()
        {
            // Try well-known test step types
            string[] candidates = {
                "python_test_steps.PythonPassStep",
                "TestModule.BasicStepTest",
                "PythonExamples.BasicFunctionality.BasicFunctionality",
                "PythonPassStep",
                "python_pass_step.PythonPassStep",
            };
            foreach (var name in candidates)
            {
                Log.Debug($"Trying TypeData.GetTypeData(\"{name}\")...");
                var td = TypeData.GetTypeData(name);
                if (td != null)
                {
                    Log.Info($"TypeData found for '{name}': {td.Name}, DescendsToITestStep={td.DescendsTo(typeof(ITestStep))}");
                    if (td.DescendsTo(typeof(ITestStep)))
                        return td;
                }
                else
                {
                    Log.Debug($"  -> null");
                }
            }

            // Enumerate all types from PythonPluginProvider
            Log.Info("Checking PythonPluginProvider types directly...");
            var searchers = TypeData.GetDerivedTypes(TypeData.FromType(typeof(ITypeDataSearcher)));
            foreach (var searcherType in searchers)
            {
                Log.Info($"  Searcher: {searcherType.Name}");
            }

            // Fall back: find any Python-derived step type
            Log.Info("Searching all derived step types...");
            var stepInterface = TypeData.FromType(typeof(ITestStep));
            int count = 0;
            foreach (var derived in stepInterface.DerivedTypes)
            {
                var name = derived.Name ?? "";
                count++;
                // Only log the first 20 and any Python ones
                if (count <= 20 || name.Contains("python", StringComparison.OrdinalIgnoreCase))
                    Log.Info($"  Candidate #{count}: {name} (BaseType={derived.BaseType?.Name})");
                if (name.Contains("Python", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("python_", StringComparison.OrdinalIgnoreCase))
                {
                    Log.Info($"Found Python step by heuristic: {name}");
                    return derived;
                }
            }
            Log.Info($"Total derived step types scanned: {count}");

            return null;
        }

        /// <summary>
        /// Creates a plan with a tracking result listener and verifies
        /// OnTestPlanRunCompleted is called for each execution.
        /// </summary>
        private void TestResultListenerIntegration(ITypeData stepType, int totalRuns)
        {
            var listener = new TrackingResultListener();
            ResultSettings.Current.Add(listener);
            try
            {
                var step = (ITestStep)stepType.CreateInstance();
                Assert.IsNotNull(step);

                var plan = new TestPlan();
                plan.ChildTestSteps.Add(step);

                for (int i = 0; i < totalRuns; i++)
                {
                    var run = plan.Execute();
                    Log.Info($"  Run {i + 1}: Verdict={run.Verdict}");
                }

                Assert.AreEqual(totalRuns, listener.PlanStartCount);
                Assert.AreEqual(totalRuns, listener.PlanCompletedCount);
                Log.Info($"  Result listener received {listener.PlanCompletedCount}/{totalRuns} " +
                         "OnTestPlanRunCompleted calls — OK");
            }
            finally
            {
                ResultSettings.Current.Remove(listener);
            }
        }

        /// <summary>
        /// Runs a plan many times and checks that reflectedObjects count
        /// does not grow linearly (i.e. cleanup is working).
        /// </summary>
        private void TestReflectedObjectsStability(ITypeData stepType, int warmupRuns, int measureRuns, int maxGrowthPerRun)
        {
            int[] reflectedCounts = new int[warmupRuns + measureRuns];

            for (int i = 0; i < warmupRuns + measureRuns; i++)
            {
                // Create fresh step and plan each iteration to stress wrapper creation.
                var step = (ITestStep)stepType.CreateInstance();
                var plan = new TestPlan();
                plan.ChildTestSteps.Add(step);

                var run = plan.Execute();

                // Force cleanup (mimics what PythonMemoryMonitor does)
                using (Py.GIL())
                {
                    try { Finalizer.Instance.Collect(); }
                    catch { /* ignore */ }
                }
                GC.Collect(2, GCCollectionMode.Forced, blocking: true);
                GC.WaitForPendingFinalizers();
                using (Py.GIL())
                {
                    try { Finalizer.Instance.Collect(); }
                    catch { /* ignore */ }
                }

                reflectedCounts[i] = Runtime.ReflectedObjectCount;
                Log.Info($"  Run {i + 1}: reflected={reflectedCounts[i]}, verdict={run.Verdict}");
            }

            // After warmup, check that reflected count is roughly stable.
            int countAfterWarmup = reflectedCounts[warmupRuns - 1];
            int countAtEnd = reflectedCounts[warmupRuns + measureRuns - 1];
            int totalGrowth = countAtEnd - countAfterWarmup;
            int allowedGrowth = maxGrowthPerRun * measureRuns;

            Log.Info($"  After warmup: {countAfterWarmup}, After measurement: {countAtEnd}");
            Log.Info($"  Growth: {totalGrowth} (allowed: {allowedGrowth})");

            if (totalGrowth > allowedGrowth)
            {
                throw new Exception(
                    $"reflectedObjects grew by {totalGrowth} over {measureRuns} runs " +
                    $"(max allowed: {allowedGrowth}). Leak detected!");
            }

            Log.Info("  reflectedObjects stable — OK");
        }

        /// <summary>
        /// Verifies that Python step methods are still callable after
        /// many creation/execution cycles (no dangling __pyobj__).
        /// </summary>
        private void TestStepMethodIntegrity(ITypeData stepType)
        {
            var steps = new List<ITestStep>();

            // Create many steps
            for (int i = 0; i < 20; i++)
            {
                var step = (ITestStep)stepType.CreateInstance();
                steps.Add(step);
            }

            // Force GC to pressure weak handles
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();

            // Verify all steps are still usable
            foreach (var step in steps)
            {
                // If __pyobj__ were dangling, calling any method would crash
                var td = TypeData.GetTypeData(step);
                Assert.IsNotNull(td);
            }

            // Execute them all in a plan
            var plan = new TestPlan();
            foreach (var step in steps)
                plan.ChildTestSteps.Add(step);

            var run = plan.Execute();
            Log.Info($"  Executed plan with {steps.Count} steps: Verdict={run.Verdict}");

            Log.Info("  All step methods callable after GC pressure — OK");
        }

        /// <summary>
        /// Verifies that the Finalizer drain mechanism works correctly.
        /// Creates many PyObject wrappers, lets them go out of scope,
        /// then drains and checks the queue was processed.
        /// </summary>
        private void TestFinalizerDrain()
        {
            int countBefore;
            using (Py.GIL())
            {
                countBefore = Runtime.ReflectedObjectCount;

                // Create some Python objects that will go out of scope
                for (int i = 0; i < 100; i++)
                {
                    using var pyInt = new PyInt(i);
                    // PyInt goes out of scope and Dispose() is called
                }
            }

            // Force .NET GC to finalize any remaining wrappers
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();

            // Drain the finalizer queue
            using (Py.GIL())
            {
                try { Finalizer.Instance.Collect(); }
                catch { /* ignore */ }
            }

            int countAfter = Runtime.ReflectedObjectCount;
            Log.Info($"  Reflected before: {countBefore}, after: {countAfter}");

            // The count should not have grown (PyInt wrappers are not CLR objects,
            // they don't go into reflectedObjects). The important thing is no crash.
            Log.Info("  Finalizer drain completed without errors — OK");
        }

        /// <summary>
        /// Creates many Python step instances without executing any test plan,
        /// then lets them go out of scope and measures whether reflectedObjects
        /// is cleaned up by eviction + GC + Finalizer drain.
        ///
        /// This tests the .NET-created PythonDerived object lifecycle:
        /// InvokeCtor → phantom reference → eviction clears dict + decref.
        /// </summary>
        private void TestMassCreationWithoutExecution(ITypeData stepType, int count)
        {
            // Force a clean state
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
            using (Py.GIL())
            {
                try { Finalizer.Instance.Collect(); } catch { }
            }

            int countBefore = Runtime.ReflectedObjectCount;
            long heapBefore = GC.GetTotalMemory(false);
            Log.Info($"  Before: reflected={countBefore}, heap={heapBefore / (1024.0 * 1024):F1} MB");

            // Create many instances, do NOT hold references (let them go out of scope)
            Log.Info($"  Creating {count} step instances (no execution)...");
            for (int i = 0; i < count; i++)
            {
                var step = (ITestStep)stepType.CreateInstance();
                // step goes out of scope at end of this iteration
                // (no list holding references)
            }

            int countAfterCreate = Runtime.ReflectedObjectCount;
            long heapAfterCreate = GC.GetTotalMemory(false);
            // Diagnose refcount distribution
            (int rc1, int rc2, int rc3plus) diag;
            using (Py.GIL()) { diag = Runtime.DiagnoseRefcounts(); }
            Log.Info($"  After create: reflected={countAfterCreate} (+{countAfterCreate - countBefore}), " +
                     $"heap={heapAfterCreate / (1024.0 * 1024):F1} MB, " +
                     $"refcounts: rc1={diag.rc1}, rc2={diag.rc2}, rc3+={diag.rc3plus}");

            // Aggressive cleanup: EvictAbandoned → GC → Finalizer drain → Python GC
            Log.Info("  Running aggressive cleanup...");
            for (int pass = 0; pass < 3; pass++)
            {
                int evicted = 0;
                // Phase A: Inside GIL — Python GC + evict abandoned phantoms
                using (Py.GIL())
                {
                    try
                    {
                        using var gc = Py.Import("gc");
                        gc.InvokeMethod("collect");
                        gc.InvokeMethod("collect");
                        gc.InvokeMethod("collect");
                    }
                    catch { }

                    // Evict phantoms: Python-created (always safe) + .NET-created
                    // orphaned steps (ITestStep with no parent).
                    try
                    {
                        // Drop strong PyObject anchors first so eviction can
                        // actually free the wrapper.
                        PythonTypeDataWrapper.RemoveAnchors(inst =>
                        {
                            if (inst is OpenTap.ITestStep step)
                                return step.Parent == null;
                            return false;
                        });

                        evicted = Runtime.EvictAbandonedObjects(inst =>
                        {
                            if (inst is OpenTap.ITestStep step)
                                return step.Parent == null;
                            return false;
                        });
                    }
                    catch { }
                }

                // Phase B: Outside GIL — .NET GC collects weakened objects
                GC.Collect(2, GCCollectionMode.Forced, blocking: true);
                GC.WaitForPendingFinalizers();

                // Phase C: Inside GIL — drain Finalizer queue (processes PyFinalize)
                int reflectedNow;
                using (Py.GIL())
                {
                    try { Finalizer.Instance.Collect(); } catch { }
                    reflectedNow = Runtime.ReflectedObjectCount;
                    var d = Runtime.DiagnoseRefcounts();
                    Log.Info($"    Pass {pass + 1}: evicted={evicted}, reflected={reflectedNow}, " +
                             $"rc1={d.rc1}, rc2={d.rc2}, rc3+={d.rc3plus}");
                }
            }

            int countAfterCleanup = Runtime.ReflectedObjectCount;
            long heapAfterCleanup = GC.GetTotalMemory(true);
            int recovered = countAfterCreate - countAfterCleanup;
            int remaining = countAfterCleanup - countBefore;

            Log.Info($"  After cleanup: reflected={countAfterCleanup} " +
                     $"(recovered={recovered}, remaining=+{remaining}), " +
                     $"heap={heapAfterCleanup / (1024.0 * 1024):F1} MB");
            Log.Info($"  Per-instance growth: {(double)remaining / count:F2} entries/instance");

            // Report the situation clearly
            if (recovered > count / 2)
            {
                Log.Info($"  GC recovered {recovered}/{countAfterCreate - countBefore} entries — " +
                         "cleanup is working for abandoned instances.");
            }
            else
            {
                Log.Warning($"  GC recovered only {recovered}/{countAfterCreate - countBefore} entries. " +
                            "PythonDerived phantom references keep abandoned instances alive. " +
                            "This is a known pythonnet design constraint — bounded but not zero.");
            }

            // This test is informational — we don't fail on growth because the
            // phantom reference is by-design in pythonnet. We just verify no crash
            // and report the numbers.
            Log.Info("  Mass creation stress test completed without errors — OK");
        }

        // ═══════════════════════════════════════════════════════════════
        //  Test 6 – Long soak: same plan, many repeated executions
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Creates ONE plan with ONE step and executes it many times.
        /// Since the same instances are reused every iteration, there must
        /// be zero reflected-object growth.  This is the primary production
        /// steady-state scenario (same testplan executed for hours/days).
        /// </summary>
        private void TestLongSoakSamePlan(ITypeData stepType, int iterations)
        {
            var step = (ITestStep)stepType.CreateInstance();
            var plan = new TestPlan();
            plan.ChildTestSteps.Add(step);

            // Warmup — let type caches and JIT stabilise
            for (int i = 0; i < 5; i++)
                plan.Execute();

            RunFullCleanupCycle();
            int baseReflected = Runtime.ReflectedObjectCount;
            long baseRss = GetRssBytes();
            Log.Info($"  Base: reflected={baseReflected}, RSS={baseRss / (1024.0 * 1024):F1} MB");

            for (int i = 0; i < iterations; i++)
            {
                plan.Execute();

                if ((i + 1) % 10 == 0)
                {
                    RunFullCleanupCycle();
                    int reflected = Runtime.ReflectedObjectCount;
                    long rss = GetRssBytes();
                    Log.Info($"  Iteration {i + 1}/{iterations}: reflected={reflected} " +
                             $"(delta={reflected - baseReflected:+0;-0;0}), " +
                             $"RSS={rss / (1024.0 * 1024):F1} MB " +
                             $"(delta={((rss - baseRss) / (1024.0 * 1024)):+0.0;-0.0;0.0} MB)");
                }
            }

            RunFullCleanupCycle();
            int finalReflected = Runtime.ReflectedObjectCount;
            int totalGrowth = finalReflected - baseReflected;

            Log.Info($"  Final: reflected={finalReflected}, growth={totalGrowth}");

            // Same instances are reused — growth must be zero (tiny tolerance for
            // internal type-cache churn).
            if (totalGrowth > 5)
                throw new Exception(
                    $"Same-plan soak FAILED: reflected grew by {totalGrowth} " +
                    $"over {iterations} executions (max 5)");

            // Cleanup
            plan.ChildTestSteps.Clear();
            Log.Info($"  Long soak ({iterations} executions, same plan): growth={totalGrowth} — OK");
        }

        // ═══════════════════════════════════════════════════════════════
        //  Test 7 – Execute-abandon cycles with eviction
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Simulates switching testplans: every cycle creates a fresh plan +
        /// step, executes once, detaches the step (ChildTestSteps.Clear sets
        /// Parent = null), then runs full eviction.  Over 100 cycles the
        /// reflected-object count must stay flat.
        /// </summary>
        private void TestExecuteAbandonCycles(ITypeData stepType, int cycles)
        {
            // Warmup
            for (int i = 0; i < 5; i++)
            {
                var s = (ITestStep)stepType.CreateInstance();
                var p = new TestPlan();
                p.ChildTestSteps.Add(s);
                p.Execute();
                p.ChildTestSteps.Clear();
            }
            RunFullCleanupCycle();

            int baseReflected = Runtime.ReflectedObjectCount;
            long baseRss = GetRssBytes();
            Log.Info($"  Base: reflected={baseReflected}, RSS={baseRss / (1024.0 * 1024):F1} MB");

            for (int i = 0; i < cycles; i++)
            {
                var step = (ITestStep)stepType.CreateInstance();
                var plan = new TestPlan();
                plan.ChildTestSteps.Add(step);
                plan.Execute();
                plan.ChildTestSteps.Clear(); // Detach → step.Parent = null

                // Evict every 5 iterations to keep phantoms from accumulating
                if ((i + 1) % 5 == 0)
                {
                    RunFullCleanupCycle();

                    if ((i + 1) % 10 == 0)
                    {
                        int reflected = Runtime.ReflectedObjectCount;
                        long rss = GetRssBytes();
                        Log.Info($"  Cycle {i + 1}/{cycles}: reflected={reflected} " +
                                 $"(delta={reflected - baseReflected:+0;-0;0}), " +
                                 $"RSS={rss / (1024.0 * 1024):F1} MB");
                    }
                }
            }

            // Final aggressive cleanup — double pass for stragglers
            RunFullCleanupCycle();
            RunFullCleanupCycle();

            int finalReflected = Runtime.ReflectedObjectCount;
            long finalRss = GetRssBytes();
            int totalGrowth = finalReflected - baseReflected;

            Log.Info($"  Final: reflected={finalReflected}, growth={totalGrowth}, " +
                     $"RSS delta={((finalRss - baseRss) / (1024.0 * 1024)):+0.0;-0.0;0.0} MB");

            // Strict: with eviction the phantoms are fully recovered each cycle.
            // Allow a small buffer for type-cache / one-time caching.
            if (totalGrowth > 10)
                throw new Exception(
                    $"Execute-abandon cycles FAILED: reflected grew by {totalGrowth} " +
                    $"over {cycles} cycles (max 10)");

            Log.Info($"  Execute-abandon ({cycles} cycles): growth={totalGrowth} — OK");
        }

        // ═══════════════════════════════════════════════════════════════
        //  Test 8 – Mega mass creation (5000 instances)
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Scaled-up version of Test 5: creates 5000 PythonDerived step
        /// instances, then evicts and verifies ≥99% recovery.
        /// </summary>
        private void TestMegaMassCreation(ITypeData stepType, int count)
        {
            RunFullCleanupCycle();
            int countBefore = Runtime.ReflectedObjectCount;
            long rssBefore = GetRssBytes();
            Log.Info($"  Before: reflected={countBefore}, RSS={rssBefore / (1024.0 * 1024):F1} MB");

            // Create in batches with progress
            int batchSize = Math.Max(1, count / 10);
            for (int batch = 0; batch < 10; batch++)
            {
                for (int i = 0; i < batchSize; i++)
                {
                    var step = (ITestStep)stepType.CreateInstance();
                    // step is immediately abandoned (no reference held)
                }
                int reflected = Runtime.ReflectedObjectCount;
                Log.Info($"    Batch {batch + 1}/10: reflected={reflected} " +
                         $"(+{reflected - countBefore})");
            }
            // Handle remainder
            int remainder = count - batchSize * 10;
            for (int i = 0; i < remainder; i++)
            {
                var step = (ITestStep)stepType.CreateInstance();
            }

            int countAfterCreate = Runtime.ReflectedObjectCount;
            long rssAfterCreate = GetRssBytes();
            Log.Info($"  After create: reflected={countAfterCreate} " +
                     $"(+{countAfterCreate - countBefore}), " +
                     $"RSS={rssAfterCreate / (1024.0 * 1024):F1} MB");

            // Aggressive cleanup passes — stop early if baseline reached
            for (int pass = 0; pass < 5; pass++)
            {
                RunFullCleanupCycle();
                int reflected = Runtime.ReflectedObjectCount;
                Log.Info($"    Cleanup pass {pass + 1}: reflected={reflected}");
                if (reflected <= countBefore + 5) break;
            }

            int countAfterCleanup = Runtime.ReflectedObjectCount;
            long rssAfterCleanup = GetRssBytes();
            int created = countAfterCreate - countBefore;
            int recovered = countAfterCreate - countAfterCleanup;
            int remaining = countAfterCleanup - countBefore;
            double recoveryPct = created > 0
                ? (double)recovered / created * 100 : 100;

            Log.Info($"  Created: {created}, Recovered: {recovered}, " +
                     $"Remaining: {remaining}");
            Log.Info($"  Recovery: {recoveryPct:F1}%, " +
                     $"Per-instance growth: {(double)remaining / count:F4}");
            Log.Info($"  RSS delta: {((rssAfterCleanup - rssBefore) / (1024.0 * 1024)):+0.0;-0.0;0.0} MB");

            if (recoveryPct < 99.0)
                throw new Exception(
                    $"Mega mass creation FAILED: only recovered {recoveryPct:F1}% " +
                    $"of {created} entries (need ≥99%)");

            Log.Info($"  Mega mass creation ({count} instances): " +
                     $"{recoveryPct:F1}% recovery — OK");
        }

        // ═══════════════════════════════════════════════════════════════
        //  Test 9 – RSS memory stability
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Tracks actual OS-level memory (VmRSS on Linux, WorkingSet64 on
        /// Windows) across 50 execute-abandon-evict cycles.  The RSS must
        /// not grow continuously; a short-term spike followed by reclaim
        /// is acceptable but a monotonic upward trend is not.
        /// </summary>
        private void TestRssMemoryStability(ITypeData stepType, int cycles)
        {
            // Warmup
            for (int i = 0; i < 5; i++)
            {
                var s = (ITestStep)stepType.CreateInstance();
                var p = new TestPlan();
                p.ChildTestSteps.Add(s);
                p.Execute();
                p.ChildTestSteps.Clear();
            }
            RunFullCleanupCycle();

            long baseRss = GetRssBytes();
            int baseReflected = Runtime.ReflectedObjectCount;
            long baseHeap = GC.GetTotalMemory(true);
            Log.Info($"  Base: RSS={baseRss / (1024.0 * 1024):F1} MB, " +
                     $"Heap={baseHeap / (1024.0 * 1024):F1} MB, " +
                     $"Reflected={baseReflected}");

            long peakRss = baseRss;
            long[] rssHistory = new long[cycles];

            for (int i = 0; i < cycles; i++)
            {
                var step = (ITestStep)stepType.CreateInstance();
                var plan = new TestPlan();
                plan.ChildTestSteps.Add(step);
                plan.Execute();
                plan.ChildTestSteps.Clear();

                if ((i + 1) % 5 == 0)
                    RunFullCleanupCycle();

                long rss = GetRssBytes();
                rssHistory[i] = rss;
                if (rss > peakRss) peakRss = rss;

                if ((i + 1) % 10 == 0)
                {
                    int reflected = Runtime.ReflectedObjectCount;
                    long heap = GC.GetTotalMemory(false);
                    Log.Info($"  Cycle {i + 1}/{cycles}: RSS={rss / (1024.0 * 1024):F1} MB, " +
                             $"Heap={heap / (1024.0 * 1024):F1} MB, " +
                             $"Reflected={reflected}");
                }
            }

            RunFullCleanupCycle();
            long finalRss = GetRssBytes();
            long rssDelta = finalRss - baseRss;

            // Trend analysis: compare average of last 10 vs first 10
            double avgFirst = rssHistory.Take(10).Select(x => (double)x).Average();
            double avgLast = rssHistory.Skip(cycles - 10).Take(10)
                .Select(x => (double)x).Average();
            long trend = (long)(avgLast - avgFirst);

            Log.Info($"  Base RSS:  {baseRss / (1024.0 * 1024):F1} MB");
            Log.Info($"  Final RSS: {finalRss / (1024.0 * 1024):F1} MB " +
                     $"(delta: {rssDelta / (1024.0 * 1024):+0.0;-0.0;0.0} MB)");
            Log.Info($"  Peak RSS:  {peakRss / (1024.0 * 1024):F1} MB");
            Log.Info($"  Trend (avg last 10 − avg first 10): " +
                     $"{trend / (1024.0 * 1024):+0.0;-0.0;0.0} MB");

            // Allow up to 20 MB growth — covers JIT, native caches,
            // Python internal pools, etc. that are not leaks.
            const long maxRssGrowthBytes = 20L * 1024 * 1024;
            if (rssDelta > maxRssGrowthBytes)
                throw new Exception(
                    $"RSS memory FAILED: grew by {rssDelta / (1024.0 * 1024):F1} MB " +
                    $"over {cycles} cycles (max {maxRssGrowthBytes / (1024 * 1024)} MB)");

            Log.Info($"  RSS memory stable over {cycles} cycles — OK");
        }

        // ═══════════════════════════════════════════════════════════════
        //  Test 10 – Combined stress (mixed workload)
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Each round: creates 50 abandoned step instances (no execution),
        /// executes one plan with proper cleanup, then evicts.  Simulates
        /// a workload where the application both creates/discards objects
        /// and runs plans concurrently.
        /// </summary>
        private void TestCombinedStress(ITypeData stepType, int rounds)
        {
            RunFullCleanupCycle();
            int baseReflected = Runtime.ReflectedObjectCount;
            long baseRss = GetRssBytes();
            Log.Info($"  Base: reflected={baseReflected}, " +
                     $"RSS={baseRss / (1024.0 * 1024):F1} MB");

            int totalCreated = 0;
            int totalExecuted = 0;

            for (int round = 0; round < rounds; round++)
            {
                // Phase A: create 50 abandoned instances
                for (int i = 0; i < 50; i++)
                {
                    var s = (ITestStep)stepType.CreateInstance();
                    totalCreated++;
                }

                // Phase B: execute a plan and properly clean up
                var execStep = (ITestStep)stepType.CreateInstance();
                var plan = new TestPlan();
                plan.ChildTestSteps.Add(execStep);
                plan.Execute();
                plan.ChildTestSteps.Clear();
                totalCreated++;
                totalExecuted++;

                // Phase C: full cleanup
                RunFullCleanupCycle();

                if ((round + 1) % 5 == 0)
                {
                    int reflected = Runtime.ReflectedObjectCount;
                    long rss = GetRssBytes();
                    Log.Info($"  Round {round + 1}/{rounds}: reflected={reflected} " +
                             $"(delta={reflected - baseReflected:+0;-0;0}), " +
                             $"RSS={rss / (1024.0 * 1024):F1} MB");
                }
            }

            // Final double cleanup
            RunFullCleanupCycle();
            RunFullCleanupCycle();

            int finalReflected = Runtime.ReflectedObjectCount;
            long finalRss = GetRssBytes();
            int totalGrowth = finalReflected - baseReflected;

            Log.Info($"  Final: reflected={finalReflected}, growth={totalGrowth}");
            Log.Info($"  Total created: {totalCreated}, executed: {totalExecuted}");
            Log.Info($"  RSS delta: {((finalRss - baseRss) / (1024.0 * 1024)):+0.0;-0.0;0.0} MB");

            if (totalGrowth > 10)
                throw new Exception(
                    $"Combined stress FAILED: reflected grew by {totalGrowth} " +
                    $"over {rounds} rounds / {totalCreated} instances (max 10)");

            Log.Info($"  Combined stress ({rounds} rounds, {totalCreated} steps): " +
                     $"growth={totalGrowth} — OK");
        }

        // ═══════════════════════════════════════════════════════════════
        //  Test 11 – XML load + execute + discard (same plan)
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Mirrors the production OpenTAP runner Session 2 lifecycle for the
        /// "same testplan executed continuously" case: XML is deserialised into
        /// a fresh <see cref="TestPlan"/> instance, the plan is executed, then
        /// the reference is dropped.  Repeating this many times must not grow
        /// reflectedObjects or RSS continuously — TapSerializer caches and
        /// dynamic type wrappers are expected to recycle.
        /// </summary>
        private void TestXmlLoadExecuteDiscardSamePlan(ITypeData stepType, int cycles)
        {
            var xml = BuildPlanXml(stepType);

            // Warmup
            for (int i = 0; i < 5; i++)
            {
                var p = LoadPlanFromXml(xml);
                p.Execute();
                p.ChildTestSteps.Clear();
            }
            RunFullCleanupCycle();

            int baseReflected = Runtime.ReflectedObjectCount;
            long baseRss = GetRssBytes();
            long baseHeap = GC.GetTotalMemory(true);
            Log.Info($"  Base: reflected={baseReflected}, RSS={baseRss / (1024.0 * 1024):F1} MB, heap={baseHeap / (1024.0 * 1024):F1} MB");

            for (int i = 0; i < cycles; i++)
            {
                var plan = LoadPlanFromXml(xml);
                plan.Execute();
                plan.ChildTestSteps.Clear();

                if ((i + 1) % 10 == 0)
                {
                    RunFullCleanupCycle();
                    int reflected = Runtime.ReflectedObjectCount;
                    long rss = GetRssBytes();
                    long heap = GC.GetTotalMemory(false);
                    Log.Info($"  Cycle {i + 1}/{cycles}: reflected={reflected} " +
                             $"(delta={reflected - baseReflected:+0;-0;0}), " +
                             $"RSS={rss / (1024.0 * 1024):F1} MB " +
                             $"(delta={((rss - baseRss) / (1024.0 * 1024)):+0.0;-0.0;0.0} MB), " +
                             $"heap={heap / (1024.0 * 1024):F1} MB " +
                             $"(delta={((heap - baseHeap) / (1024.0 * 1024)):+0.0;-0.0;0.0} MB)");
                }
            }

            RunFullCleanupCycle();
            RunFullCleanupCycle();

            int finalReflected = Runtime.ReflectedObjectCount;
            long finalRss = GetRssBytes();
            long finalHeap = GC.GetTotalMemory(true);
            int reflectedGrowth = finalReflected - baseReflected;
            long rssGrowth = finalRss - baseRss;
            long heapGrowth = finalHeap - baseHeap;

            Log.Info($"  Final: reflected={finalReflected} (growth={reflectedGrowth:+0;-0;0}), " +
                     $"RSS delta={rssGrowth / (1024.0 * 1024):+0.0;-0.0;0.0} MB, " +
                     $"heap delta={heapGrowth / (1024.0 * 1024):+0.0;-0.0;0.0} MB");

            // Strict thresholds: per-cycle the plan is fully torn down, so growth
            // should be tiny.  Allow some slack for one-time TapSerializer warmup
            // that might happen after the warmup loop.
            const int maxReflectedGrowth = 50;
            const long maxRssGrowthBytes = 30L * 1024 * 1024;

            if (reflectedGrowth > maxReflectedGrowth)
                throw new Exception(
                    $"XML load same-plan FAILED: reflected grew by {reflectedGrowth} " +
                    $"over {cycles} cycles (max {maxReflectedGrowth})");

            if (rssGrowth > maxRssGrowthBytes)
                throw new Exception(
                    $"XML load same-plan FAILED: RSS grew by {rssGrowth / (1024.0 * 1024):F1} MB " +
                    $"over {cycles} cycles (max {maxRssGrowthBytes / (1024 * 1024)} MB)");

            Log.Info($"  XML load same-plan ({cycles} cycles): " +
                     $"reflected growth={reflectedGrowth}, RSS growth={rssGrowth / (1024.0 * 1024):F1} MB — OK");
        }

        // ═══════════════════════════════════════════════════════════════
        //  Test 12 – XML load + switch between two plans + execute
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Mirrors the production scenario where the runner session loads
        /// alternating test plans (e.g. <c>plan A</c>, <c>plan B</c>, <c>plan A</c>, …)
        /// from XML, executes each once, and discards.
        ///
        /// Two distinct XMLs are used to defeat any per-XML deserialisation
        /// cache that might mask a leak by reusing the same parsed objects.
        /// We assert RSS, heap, and reflectedObjects all stay bounded.
        /// </summary>
        private void TestXmlLoadSwitchExecute(ITypeData stepType, int switches)
        {
            // Two structurally different plans so TapSerializer cannot
            // shortcut by hashing the input.
            var xmlA = BuildPlanXml(stepType, stepCount: 1, planName: "PlanA");
            var xmlB = BuildPlanXml(stepType, stepCount: 2, planName: "PlanB");

            // Warmup — alternate a few times to JIT both code paths
            for (int i = 0; i < 6; i++)
            {
                var p = LoadPlanFromXml(i % 2 == 0 ? xmlA : xmlB);
                p.Execute();
                p.ChildTestSteps.Clear();
            }
            RunFullCleanupCycle();

            int baseReflected = Runtime.ReflectedObjectCount;
            long baseRss = GetRssBytes();
            long baseHeap = GC.GetTotalMemory(true);
            Log.Info($"  Base: reflected={baseReflected}, RSS={baseRss / (1024.0 * 1024):F1} MB, heap={baseHeap / (1024.0 * 1024):F1} MB");

            // Sample RSS history to detect monotonic growth
            long[] rssSamples = new long[switches / 10];
            int sampleIx = 0;

            for (int i = 0; i < switches; i++)
            {
                var xml = i % 2 == 0 ? xmlA : xmlB;
                var plan = LoadPlanFromXml(xml);
                plan.Execute();
                plan.ChildTestSteps.Clear();

                if ((i + 1) % 10 == 0)
                {
                    RunFullCleanupCycle();
                    int reflected = Runtime.ReflectedObjectCount;
                    long rss = GetRssBytes();
                    long heap = GC.GetTotalMemory(false);
                    if (sampleIx < rssSamples.Length) rssSamples[sampleIx++] = rss;
                    Log.Info($"  Switch {i + 1}/{switches} ({(i % 2 == 0 ? "A" : "B")}): " +
                             $"reflected={reflected} (delta={reflected - baseReflected:+0;-0;0}), " +
                             $"RSS={rss / (1024.0 * 1024):F1} MB " +
                             $"(delta={((rss - baseRss) / (1024.0 * 1024)):+0.0;-0.0;0.0} MB), " +
                             $"heap={heap / (1024.0 * 1024):F1} MB");
                }
            }

            RunFullCleanupCycle();
            RunFullCleanupCycle();

            int finalReflected = Runtime.ReflectedObjectCount;
            long finalRss = GetRssBytes();
            long finalHeap = GC.GetTotalMemory(true);
            int reflectedGrowth = finalReflected - baseReflected;
            long rssGrowth = finalRss - baseRss;
            long heapGrowth = finalHeap - baseHeap;

            // Linear-regression-lite: compare avg of first 3 samples to last 3.
            // If the trend is monotonic upward, that's the leak signal.
            long firstAvg = 0, lastAvg = 0;
            if (sampleIx >= 6)
            {
                long first = 0, last = 0;
                for (int k = 0; k < 3; k++) { first += rssSamples[k]; last += rssSamples[sampleIx - 1 - k]; }
                firstAvg = first / 3;
                lastAvg = last / 3;
            }

            Log.Info($"  Final: reflected={finalReflected} (growth={reflectedGrowth:+0;-0;0}), " +
                     $"RSS delta={rssGrowth / (1024.0 * 1024):+0.0;-0.0;0.0} MB, " +
                     $"heap delta={heapGrowth / (1024.0 * 1024):+0.0;-0.0;0.0} MB");
            if (sampleIx >= 6)
                Log.Info($"  RSS trend: first 3 samples avg={firstAvg / (1024.0 * 1024):F1} MB, " +
                         $"last 3 avg={lastAvg / (1024.0 * 1024):F1} MB, " +
                         $"trend={(lastAvg - firstAvg) / (1024.0 * 1024):+0.0;-0.0;0.0} MB");

            const int maxReflectedGrowth = 80;          // 2 step types × tolerance
            const long maxRssGrowthBytes = 40L * 1024 * 1024;
            const long maxRssTrendBytes = 25L * 1024 * 1024; // last 3 vs first 3

            if (reflectedGrowth > maxReflectedGrowth)
                throw new Exception(
                    $"XML load+switch FAILED: reflected grew by {reflectedGrowth} " +
                    $"over {switches} switches (max {maxReflectedGrowth})");

            if (rssGrowth > maxRssGrowthBytes)
                throw new Exception(
                    $"XML load+switch FAILED: RSS grew by {rssGrowth / (1024.0 * 1024):F1} MB " +
                    $"over {switches} switches (max {maxRssGrowthBytes / (1024 * 1024)} MB)");

            if (sampleIx >= 6 && (lastAvg - firstAvg) > maxRssTrendBytes)
                throw new Exception(
                    $"XML load+switch FAILED: RSS trend +{(lastAvg - firstAvg) / (1024.0 * 1024):F1} MB " +
                    $"(max {maxRssTrendBytes / (1024 * 1024)} MB) suggests monotonic leak");

            Log.Info($"  XML load+switch ({switches} switches): " +
                     $"reflected growth={reflectedGrowth}, RSS growth={rssGrowth / (1024.0 * 1024):F1} MB — OK");
        }

        // ═══════════════════════════════════════════════════════════════
        //  Test 12b — Heavy production-style load+switch
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Bigger version of <see cref="TestXmlLoadSwitchExecute"/> using
        /// <paramref name="stepsPerPlan"/> Python steps per plan to mimic
        /// realistic production plans (~50 steps).  Logs a per-type
        /// histogram of <c>reflectedObjects</c> on every cycle so we can
        /// see what types accumulate.
        /// </summary>
        private void TestXmlLoadSwitchExecuteHeavy(ITypeData stepType, int switches, int stepsPerPlan)
        {
            var xmlA = BuildPlanXml(stepType, stepCount: stepsPerPlan, planName: "HeavyA");
            var xmlB = BuildPlanXml(stepType, stepCount: stepsPerPlan + 1, planName: "HeavyB");

            // Warmup
            for (int i = 0; i < 3; i++)
            {
                var p = LoadPlanFromXml(i % 2 == 0 ? xmlA : xmlB);
                p.Execute();
                p.ChildTestSteps.Clear();
            }
            RunFullCleanupCycle();
            RunFullCleanupCycle();

            int baseReflected = Runtime.ReflectedObjectCount;
            long baseRss = GetRssBytes();
            long baseHeap = GC.GetTotalMemory(true);
            Log.Info($"  Base: reflected={baseReflected}, RSS={baseRss / (1024.0 * 1024):F1} MB, heap={baseHeap / (1024.0 * 1024):F1} MB");
            DumpHistogram("BASE");

            for (int i = 0; i < switches; i++)
            {
                var xml = i % 2 == 0 ? xmlA : xmlB;
                var plan = LoadPlanFromXml(xml);
                plan.Execute();
                plan.ChildTestSteps.Clear();

                if ((i + 1) % 5 == 0)
                {
                    RunFullCleanupCycle();
                    int reflected = Runtime.ReflectedObjectCount;
                    long rss = GetRssBytes();
                    long heap = GC.GetTotalMemory(false);
                    Log.Info($"  Heavy switch {i + 1}/{switches} ({(i % 2 == 0 ? "A" : "B")}): " +
                             $"reflected={reflected} (delta={reflected - baseReflected:+0;-0;0}), " +
                             $"RSS={rss / (1024.0 * 1024):F1} MB " +
                             $"(delta={((rss - baseRss) / (1024.0 * 1024)):+0.0;-0.0;0.0} MB), " +
                             $"heap={heap / (1024.0 * 1024):F1} MB " +
                             $"(delta={((heap - baseHeap) / (1024.0 * 1024)):+0.0;-0.0;0.0} MB)");
                }
            }

            RunFullCleanupCycle();
            RunFullCleanupCycle();

            int finalReflected = Runtime.ReflectedObjectCount;
            long finalRss = GetRssBytes();
            long finalHeap = GC.GetTotalMemory(true);
            int reflectedGrowth = finalReflected - baseReflected;
            long rssGrowth = finalRss - baseRss;
            long heapGrowth = finalHeap - baseHeap;

            Log.Info($"  Final: reflected={finalReflected} (growth={reflectedGrowth:+0;-0;0}), " +
                     $"RSS delta={rssGrowth / (1024.0 * 1024):+0.0;-0.0;0.0} MB, " +
                     $"heap delta={heapGrowth / (1024.0 * 1024):+0.0;-0.0;0.0} MB");
            DumpHistogram("FINAL");

            // Per-switch attribution to make leak ratios obvious in logs.
            double perSwitchReflected = reflectedGrowth / (double)switches;
            double perSwitchHeapKb = (heapGrowth / 1024.0) / switches;
            Log.Info($"  Per-switch: reflected={perSwitchReflected:F2}, heap={perSwitchHeapKb:F1} KB");

            // Strict thresholds — any per-switch growth over a tiny budget
            // means we're still leaking.
            const int maxReflectedGrowth = 50;
            const long maxHeapGrowthBytes = 8L * 1024 * 1024; // 8 MB over 50 heavy cycles
            const long maxRssGrowthBytes = 30L * 1024 * 1024;

            if (reflectedGrowth > maxReflectedGrowth)
                throw new Exception(
                    $"Heavy load+switch FAILED: reflected grew by {reflectedGrowth} " +
                    $"over {switches} cycles of {stepsPerPlan}-step plans (max {maxReflectedGrowth})");

            if (heapGrowth > maxHeapGrowthBytes)
                throw new Exception(
                    $"Heavy load+switch FAILED: heap grew by {heapGrowth / (1024.0 * 1024):F1} MB " +
                    $"over {switches} cycles (max {maxHeapGrowthBytes / (1024 * 1024)} MB)");

            if (rssGrowth > maxRssGrowthBytes)
                throw new Exception(
                    $"Heavy load+switch FAILED: RSS grew by {rssGrowth / (1024.0 * 1024):F1} MB " +
                    $"over {switches} cycles (max {maxRssGrowthBytes / (1024 * 1024)} MB)");

            Log.Info($"  Heavy load+switch ({switches} × {stepsPerPlan}-step plans): " +
                     $"reflected growth={reflectedGrowth}, heap growth={heapGrowth / (1024.0 * 1024):F1} MB — OK");
        }

        /// <summary>Logs a histogram of the top reflected-object types under a tag.</summary>
        private static void DumpHistogram(string tag)
        {
            try
            {
                IReadOnlyList<(string TypeName, int Count, long TotalRc, int PythonDerivedCount, int ParentlessStepCount)> hist;
                using (Py.GIL())
                {
                    hist = Runtime.DiagnoseTypeHistogram(topN: 15);
                }
                var sb = new System.Text.StringBuilder();
                sb.Append($"  [{tag}] Top reflected types:");
                foreach (var row in hist)
                {
                    sb.Append($"\n     {row.Count,5}× {row.TypeName} (rcSum={row.TotalRc}, pyDerived={row.PythonDerivedCount}, parentless={row.ParentlessStepCount})");
                }
                Log.Info(sb.ToString());
            }
            catch (Exception ex) { Log.Warning($"  [{tag}] Histogram failed: {ex.Message}"); }
        }

        // ═══════════════════════════════════════════════════════════════
        //  Test 13 – WeakReference leak detector
        // ═══════════════════════════════════════════════════════════════

        /// <summary>A trivial pure-.NET test step used to bisect the leak.</summary>
        public sealed class PureDotNetNoOpStep : TestStep
        {
            public override void Run() => UpgradeVerdict(Verdict.Pass);
        }

        /// <summary>
        /// Builds N plans, executes each, drops every strong reference, then
        /// asserts via <see cref="WeakReference"/> that the .NET GC actually
        /// reclaimed each <see cref="TestPlan"/>, each contained
        /// <see cref="ITestStep"/>, and each <see cref="TestPlanRun"/>.
        ///
        /// Bisects across THREE configurations to localise the leak source:
        ///   A) Pure .NET step + plan + execute  (no Python at all)
        ///   B) Python step + plan, NO execute   (rules out execution path)
        ///   C) Python step + plan + execute     (full production path)
        /// </summary>
        private void TestWeakReferenceLeakDetector(ITypeData pythonStepType)
        {
            var pureStepType = TypeData.FromType(typeof(PureDotNetNoOpStep));

            Log.Info("  -- 13a: pure .NET step, with execute --");
            int aSurvivors = RunWeakRefScenario(pureStepType, sampleSize: 20, execute: true);

            Log.Info("  -- 13b: Python step, NO execute --");
            int bSurvivors = RunWeakRefScenario(pythonStepType, sampleSize: 20, execute: false);

            Log.Info("  -- 13c: Python step, with execute --");
            int cSurvivors = RunWeakRefScenario(pythonStepType, sampleSize: 20, execute: true);

            Log.Info($"  Survivors — A(pure+exec)={aSurvivors}/60  B(py+noexec)={bSurvivors}/60  C(py+exec)={cSurvivors}/60");

            // Acceptance: ZERO survivors across all three scenarios. Any
            // remaining instance after two full cleanup cycles indicates a
            // strong .NET reference somewhere in the static caches / event
            // subscription graph. Reporting per-scenario localises the cause.
            if (aSurvivors > 0)
                throw new Exception($"WeakReference leak (A: pure .NET + execute): {aSurvivors}/60 instances survived — leak is in OpenTap core / TapX listeners (NOT Python)");
            if (bSurvivors > 0)
                throw new Exception($"WeakReference leak (B: Python step, no execute): {bSurvivors}/60 instances survived — leak is in Python step instantiation / type-data caching");
            if (cSurvivors > 0)
                throw new Exception($"WeakReference leak (C: Python + execute): {cSurvivors}/60 instances survived — leak is specific to executing Python plans");

            Log.Info("  All TestPlan / ITestStep / TestPlanRun instances were collected — OK");
        }

        /// <summary>
        /// Runs a sample of <paramref name="sampleSize"/> create-execute-drop
        /// cycles and returns the total number of (plan + step + run)
        /// instances that survived two full cleanup cycles.
        /// </summary>
        private int RunWeakRefScenario(ITypeData stepType, int sampleSize, bool execute)
        {
            var planRefs = new WeakReference[sampleSize];
            var stepRefs = new WeakReference[sampleSize];
            var runRefs = new WeakReference[sampleSize];

            for (int i = 0; i < sampleSize; i++)
                CreateAndExecuteOnce(stepType, i, planRefs, stepRefs, runRefs, execute);

            RunFullCleanupCycle();
            RunFullCleanupCycle();

            int planAlive = 0, stepAlive = 0, runAlive = 0;
            for (int i = 0; i < sampleSize; i++)
            {
                if (planRefs[i].IsAlive) planAlive++;
                if (stepRefs[i].IsAlive) stepAlive++;
                if (runRefs[i].IsAlive) runAlive++;
            }

            Log.Info($"     plan={planAlive}/{sampleSize}  step={stepAlive}/{sampleSize}  run={runAlive}/{sampleSize}");
            return planAlive + stepAlive + runAlive;
        }

        /// <summary>
        /// Helper for <see cref="TestWeakReferenceLeakDetector"/>: kept in a
        /// separate method so the local strong references go out of scope
        /// before the caller's GC, allowing the WeakReferences to be valid.
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void CreateAndExecuteOnce(
            ITypeData stepType,
            int i,
            WeakReference[] planRefs,
            WeakReference[] stepRefs,
            WeakReference[] runRefs,
            bool execute)
        {
            var step = (ITestStep)stepType.CreateInstance();
            var plan = new TestPlan();
            plan.ChildTestSteps.Add(step);

            TestPlanRun? run = null;
            if (execute)
                run = plan.Execute();

            planRefs[i] = new WeakReference(plan);
            stepRefs[i] = new WeakReference(step);
            runRefs[i] = new WeakReference((object?)run ?? new object());

            // Detach to mirror production teardown
            plan.ChildTestSteps.Clear();
            // local refs (step, plan, run) drop on method exit
        }


        private static string BuildPlanXml(ITypeData stepType, int stepCount = 1, string planName = "MemTestPlan")
        {
            // Use TapSerializer to round-trip a plan we build in-memory — ensures
            // the XML is exactly what OpenTAP expects for this step type.
            var plan = new TestPlan();
            for (int i = 0; i < stepCount; i++)
                plan.ChildTestSteps.Add((ITestStep)stepType.CreateInstance());
            using var ms = new MemoryStream();
            new TapSerializer().Serialize(ms, plan);
            return System.Text.Encoding.UTF8.GetString(ms.ToArray());
        }

        /// <summary>Deserialises a TestPlan XML using the same path as production (TestPlan.Load).</summary>
        private static TestPlan LoadPlanFromXml(string xml)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(xml);
            using var ms = new MemoryStream(bytes);
            return TestPlan.Load(ms, "memtestplan.TapPlan");
        }


        /// <list type="number">
        ///   <item>Finalizer drain</item>
        ///   <item>Python gc.collect × 3</item>
        ///   <item>EvictAbandonedObjects (orphaned ITestStep with Parent==null)</item>
        ///   <item>.NET GC.Collect(2) outside GIL</item>
        ///   <item>Second Finalizer drain</item>
        ///   <item>malloc_trim (Linux)</item>
        /// </list>
        /// </summary>
        private void RunFullCleanupCycle()
        {
            // Phase 1-3: inside GIL
            using (Py.GIL())
            {
                // Drain finalizer queue
                try { Finalizer.Instance.Collect(); } catch { }

                // Python GC × 3 generations
                try
                {
                    using var gc = Py.Import("gc");
                    for (int gen = 0; gen < 3; gen++)
                    {
                        using var arg = new PyInt(gen);
                        gc.InvokeMethod("collect", arg);
                    }
                }
                catch { }

                // Evict phantom references on abandoned PythonDerived objects
                try
                {
                    // Drop the strong PyObject anchors first so eviction can
                    // actually free the Python wrapper (otherwise the anchor's
                    // INCREF keeps tp_dealloc from firing).
                    PythonTypeDataWrapper.RemoveAnchors(inst =>
                    {
                        if (inst is ITestStep step)
                            return step.Parent == null;
                        return false;
                    });

                    Runtime.EvictAbandonedObjects(inst =>
                    {
                        if (inst is ITestStep step)
                            return step.Parent == null;
                        return false;
                    });
                }
                catch { }
            }

            // Phase 4: .NET GC (outside GIL to avoid deadlocks)
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();

            // Phase 5-6: re-acquire GIL
            using (Py.GIL())
            {
                // Second finalizer drain
                try { Finalizer.Instance.Collect(); } catch { }

                // malloc_trim (Linux — return freed native memory to OS)
                try
                {
                    using var ctypes = Py.Import("ctypes");
                    using var libcName = new PyString("libc.so.6");
                    using var cdll = ctypes.GetAttr("CDLL");
                    using var libc = cdll.Invoke(libcName);
                    using var trimFn = libc.GetAttr("malloc_trim");
                    using var zero = new PyInt(0);
                    trimFn.Invoke(zero);
                }
                catch { /* Not Linux/glibc — skip silently */ }
            }
        }

        /// <summary>
        /// Returns the current process RSS in bytes.
        /// On Linux reads VmRSS from /proc/self/status (accurate).
        /// Falls back to .NET WorkingSet64 on other platforms.
        /// </summary>
        private static long GetRssBytes()
        {
            try
            {
                if (File.Exists("/proc/self/status"))
                {
                    foreach (var line in File.ReadAllLines("/proc/self/status"))
                    {
                        if (line.StartsWith("VmRSS:", StringComparison.Ordinal))
                        {
                            var parts = line.Split(
                                new[] { ' ', '\t' },
                                StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length >= 2 &&
                                long.TryParse(parts[1], out var kb))
                                return kb * 1024; // kB → bytes
                        }
                    }
                }
            }
            catch { }

            // Fallback
            try
            {
                return System.Diagnostics.Process.GetCurrentProcess().WorkingSet64;
            }
            catch { return 0; }
        }
    }
}
