using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using NUnit.Framework;
using Python.Runtime;

namespace OpenTap.Python.UnitTests
{
    /// <summary>
    /// NUnit integration tests that verify:
    /// <list type="number">
    ///   <item>reflectedObjects does not grow unboundedly across repeated plan executions</item>
    ///   <item>Result listeners receive OnTestPlanRunCompleted during plan execution</item>
    ///   <item>Python-derived steps are callable after many cycles without dangling pointers</item>
    ///   <item>Finalizer drain mechanism works correctly</item>
    /// </list>
    /// Run with: <c>dotnet test --filter "FullyQualifiedName~MemoryLeakTests"</c>
    /// </summary>
    [TestFixture]
    public class MemoryLeakTests
    {
        private static readonly TraceSource Log = OpenTap.Log.CreateSource("mem-test");

        [OneTimeSetUp]
        public void SetUp()
        {
            // Try standard OpenTAP Python initialization first
            PythonInitializer.LoadPython();

            // If auto-discovery failed, try manual initialization with common paths
            if (!PythonEngine.IsInitialized)
            {
                Log.Info("Auto-discovery failed. Attempting manual Python initialization...");
                TryManualPythonInit();
            }

            NUnit.Framework.Assert.That(PythonEngine.IsInitialized, Is.True,
                "Python engine must be initialized. Ensure Python 3.7-3.13 is installed " +
                "and discoverable, or set PYTHON_DLL environment variable.");
        }

        private static void TryManualPythonInit()
        {
            // Check PYTHON_DLL env var first
            var envDll = Environment.GetEnvironmentVariable("PYTHON_DLL");
            if (!string.IsNullOrEmpty(envDll) && System.IO.File.Exists(envDll))
            {
                InitPythonFromDll(envDll);
                return;
            }

            // Try common Windows Python locations
            string[] searchPaths = {
                @"C:\ProgramData\anaconda3",
                @"C:\Python313", @"C:\Python312", @"C:\Python311", @"C:\Python310",
                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python"),
            };

            foreach (var basePath in searchPaths)
            {
                if (!System.IO.Directory.Exists(basePath)) continue;
                // Look for python3xx.dll directly in this folder
                foreach (var dll in System.IO.Directory.GetFiles(basePath, "python3*.dll"))
                {
                    var name = System.IO.Path.GetFileName(dll);
                    // Skip python3.dll (stub), want python3XX.dll
                    if (name == "python3.dll") continue;
                    InitPythonFromDll(dll);
                    if (PythonEngine.IsInitialized) return;
                }
                // Check subdirectories (e.g., Python312/)
                foreach (var subDir in System.IO.Directory.GetDirectories(basePath))
                {
                    foreach (var dll in System.IO.Directory.GetFiles(subDir, "python3*.dll"))
                    {
                        var name = System.IO.Path.GetFileName(dll);
                        if (name == "python3.dll") continue;
                        InitPythonFromDll(dll);
                        if (PythonEngine.IsInitialized) return;
                    }
                }
            }
        }

        private static void InitPythonFromDll(string dllPath)
        {
            try
            {
                Log.Info($"Trying Python DLL: {dllPath}");
                Runtime.PythonDLL = dllPath;
                var home = System.IO.Path.GetDirectoryName(dllPath);
                PythonEngine.PythonHome = home;
                PythonEngine.Initialize(false);
                if (PythonEngine.IsInitialized)
                {
                    PythonEngine.BeginAllowThreads();
                    Log.Info($"Python {PythonEngine.Version} initialized from {dllPath}");
                }
            }
            catch (Exception ex)
            {
                Log.Info($"Failed: {ex.Message}");
            }
        }

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

        [Test]
        public void ResultListener_ReceivesOnTestPlanRunCompleted()
        {
            const int totalRuns = 3;

            var listener = new TrackingResultListener();
            ResultSettings.Current.Add(listener);
            try
            {
                var pyStepType = TypeData.GetTypeData("TestModule.BasicStepTest");
                NUnit.Framework.Assert.That(pyStepType, Is.Not.Null,
                    "TestModule.BasicStepTest type must be discoverable");

                var step = (ITestStep)pyStepType.CreateInstance();
                NUnit.Framework.Assert.That(step, Is.Not.Null);

                // Assign the DUT to avoid null-ref in the step's Run()
                var pyDutType = TypeData.GetTypeData("TestModule.DutTest");
                var dut = (IDut)pyDutType.CreateInstance();
                DutSettings.Current.Add(dut);
                pyStepType.GetMember("Dut").SetValue(step, dut);

                var plan = new TestPlan();
                plan.ChildTestSteps.Add(step);

                for (int i = 0; i < totalRuns; i++)
                {
                    var run = plan.Execute();
                    Log.Info($"  Run {i + 1}: Verdict={run.Verdict}");
                }

                NUnit.Framework.Assert.Multiple(() =>
                {
                    NUnit.Framework.Assert.That(listener.PlanStartCount, Is.EqualTo(totalRuns),
                        $"Expected {totalRuns} OnTestPlanRunStart calls");
                    NUnit.Framework.Assert.That(listener.PlanCompletedCount, Is.EqualTo(totalRuns),
                        $"Expected {totalRuns} OnTestPlanRunCompleted calls");
                });

                Log.Info($"  Result listener received {listener.PlanCompletedCount}/{totalRuns} " +
                         "OnTestPlanRunCompleted calls — OK");

                DutSettings.Current.Remove(dut);
            }
            finally
            {
                ResultSettings.Current.Remove(listener);
            }
        }

        [Test]
        public void ReflectedObjects_DoNotGrowUnbounded()
        {
            const int warmupRuns = 3;
            const int measureRuns = 10;
            // Maximum acceptable growth per run in reflectedObjects after warmup.
            const int maxGrowthPerRun = 5;

            var pyStepType = TypeData.GetTypeData("TestModule.BasicStepTest");
            NUnit.Framework.Assert.That(pyStepType, Is.Not.Null);

            int[] reflectedCounts = new int[warmupRuns + measureRuns];

            for (int i = 0; i < warmupRuns + measureRuns; i++)
            {
                // Create fresh step and plan each iteration to stress wrapper creation.
                var step = (ITestStep)pyStepType.CreateInstance();
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

            NUnit.Framework.Assert.That(totalGrowth, Is.LessThanOrEqualTo(allowedGrowth),
                $"reflectedObjects grew by {totalGrowth} over {measureRuns} runs " +
                $"(max allowed: {allowedGrowth}). Leak detected!");

            Log.Info("  reflectedObjects stable — OK");
        }

        [Test]
        public void StepMethods_WorkAfterGCPressure()
        {
            var pyStepType = TypeData.GetTypeData("TestModule.BasicStepTest");
            NUnit.Framework.Assert.That(pyStepType, Is.Not.Null);

            var steps = new List<ITestStep>();

            // Create many steps
            for (int i = 0; i < 20; i++)
            {
                var step = (ITestStep)pyStepType.CreateInstance();
                steps.Add(step);
            }

            // Force GC to pressure weak handles
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();

            // Verify all steps are still usable — if __pyobj__ were dangling, this would crash
            foreach (var step in steps)
            {
                NUnit.Framework.Assert.DoesNotThrow(
                    () => step.GetType().GetMethod("MethodTest")?.Invoke(step, Array.Empty<object>()),
                    "Step method call should not throw after GC pressure");
            }

            // Execute them all in a plan
            var plan = new TestPlan();
            foreach (var step in steps)
                plan.ChildTestSteps.Add(step);

            var run = plan.Execute();
            Log.Info($"  Executed plan with {steps.Count} steps: Verdict={run.Verdict}");

            // Verify all steps ran (PostPlanRun was called)
            foreach (var step in steps)
            {
                var postRun = (bool)pyStepType.GetMember("PostPlanRunExecuted").GetValue(step);
                NUnit.Framework.Assert.That(postRun, Is.True,
                    "PostPlanRunExecuted should be true after plan execution");
            }

            Log.Info("  All step methods callable after GC pressure — OK");
        }

        [Test]
        public void FinalizerDrain_CompletesWithoutErrors()
        {
            int countBefore;
            using (Py.GIL())
            {
                countBefore = Runtime.ReflectedObjectCount;

                // Create and dispose some Python objects
                for (int i = 0; i < 100; i++)
                {
                    using var pyInt = new PyInt(i);
                }
            }

            // Force .NET GC to finalize any remaining wrappers
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();

            // Drain the finalizer queue — should not throw
            NUnit.Framework.Assert.DoesNotThrow(() =>
            {
                using (Py.GIL())
                {
                    Finalizer.Instance.Collect();
                }
            }, "Finalizer drain should complete without errors");

            int countAfter = Runtime.ReflectedObjectCount;
            Log.Info($"  Reflected before: {countBefore}, after: {countAfter}");
            Log.Info("  Finalizer drain completed without errors — OK");
        }

        [Test]
        public void ReflectedObjectCount_IsAccessible()
        {
            // Simple smoke test: the property exists and returns a non-negative value
            int count = Runtime.ReflectedObjectCount;
            NUnit.Framework.Assert.That(count, Is.GreaterThanOrEqualTo(0),
                "ReflectedObjectCount should be non-negative");
            Log.Info($"  Current ReflectedObjectCount: {count}");
        }
    }
}
