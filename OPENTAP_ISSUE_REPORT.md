# OpenTAP + pythonnet — Memory-Leak Fixes

Reference baselines: pythonnet `v3.0.1` (`42ee643`), `OpenTap.Python` 3.2.1 (`eb8f477`).

Empirically reproduced on Windows x64 (Python 3.11) and Linux ARM64 (Revolution Pi). Reproducer: 50 plans × 50 trivial Python `TestStep` instances per plan, `GC.GetTotalMemory(forceFullCollection: true)` between iterations.

| Build | Growth over 50 × 50 step instances |
|---|---|
| Stock pythonnet `v3.0.1` | **+39 MB** (~16 KB / step instance, monotonic) |
| With the fixes below | **+1 MB** (~400 B / step instance) |

---

## 1. `CLRObject.reflectedObjects` is append-only

**Where:** `src/runtime/Types/ClrObject.cs`, the static `internal static readonly HashSet<IntPtr> reflectedObjects`.

**What goes wrong:** every CLR object exposed to Python is recorded in this hash set (via `CLRObject.GetReference` and `ToPython`). Entries are inserted but **never removed**. When the Python wrapper's refcount hits zero, the `IntPtr` stays in the set, the underlying `GCHandle` and the wrapper's `__dict__` (which holds Python state of any Python-derived `TestStep`) survive until process exit. The entries are reachable from a static field, so `GC.Collect(GC.MaxGeneration, …, blocking: true, compacting: true)` does **not** reclaim them.

This is the dominant leak source — neutralizing only this fix in our build reproduces the +39 MB stock-pythonnet number; restoring it brings growth back to +1 MB.

**Fix (pythonnet):** add an explicit eviction API:

```csharp
// CLRObject.cs
internal static int EvictAbandonedObjects(Func<object, bool>? isAbandoned = null)
{
    lock (reflectedObjects)
    {
        var snapshot = reflectedObjects.ToArray();
        int released = 0;

        // Pass 1 — phantoms: rc<=1, GCHandle invalid, __dict__ already cleared.
        // Always safe — nothing on the Python side can resurrect these.
        var preEvictRc1 = new HashSet<IntPtr>();
        foreach (var addr in snapshot)
            if (Runtime.Refcount(addr) <= 1) preEvictRc1.Add(addr);

        foreach (var addr in snapshot)
            if (reflectedObjects.Contains(addr) && IsPhantom(addr)) {
                FreeGCHandle(addr);
                reflectedObjects.Remove(addr);
                released++;
            }

        // Pass 2 — caller-driven abandonment predicate. Skip entries that were
        // already at rc==1 *before* pass 1 — those are still-live objects whose
        // owners are mid-call.
        if (isAbandoned != null) {
            foreach (var addr in snapshot) {
                if (preEvictRc1.Contains(addr)) continue;
                if (!reflectedObjects.Contains(addr)) continue;
                var managed = TryGetManaged(addr);
                if (managed != null && isAbandoned(managed)) {
                    FreeGCHandle(addr);
                    reflectedObjects.Remove(addr);
                    released++;
                }
            }
        }
        return released;
    }
}
```

Re-export from `Runtime.cs` so external assemblies can call it without referencing internals:

```csharp
public static int EvictAbandonedObjects(Func<object, bool>? isAbandoned = null)
    => CLRObject.EvictAbandonedObjects(isAbandoned);
```

---

## 2. No driver to call the eviction

**Where:** `OpenTap.Python` package — currently nothing calls into pythonnet's eviction (even if it existed).

**Fix:** ship an `ITestPlanRunMonitor` that periodically drains the finalizer queue and calls `Runtime.EvictAbandonedObjects(IsAbandoned)`:

```csharp
public sealed class PythonMemoryMonitor : ComponentSettings<PythonMemoryMonitor>,
                                          ITestPlanRunMonitor
{
    [Display("Cleanup Interval (s)")] public int CleanupIntervalSeconds { get; set; } = 30;

    private static readonly object _liveLock = new();
    private static readonly List<WeakReference> _live = new();

    private Thread _cleanupThread;
    private readonly ManualResetEventSlim _stop = new(false);

    public void EnterTestPlanRun(TestPlanRun run)
    {
        var plan = (TestPlan)typeof(TestPlanRun)
            .GetField("plan", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(run)!;
        lock (_liveLock) _live.Add(new WeakReference(plan));

        _stop.Reset();
        _cleanupThread = new Thread(CleanupLoop) {
            IsBackground = true,        // ★ NOT a thread-pool job — see issue 3
            Name = "PythonMemoryMonitor"
        };
        _cleanupThread.Start();
    }

    public void ExitTestPlanRun(TestPlanRun run)
    {
        _stop.Set();
        _cleanupThread?.Join(TimeSpan.FromSeconds(5));
    }

    private void CleanupLoop()
    {
        while (!_stop.Wait(TimeSpan.FromSeconds(CleanupIntervalSeconds)))
        {
            if (!PythonEngine.IsInitialized) continue;
            using (Py.GIL()) Runtime.EvictAbandonedObjects(IsAbandoned);
        }
    }

    private static bool IsAbandoned(object inst)
    {
        if (inst is TestPlan tp) return !IsLive(tp);
        if (inst is ITestStepParent node) {
            for (int i = 0; i < 64 && node != null; i++) {  // bounded against cycles
                if (node is TestPlan p) return !IsLive(p);
                var next = node.Parent;
                if (ReferenceEquals(next, node)) break;
                node = next;
            }
            return true;            // free-floating step graph
        }
        return false;               // global resource (ComponentSettings, …)
    }

    private static bool IsLive(TestPlan p) {
        lock (_liveLock)
            foreach (var w in _live)
                if (ReferenceEquals(w.Target, p)) return true;
        return false;
    }
}
```

---

## 3. ThreadPool growth from stuck GIL callbacks

**Where:** any periodic cleanup invocation that uses `ThreadPool.QueueUserWorkItem`, `Task.Run`, or `System.Threading.Timer` to call into pythonnet under the GIL.

**What goes wrong:** if the GIL is held by a long-running Python callback (or a `Result Listener` mid-publish), the queued work item blocks waiting for the GIL. The `ThreadPool` interprets the stuck thread as starvation and grows another worker — which also blocks. We measured **8 000+ leaked threads** on ARM64 inside a multi-hour soak run before identifying this as the cause. Each thread is ~8 MB of committed stack on Linux, so the secondary leak dwarfs the primary one over time.

**Fix:** use a dedicated `IsBackground = true` `Thread`, not the pool, for any periodic work that must acquire the GIL. The `PythonMemoryMonitor` sketch in issue #2 already does this.

---

## 4. `PyObject` lifetime not anchored to the .NET object

**Where:** `OpenTap.Python/PythonTypeDataWrapper.cs` — wherever the plugin caches or returns a `PyObject` derived from a managed instance.

**What goes wrong:** the .NET GC is free to collect a `PyObject` before pythonnet's `reflectedObjects` slot for the same `IntPtr` is cleared. The next `GetReference(sameObj, sameType)` call returns the address of the dead wrapper. With the eviction sweep from issue #1 active this becomes visible: previously-permanent zombies are now actually freed, and any code path that re-uses a stale `PyObject` reads released memory.

**Fix:** anchor the `PyObject` lifetime to the .NET object via a `ConditionalWeakTable` so the entry disappears exactly when the .NET object becomes unreachable — never sooner, never later:

```csharp
// PythonTypeDataWrapper.cs
private static readonly ConditionalWeakTable<object, PyObject> _aliveObjects = new();

public PyObject ToPyObject(object netInstance)
{
    if (_aliveObjects.TryGetValue(netInstance, out var existing)) return existing;
    var fresh = /* CLRObject.GetReference(...) wrapped as PyObject */;
    _aliveObjects.Add(netInstance, fresh);
    return fresh;
}
```

`ConditionalWeakTable` keeps the value alive only as long as the *key* is alive, but never the other way round, so the table itself adds zero retention and prevents the dangling-pointer regression that the eviction pass would otherwise expose.
