# OpenTAP Python Memory Fix — Technical Deep Dive (v2 — Clean Source Fix)

## tl;dr

The stock OpenTAP Python package (`pythonnet` + `OpenTap.Python`) leaks memory
every time a .NET object crosses the CLR↔Python boundary.  On long-running or
infinite-loop test plans the process grows by **~100–300 MB/hour** on a
Raspberry Pi and eventually triggers the OOM killer.

The v1 fix used a brute-force `EvictCollectable()` mechanism to periodically
drain the leaked tracking set.  **v2 eliminates this workaround entirely** by
fixing the root cause: making pythonnet's existing cleanup chain properly cover
all object lifecycles, including Python-derived types.

| Layer                       | Repo             | What changed                                                                                                                                                                                                                                |
| --------------------------- | ---------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **pythonnet** (runtime)     | `pythonnet` fork | 3 surgical edits to `ClassDerived.cs` so `reflectedObjects` entries are removed during the normal `tp_dealloc`/`ToPython`/`Finalize` lifecycle. Removed `EvictCollectable`/`EvictResult`. Added `ReflectedObjectCount` diagnostic property. |
| **OpenTap.Python** (plugin) | This repo        | Simplified `PythonMemoryMonitor` from 6-phase to 5-phase (removed `EvictReflectedObjects` phase). Relies entirely on GC + Finalizer drain + `malloc_trim`.                                                                                  |

The RAM fix is working and stable on ARM64 (Raspberry Pi 4) and x64 (Windows).

---

## 1. Root Cause — `reflectedObjects` Never Shrinks

### How pythonnet bridges CLR and Python

When a .NET (`CLR`) object needs to be visible to Python, pythonnet creates a
**Python wrapper object** and pins the .NET object with a `GCHandle` so the
garbage collector cannot move or collect it.  This happens inside
`CLRObject.Create()`:

```
CLR object  ──GCHandle──▶  CLRObject (ManagedType)
                               │
Python wrapper (PyObject)  ◀───┘ tp_clr_inst slot stores GCHandle
```

Every wrapper's `IntPtr` is added to a static `HashSet<IntPtr>` called
**`CLRObject.reflectedObjects`**.

### Two wrapper categories

| Category                     | Created by                                   | `tp_dealloc` handler                | Cleanup path                                                 |
| ---------------------------- | -------------------------------------------- | ----------------------------------- | ------------------------------------------------------------ |
| **Regular CLR wrappers**     | `Converter.ToPython`, method args, etc.      | `ClassBase.tp_dealloc` → `tp_clear` | `tp_clear` → `TryFreeGCHandle` → `reflectedObjects.Remove` ✅ |
| **Python-derived instances** | `ClassDerived.InvokeCtor`, Python `__init__` | `ClassDerivedObject.tp_dealloc`     | **Nothing removed from reflectedObjects** ❌                  |

**Stock pythonnet already handles regular CLR wrappers correctly.** The cleanup
chain `ClassBase.tp_dealloc → CallClear → tp_clear → reflectedObjects.Remove`
works for the majority of objects.  Additionally, `ThrottledCollect` in
`PyObject` constructors drains the Finalizer queue every 200 constructions,
triggering deferred `Py_DecRef` calls that reclaim dead wrappers.

The leak affects **only Python-derived types** (steps, listeners, instruments,
DUTs) — objects whose `.NET` class inherits from a Python subclass via
`ClassDerived.InvokeCtor`.  These follow a different lifecycle and their
entries were never removed from `reflectedObjects`.

### The PythonDerived lifecycle

Python-derived objects created from .NET get a "phantom reference" (an
intentionally leaked `NewReference` from `InvokeCtor`) that keeps Python
`ob_refcnt ≥ 1` so the GCHandle stays strong and the .NET side can call
Python methods at any time.  This means:

- `tp_dealloc` never fires for .NET-created PythonDerived objects
- The reflectedObjects entry stays forever
- Each plan execution creates new temporary wrappers → set grows indefinitely

### The second leak: `PythonTypeDataWrapper` round-trip

The stock `OpenTap.Python` `PythonTypeDataWrapper.CreateInstance()` did:

```csharp
var mem = innerType.CreateInstance(arguments);
var pyObj = Converter.ToPython(mem);          // creates wrapper → reflectedObjects entry
return pyObj.AsManagedObject(mem.GetType());  // creates ANOTHER wrapper
```

This was already fixed in v1 by returning the .NET object directly.

---

## 2. The Clean Fix: Source-Level `reflectedObjects` Lifecycle

Instead of the v1 `EvictCollectable()` workaround that periodically scanned
and drained the set, **v2 fixes the root cause** by making `ClassDerived`'s
existing lifecycle hooks properly maintain `reflectedObjects`.

### Design principle

Every code path that **adds** to `reflectedObjects` now has a corresponding
**removal** in the natural object lifecycle:

```
ADDS to reflectedObjects:
  CLRObject.Create()     — wrapping a .NET object for Python
  CLRObject.OnLoad()     — deserializing a pickled wrapper

REMOVES from reflectedObjects:
  ClassBase.tp_clear     — regular CLR wrappers (stock pythonnet, unchanged)
  ClassDerived.tp_dealloc — Python-derived objects when deallocated   [NEW]
  ClassDerived.Finalize   — safety net if dealloc path is bypassed   [NEW]

RE-ADDS to reflectedObjects:
  ClassDerived.ToPython  — resurrection after dealloc (weak→strong)  [NEW]
```

### The three edits to ClassDerived.cs

#### Edit 1: `tp_dealloc` — remove on deallocation

When a Python-derived wrapper is deallocated (refcount drops to 0), the entry
must be removed from `reflectedObjects` before the GCHandle is weakened:

```csharp
// ClassDerived.cs, tp_dealloc (after PyObject_GC_UnTrack, before GCHandle weakening)
CLRObject.reflectedObjects.Remove(ob.Borrow().DangerousGetAddress());
```

This covers Python-created PythonDerived objects that go through the normal
death→dealloc cycle.  .NET-created objects (InvokeCtor phantom reference)
never reach refcount 0, so this path doesn't fire for them — they're handled
by Edit 3 (Finalize).

#### Edit 2: `ToPython` — re-add on resurrection

When `ToPython` resurrects a previously-deallocated PythonDerived object
(refcount was 1, GCHandle was weakened, now it's needed again), the entry
must be re-added:

```csharp
// ClassDerived.cs, ToPython (inside the if-refcount==1 resurrection block)
CLRObject.reflectedObjects.Add(self.DangerousGetAddress());
```

This ensures the tracking set stays consistent through the
dealloc→resurrection cycle that Python-created PythonDerived objects go through.

#### Edit 3: `Finalize` — safety net for .NET-created objects

The `Finalize` method on PythonDerived objects runs when the .NET-side object
is garbage collected (after all references are gone and the GCHandle has been
freed or weakened).  This is the **only** removal path for .NET-created
PythonDerived objects whose phantom reference kept `tp_dealloc` from firing:

```csharp
// ClassDerived.cs, Finalize (at the top, before any other work)
CLRObject.reflectedObjects.Remove(derived);
```

### Why this works

| Object type                             | Lifecycle                                                          | Removal path                                            |
| --------------------------------------- | ------------------------------------------------------------------ | ------------------------------------------------------- |
| Regular CLR wrappers                    | `Create → use → refcnt=0 → tp_dealloc → tp_clear`                  | `ClassBase.tp_clear` (stock)                            |
| PythonDerived from Python               | `Create → use → refcnt=0 → tp_dealloc`                             | Edit 1 (`tp_dealloc`)                                   |
| PythonDerived from Python (resurrected) | `tp_dealloc → ToPython → use → tp_dealloc`                         | Edit 1 (remove), Edit 2 (re-add), Edit 1 (remove again) |
| PythonDerived from .NET                 | `InvokeCtor → phantom ref → never tp_dealloc → .NET GC → Finalize` | Edit 3 (`Finalize`)                                     |

### What was removed

The entire `EvictCollectable()` mechanism and `EvictResult` struct in
`ClrObject.cs` (~220 lines) were deleted.  `Runtime.EvictReflectedObjects()`
was replaced with a simple diagnostic property:

```csharp
public static int ReflectedObjectCount => CLRObject.ReflectedObjectCount;
```

---

## 3. The Phantom Reference — Why PythonDerived Objects Are Special

### The phantom reference trap (v1 lesson learned)

In v1, an earlier iteration tried to `Py_DecRef` the phantom reference on
PythonDerived objects to let Python GC collect them.  This was **catastrophic**
when reference cycles were involved.

Every Python base class in `opentap.py` creates a cycle in `__init__`:

```python
class PyResultListener(OpenTap.ResultListener):
    def __init__(self):
        super().__init__()
        self.log = Trace(self)    # Trace stores self.resource = resource
```

```
ResultListener (RL)  ──self.log──▶  Trace (T)
        ▲                              │
        └──────── T.resource ──────────┘
```

With the phantom reference:
```
RL: ob_refcnt = 2  (1 phantom + 1 cycle-internal)
    Provisional GC refcount: 2 - 1 = 1 > 0 → REACHABLE ✓
```

Without the phantom reference (after Py_DecRef):
```
RL: ob_refcnt = 1  (1 cycle-internal only)
    Provisional GC refcount: 1 - 1 = 0 → GARBAGE ✗
```

Python's cyclic GC would collect the entire cycle, freeing the Python wrapper.
Later calls from .NET to the Python method overrides would access freed memory
→ segfault or silent data corruption.

**The v2 fix never touches the phantom reference.** It only manages the
`reflectedObjects` tracking set entry, which has no effect on Python refcounting
or GC reachability.

---

## 4. PythonMemoryMonitor (Simplified)

### What changed from v1

| v1 (6-phase)                                      | v2 (5-phase)                                      |
| ------------------------------------------------- | ------------------------------------------------- |
| Phase 1: `EvictReflectedObjects(long.MaxValue)`   | **Removed** — no longer needed                    |
| Phase 2: Drain Finalizer queue                    | Phase 1: Drain Finalizer queue                    |
| Phase 3: Python gc.collect × 3 + clear_type_cache | Phase 2: Python gc.collect × 3 + clear_type_cache |
| Phase 4: .NET GC.Collect                          | Phase 3: .NET GC.Collect (outside GIL)            |
| Phase 5: Drain Finalizer again                    | Phase 4: Drain Finalizer again                    |
| Phase 6: malloc_trim on Linux                     | Phase 5: malloc_trim on Linux                     |

### The 5-phase cleanup cycle

| Phase | GIL? | Action                                                                     | Purpose                                                              |
| ----- | ---- | -------------------------------------------------------------------------- | -------------------------------------------------------------------- |
| 1     | Yes  | `Finalizer.Instance.Collect()`                                             | Flush deferred `Py_DecRef` from .NET finalizers                      |
| 2     | Yes  | `gc.collect()` × 3 + `sys._clear_type_cache()`                             | Reclaim unreachable Python objects across all generations            |
| 3     | No   | `GC.Collect(2, Forced, Blocking, Compacting)` + `WaitForPendingFinalizers` | Reclaim dead .NET CLR wrappers, queue their Py_DecRef                |
| 4     | Yes  | `Finalizer.Instance.Collect()` again                                       | Flush the Py_DecRef calls queued by Phase 3                          |
| 5     | Yes  | `malloc_trim(0)` (Linux only)                                              | Return freed native pages to OS (combats glibc malloc fragmentation) |

### malloc fragmentation — the real resident memory growth

On ARM64 Linux (Raspberry Pi), the **primary cause** of observed RSS growth is
glibc's `malloc` arena fragmentation, not leaked managed objects.  When Python
and .NET allocate/free many small objects, `malloc` holds onto freed pages
rather than returning them to the OS.

Phase 5's `malloc_trim(0)` forces glibc to release free pages back to the OS.
This is the single most impactful action for keeping RSS stable on ARM64.

### Lifecycle

```
EnterTestPlanRun
  │
  │  ┌─── cleanup thread ───────────────────────┐
  │  │  sleep(interval)                          │
  │  │  RunCleanupCycle("PERIODIC-1")            │
  │  │  sleep(interval)                          │
  │  │  RunCleanupCycle("PERIODIC-2")            │
  │  │  ...                                      │
  │  └───────────────────────────────────────────┘
  │
ExitTestPlanRun
  ├── signal stop → join thread
  └── RunCleanupCycle("EXIT-FINAL")
```

---

## 5. Why a Dedicated Thread Instead of `System.Threading.Timer`

The cleanup must acquire the **Python GIL** (Global Interpreter Lock) before
touching any Python objects.  If a Python step is executing, the GIL is held by
that step for its entire duration (potentially seconds to minutes).

When we used `System.Threading.Timer` (backed by the .NET ThreadPool):

1. Timer callback fires on a ThreadPool thread.
2. Callback calls `Py.GIL()` → blocks waiting for the Python step to finish.
3. .NET's **hill-climbing algorithm** detects the pool thread is "stuck" and
   injects a new thread to maintain throughput.
4. Next timer tick fires on a new pool thread → also blocks on GIL.
5. Hill-climbing injects another thread.
6. Repeat every interval for the entire plan duration.

On a Raspberry Pi with 10-second loop iterations and 30-second cleanup
intervals, this led to **8,000+ leaked ThreadPool threads** in a few hours.

A dedicated `IsBackground = true` thread with `ThreadPriority.BelowNormal`
avoids the ThreadPool entirely.  It blocks on the GIL, runs cleanup, goes back
to sleep — one thread, no accumulation.

---

## 6. Additional pythonnet Fixes (Carried Forward from v1)

### 6a. `InvokeCtor` — Python `__init__` for .NET-created objects

**Problem**: When OpenTAP's XML deserializer or `TypeData.CreateInstance()`
creates a Python-derived object from .NET, the original `InvokeCtor` only
called the base CLR constructor.  Python `__init__` was **never called**.

**Fix**: After the base constructor returns, explicitly call `__init__` via
the Python API.

### 6b. `tp_hash` — fallback for weakened GCHandles

**Problem**: After `tp_dealloc` downgrades a GCHandle to Weak,
`GetManagedObject()` returns `null`.  The default `tp_hash` tried to call
`.GetHashCode()` on null → `TypeError: unhashable type`.

**Fix**: Fall back to pointer-based hash instead of raising.

### 6c. `clr.property` — id-based instance storage

**Problem**: `clr.property` used `WeakKeyDictionary` to store per-instance
property values, which hashes keys via `tp_hash` → broken for weakened handles.

**Fix**: Switch to `dict` keyed by `id(instance)`.

### 6d. `PythonTypeDataWrapper.CreateInstance()` — skip round-trip

**Problem**: The `ToPython → AsManagedObject` round-trip created two tracked
entries per `CreateInstance()` call.

**Fix**: Return the .NET object directly.

---

## 7. Memory Profile (Before vs. After)

Measured on Raspberry Pi 4 (4 GB RAM), infinite-loop plan with 1 Python delay
step, 30-second cleanup interval:

| Metric                             | Before fix                            | After v2 fix                         |
| ---------------------------------- | ------------------------------------- | ------------------------------------ |
| `reflectedObjects` growth per hour | ~50,000 entries                       | ~240 (bounded, ~2/run)               |
| .NET heap growth per hour          | ~150 MB                               | 0 (stable, negative delta per cycle) |
| Process RSS after 8 hours          | > 2 GB (OOM killed)                   | ~350 MB (stable)                     |
| ThreadPool threads after 8 hours   | 8,000+ (Timer-based)                  | 1 dedicated thread                   |
| Cleanup mechanism                  | `EvictCollectable()` brute-force scan | Natural pythonnet lifecycle          |

### Test results (tap python test-memory)

```
Test 1: Result listener integration — PASS (3/3 OnTestPlanRunCompleted calls)
Test 2: reflectedObjects stability — PASS (growth 20 over 13 runs, threshold 50)
Test 3: Step method integrity     — PASS (20 steps after GC pressure all callable)
Test 4: Finalizer drain           — PASS (no errors)
```

Heap delta per cleanup cycle: **-1.1 to -1.7 MB** (memory actively being freed).

---

## 8. File Map

### pythonnet (fork)

| File                                | v2 Changes                                                                                                                   |
| ----------------------------------- | ---------------------------------------------------------------------------------------------------------------------------- |
| `src/runtime/Types/ClassDerived.cs` | 3 edits: `tp_dealloc` remove, `ToPython` re-add, `Finalize` safety-net remove                                                |
| `src/runtime/Types/ClrObject.cs`    | Removed `EvictCollectable`/`EvictResult` (~220 lines). Added `ReflectedObjectCount` property. Added lifecycle documentation. |
| `src/runtime/Runtime.cs`            | Replaced `EvictReflectedObjects()` with `ReflectedObjectCount` diagnostic property                                           |

### OpenTap.Python (this repo)

| File                                         | v2 Changes                                                               |
| -------------------------------------------- | ------------------------------------------------------------------------ |
| `OpenTap.Python/PythonMemoryMonitor.cs`      | Simplified from 6-phase to 5-phase. Removed EvictReflectedObjects phase. |
| `OpenTap.Python/PythonTypeDataWrapper.cs`    | Updated stale comment referencing EvictCollectable.                      |
| `OpenTap.Python.UnitTests/MemoryLeakTest.cs` | New CLI action test (`tap python test-memory`) with 4 test sections.     |

---

## 9. Building & Deploying

```bash
# 1. Build pythonnet (produces Python.Runtime.dll)
cd pythonnet
dotnet build src/runtime/Python.Runtime.csproj -c Release
# Output: build_out/Python.Runtime.dll

# 2. Copy Python.Runtime.dll to OpenTap.Python's dependency path
cp build_out/Python.Runtime.dll ../OpenTap.Python/Python.Dependencies/Python.Runtime.dll

# 3. Build OpenTap.Python (produces OpenTap.Python.dll)
cd ../OpenTap.Python
dotnet build OpenTap.Python/OpenTap.Python.csproj -c Release
# Output: bin/Release/OpenTap.Python.dll

# 4. Build tests
dotnet build OpenTap.Python.UnitTests/OpenTap.Python.UnitTests.csproj -c Release

# 5. Deploy to target (e.g., Raspberry Pi)
# Python.Runtime.dll goes to THREE locations:
scp build_out/Python.Runtime.dll pi@<host>:~/.local/share/opentap/Packages/Python/
scp build_out/Python.Runtime.dll pi@<host>:~/.local/share/opentap/Dependencies/Python.Runtime.3.0.5.0/
scp build_out/Python.Runtime.dll pi@<host>:~/.local/share/opentap/Dependencies/OpenTap.Python.3.2.1.0/

# OpenTap.Python.dll goes to THREE locations:
scp bin/Release/OpenTap.Python.dll pi@<host>:~/.local/share/opentap/Packages/Python/
scp bin/Release/OpenTap.Python.dll pi@<host>:~/.local/share/opentap/Dependencies/Python.Runtime.3.0.5.0/
scp bin/Release/OpenTap.Python.dll pi@<host>:~/.local/share/opentap/Dependencies/OpenTap.Python.3.2.1.0/

# Test DLL (optional, for running tap python test-memory):
scp bin/Release/OpenTap.Python.UnitTests.dll pi@<host>:~/.local/share/opentap/Packages/Python/

# 6. Restart the runner
ssh pi@<host> "pkill -f 'tap.dll runner'; sleep 3; \
  nohup dotnet ~/.local/share/opentap/tap.dll runner start --listen 20111 &"
```

---

## 10. Running the Memory Test

```bash
# On the Pi (or any machine with the DLLs deployed):
cd /home/pi/.local/share/opentap
./tap python test-memory

# Expected output:
# === Memory Leak Test ===
# --- Test 1: Result listener OnTestPlanRunCompleted --- OK
# --- Test 2: reflectedObjects growth --- OK
# --- Test 3: Step method integrity --- OK
# --- Test 4: Finalizer queue drain --- OK
# === All memory leak tests passed ===
```

The test requires a Python step module deployed as a proper Python package
(directory with `__init__.py` + step `.py` file) in a directory scanned by
`PythonPluginProvider`. The default search paths include:
- The OpenTAP installation directory
- `Packages/Python/`
- `Packages/`

---

## 11. Version History

| Version          | Approach                                                                                                                                           | Drawbacks                                                                                                                            |
| ---------------- | -------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------ |
| **v1**           | `EvictCollectable()` brute-force: periodic scan of all reflectedObjects, classify by refcount, free/untrack based on type                          | Complex (220 lines), fragile classification heuristics, risk of freeing infrastructure objects, required careful refcount thresholds |
| **v2** (current) | Source-level fix: 3 small edits to ClassDerived.cs so the natural `tp_dealloc`/`ToPython`/`Finalize` lifecycle properly maintains reflectedObjects | Clean, minimal, follows pythonnet's existing patterns. No scanning, no classification, no refcount inspection.                       |
