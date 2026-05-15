// Minimal reproduction for the OpenTap.Python / pythonnet RAM leak.
//
// What this shows
// ---------------
// Each iteration:
//   1. Builds a TestPlan
//   2. Adds N Python-derived TestStep instances (ClassDerived in pythonnet terms)
//   3. Executes the plan
//   4. Drops every reference and forces full blocking GC
//
// On *stock* pythonnet + OpenTap.Python, the GC.GetTotalMemory(true) value
// climbs steadily run after run, even though nothing should remain alive.
//
// Root cause (short version):
//   - pythonnet pins each CLR↔Python wrapper in a static
//     HashSet<IntPtr> CLRObject.reflectedObjects.
//   - Regular CLR wrappers are removed via tp_clear.
//   - PythonDerived wrappers carry a deliberately-leaked "phantom reference"
//     so .NET can keep calling Python overrides → tp_dealloc never fires →
//     reflectedObjects entry lives forever.
//   - Every Execute() also round-trips through PythonTypeDataWrapper which
//     used to create *two* tracked wrappers per CreateInstance().
//
// Run with the patched Python.Runtime.dll (the v2 fix) and the growth flat-
// lines and even goes slightly negative per cleanup cycle.
//
// Build:
//   dotnet build mem_leak_repro/mem_leak_repro.csproj -c Release
//
// Run (from the OpenTAP install dir, with Python package deployed):
//   tap.exe python install        # if not already
//   cp mem_leak_repro/leak_step.py <opentap>/Packages/<your_python_pkg>/
//   <opentap>/mem_leak_repro

using System;
using System.IO;
using System.Reflection;
using System.Runtime;
using System.Runtime.Loader;
using OpenTap;
using OpenTap.Python;
using Python.Runtime;

namespace MemLeakRepro;

internal static class Program
{
    private const int OuterIterations = 50;
    private const int StepsPerPlan = 50;
    private const int ReportEvery = 5;

    private static int Main()
    {
        // OpenTap.Python and OpenTap dlls sit next to this exe but the
        // .NET deps resolver doesn't probe siblings for project refs that
        // target netstandard. Hand-roll an AppContext-directory probe.
        AssemblyLoadContext.Default.Resolving += (ctx, name) =>
        {
            var probe = Path.Combine(AppContext.BaseDirectory, name.Name + ".dll");
            return File.Exists(probe) ? ctx.LoadFromAssemblyPath(probe) : null;
        };

        return Run();
    }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static int Run() => Runner.Run(OuterIterations, StepsPerPlan, ReportEvery);
}

// All real work lives in a separate type so its method bodies are JIT-compiled
// only AFTER Main has installed the AssemblyResolve handler.
internal static class Runner
{
    public static int Run(int outerIterations, int stepsPerPlan, int reportEvery)
    {
        // 1. Bring up OpenTAP and Python (mirrors what `tap.exe` does at startup).
        OpenTap.PluginManager.SearchAsync().Wait();

        // Attach a console log listener only for init so we can diagnose failures.
        var initLog = new ConsoleLogListener();
        OpenTap.Log.AddListener(initLog);

        bool loaded;
        try { loaded = PythonInitializer.LoadPython(); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"LoadPython threw: {ex}");
            return 1;
        }

        if (!loaded || !PythonEngine.IsInitialized)
        {
            Console.Error.WriteLine("Python engine failed to initialize.");
            return 1;
        }

        // 2. Resolve the Python-derived step type. Discovery names follow the
        //    package path (e.g. "PythonExamples.leak_step.LeakStep"), not the
        //    `__namespace__` attribute, so just scan derived step types.
        // 2. Resolve the Python-derived step type. Python types are registered
        //    by PythonPluginProvider.Search(); trigger it explicitly and then
        //    look up our step by name.
        var provider = new OpenTap.Python.PythonPluginProvider();
        provider.Search();

        ITypeData stepType = null;
        foreach (var t in provider.Types)
        {
            if ((t.Name ?? "").Contains("LeakStep", StringComparison.Ordinal))
            {
                stepType = t;
                break;
            }
        }

        if (stepType == null)
        {
            Console.Error.WriteLine("Could not find LeakStep python type. " +
                                    "Confirm leak_step.py was deployed under Packages/PythonExamples/.");
            Console.Error.WriteLine("Discovered Python types:");
            foreach (var t in provider.Types) Console.Error.WriteLine($"  {t.Name}");
            return 2;
        }

        Console.WriteLine($"Python:   {PythonEngine.Version.ToString().Trim()}");
        Console.WriteLine($"StepType: {stepType.Name}");
        Console.WriteLine($"Plan:     {stepsPerPlan} python steps × {outerIterations} executions");
        Console.WriteLine();

        // Drop the verbose console listener now that init succeeded.
        OpenTap.Log.RemoveListener(initLog);

        // 3. Establish a baseline after JIT/type-load warm-up.
        for (int i = 0; i < 2; i++) BuildAndExecute(stepType, stepsPerPlan);
        FullGc();
        long baseline = GC.GetTotalMemory(forceFullCollection: true);
        Console.WriteLine($"Baseline:        {Mb(baseline),8:F2} MB");
        Console.WriteLine();

        // 4. Loop and report growth.
        for (int i = 1; i <= outerIterations; i++)
        {
            BuildAndExecute(stepType, stepsPerPlan);

            if (i % reportEvery == 0)
            {
                FullGc();
                long current = GC.GetTotalMemory(forceFullCollection: true);
                double growth = Mb(current - baseline);
                Console.WriteLine(
                    $"After run {i,3}: {Mb(current),8:F2} MB  (growth: {growth,+7:+0.00;-0.00;0.00} MB)");
            }
        }

        Console.WriteLine();
        Console.WriteLine("Stock pythonnet: growth is monotonic and unbounded.");
        Console.WriteLine("v2-patched Python.Runtime.dll: growth flatlines (or goes slightly negative).");
        return 0;
    }

    // Kept in its own non-inlined frame so locals are guaranteed dead on return.
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void BuildAndExecute(ITypeData stepType, int stepsPerPlan)
    {
        var plan = new TestPlan();
        for (int i = 0; i < stepsPerPlan; i++)
        {
            var step = (ITestStep)stepType.CreateInstance(Array.Empty<object>());
            plan.ChildTestSteps.Add(step);
        }
        plan.Execute(Array.Empty<IResultListener>());
        // plan, all steps, and every wrapper go out of scope here —
        // on stock pythonnet they leak via CLRObject.reflectedObjects.
    }

    private static void FullGc()
    {
        for (int i = 0; i < 3; i++)
        {
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }

    private static double Mb(long bytes) => bytes / (1024.0 * 1024.0);
}

internal sealed class ConsoleLogListener : OpenTap.Diagnostic.ILogListener
{
    public void EventsLogged(System.Collections.Generic.IEnumerable<OpenTap.Diagnostic.Event> events)
    {
        foreach (var e in events)
            Console.WriteLine($"[{e.Source}] {e.Message}");
    }
    public void Flush() { }
}
