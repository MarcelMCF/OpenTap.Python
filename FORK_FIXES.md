# Pythonnet + OpenTap.Python — Fork Fixes

This document describes the issues that were uncovered while running Python-defined OpenTAP plugins under sustained load (long test-plan loops, ARM64 RevPi target, Python 3.11–3.13) and the changes that were made to the two forks to address them.

It is not a re-write of the upstream README — it tracks the **delta on top of upstream** as it appears in our git history of:

* [pythonnet fork](file:///c%3A/Users/marcel.volz/source/repos/pythonnet) — branched from upstream tag `v3.0.1`.
* [OpenTap.Python fork](file:///c%3A/Users/marcel.volz/source/repos/OpenTap.Python) — branched from upstream `3.2.1`.

## Commits referenced

### `pythonnet` (4 commits on top of upstream `42ee643`)

| SHA | Title |
|-----|-------|
| `4d55b01` | fix: memory leak + Python 3.11+ ABI + .NET attribute support |
| `4b2aa8d` | Port Keysight auto-namespace + handle pre-constructed attribute instances |
| `30a567b` | Fix Python step execution: tp_hash fallback, id-based property storage, InvokeCtor `__init__` |
| `73f7185` | "my fixes" — multi-interface inheritance, GC slot enforcement, lazy wrapper revival, lifetime anchoring |

### `OpenTap.Python` (3 commits on top of upstream `eb8f477`)

| SHA | Title |
|-----|-------|
| `779b3c1` | feat: add `PythonMemoryMonitor` + updated `Python.Runtime.dll` |
| `5b22d22` | Clean up `PythonPluginProvider`: duplicate-safe type dict, remove debug logs |
| `daa3bef` | "myfixes" — extended `PythonMemoryMonitor` (live-plan tracking, abandonment heuristic, dedicated cleanup thread) |

---

## 1. The unbounded `reflectedObjects` leak

**Root cause.** Pythonnet keeps a process-global `HashSet<IntPtr> CLRObject.reflectedObjects` that records every CLR-backed `PyObject` ever handed out. Entries are inserted from two paths:

* `CLRObject.GetReference(obj, type)` — used whenever managed code crosses into Python, including every `__init__` of a Python-derived OpenTAP step.
* `ToPython()` — used for ad-hoc managed → Python conversions.

The set is **never trimmed**. When the Python wrapper is collected by the Python GC the corresponding `IntPtr` becomes stale but stays in the hash set. Worse, the wrapper's `__dict__` (which holds attribute setters' state on Python-derived steps) is anchored by the `CLRObject`'s own GC handle, so even after Python releases its reference the managed-side state survives until the end of the process.

In practice this means:

* Every `TestPlan.Execute()` that contains a Python step instantiates one `CLRObject` per step, all of which are pinned forever.
* Long soak tests on the RevPi grew the .NET working set by tens of MB per hour and eventually OOMed.
* `GC.Collect(GC.MaxGeneration, …, blocking: true, compacting: true)` did **not** reclaim the memory because the entries are reachable from the static hash set.

**The fix (`pythonnet@4d55b01`, `73f7185`).**

* `CLRObject.EvictAbandonedObjects(Func<object,bool>? isAbandoned)` — a 2-pass sweep over a snapshot of `reflectedObjects`:
  * **Pass 1** removes Python-created phantoms (refcount ≤ 1, `__dict__` already cleared, GC-handle invalid).
  * **Pass 2** optionally evicts entries whose underlying CLR object satisfies a caller-provided "abandoned" predicate (e.g. *step belongs to a no-longer-running TestPlan*). Pass 2 deliberately skips entries that were already at `rc == 1` before pass 1 to avoid touching live objects whose owners are mid-call.
* `Runtime.EvictAbandonedObjects(Func<object,bool>?)` — public re-export so external assemblies (the OpenTAP plugin) can call it without referencing internals.
* In `Types/ClrObject.cs`, the snapshot/eviction logic is wrapped in `lock(reflectedObjects)` to prevent races with concurrent `GetReference` calls under the GIL.

**The driver (`OpenTap.Python@779b3c1`, `daa3bef`).** A new `PythonMemoryMonitor : ComponentSettings, ITestPlanRunMonitor` does the actual eviction:

* On `EnterTestPlanRun` it captures the live `TestPlan` in a static weak-referenced set and starts a dedicated **`IsBackground = true` cleanup thread** (not a `ThreadPool` work item — under sustained GIL contention the pool would otherwise spawn a new thread per stuck callback and we observed 8 000+ leaked threads on ARM64).
* On a configurable interval (default 30 s) the cleanup thread acquires the GIL, drains the pythonnet finalizer queue, then calls `Runtime.EvictAbandonedObjects(IsAbandoned)`.
* `IsAbandoned(object)` walks the `ITestStepParent.Parent` chain (bounded to 64 hops to defend against accidental cycles); if it ends up at a `TestPlan` that is **not** in the live set — or runs out of parents without ever finding one — the wrapper is fair game for eviction.
* On `ExitTestPlanRun` the live-plan entry is removed and the cleanup thread is signalled to stop via `ManualResetEventSlim`.

### Empirical demonstration

The repro in [`mem_leak_repro/`](file:///c%3A/Users/marcel.volz/source/repos/OpenTap.Python/mem_leak_repro/) executes 50 plans × 50 Python steps each, calling `GC.GetTotalMemory(forceFullCollection: true)` between iterations:

| Build (Windows x64, Python 3.11) | Growth over 50 × 50 steps |
|---|---|
| Unpatched (`EvictAbandonedObjects` neutralised) | **+39.22 MB** (~16 KB / step instance) |
| Forked (v2) | **+1.05 MB** (~400 B / step instance) |

≈ 37× reduction; the residual ~400 B/step is .NET-heap creep that is dominated on Linux/ARM64 by `malloc_trim` (see `MEMORY_FIX_README.md`).

---

## 2. Python-derived steps crashed on creation paths that don't go through `TestStep()` Python ctor

This wasn't a leak but it manifested **alongside** the leak, because the same code path that prevents tp_dealloc from clearing `__dict__` is also what keeps Python attribute state alive.

**Root cause.** Three independent symptoms that all trace back to the wrapper's lifecycle being broken by serializer- or framework-instantiated objects:

1. `'DischargeStep' object has no attribute 'log'` after a few runs — `tp_dealloc` weakened the `GCHandle` and cleared the `__dict__` while .NET still held the object.
2. `Unable to read Boolean. Instance must be specified when setting a property: set_LoadFirst(Boolean)` — the OpenTAP XML deserializer creates settings instances via `FormatterServices.GetUninitializedObject`, so the IL ctor never runs and `__pyobj__` is `null` when the XML setter fires.
3. `unhashable type: 'CLRMetatype'` from `clr.property` — its `WeakKeyDictionary` was hashing Python objects whose backing `GCHandle` was already collected.

**Fixes (`pythonnet@30a567b`):**

* **`ClassBase.tp_hash`**: when `GetManagedObject` returns null, fall back to a pointer-based hash instead of throwing `TypeError("unhashable type")`.
* **`clr.property`**: switched from `WeakKeyDictionary` to `id(instance)`-keyed dict so the lookup never has to hash a half-dead wrapper.
* **`ClassDerived.InvokeCtor`**: when an instance arrives via the .NET-construction path (XML deserialiser, factory, reflection), call the Python `__init__` after the base CLR ctor and do **not** dispose the `NewReference` from `CLRObject.GetReference` — keeping the refcount above zero is what stops `tp_dealloc` from weakening the GCHandle.

**Fix (`pythonnet@73f7185`).** `EnsurePythonWrapper(obj)` is invoked from `InvokeGetProperty<T>` / `InvokeSetProperty<T>` whenever `self.Ref == null`. It lazily reconstructs `__pyobj__` under the GIL via `CLRObject.GetReference(obj, obj.GetType())`, mirroring what `InvokeCtor` does on the .NET-construction path. Without this, anything that the OpenTAP XML deserialiser produces would throw `"Python object reference is null"` on the first property access.

**Plugin-side anchor (`OpenTap.Python` `PythonTypeDataWrapper.cs`).** A static `ConditionalWeakTable<object, PyObject> _aliveObjects` ties the lifetime of any `PyObject` produced by the plugin to its underlying .NET object — when the .NET object becomes unreachable, the table entry is collected too, so the eviction sweep above can actually reclaim the slot. Without this, the .NET GC would collect the `PyObject` while `reflectedObjects` still held its address, and the next `GetReference` for the same object would return a dangling pointer.

---

## 3. Multiple inheritance of CLR interfaces

**Root cause.** `MetaType.tp_new` rejected anything with more than one CLR base, raising `"cannot use multiple inheritance with managed classes"`. This blocked the canonical OpenTAP pattern of writing a Python class that implements both a marker (`OpenTap.ITapPlugin`) and a behaviour interface (`OpenTap.IVisa`).

**Fix (`pythonnet@73f7185`).** `MetaType` now collects extra interface types from the bases and threads them through `TypeManager` → `ReflectedClrType` → `ClassDerived.CreateDerivedType`. The latter:

* Adds the extra interfaces to the dynamic type.
* Iterates each interface's methods and adds virtual overrides.
* Forces the managed-subtype build path whenever `extraInterfaces.Length > 0` to avoid the `"multiple bases have instance lay-out conflict"` error from CPython's tp_new.

---

## 4. Generic virtual methods produced invalid IL on ARM64

**Root cause.** `ClassDerived.CreateDerivedType` walked every virtual method on the base and emitted an override stub. For generic-virtuals like `T[] OpenTap.ScpiInstrument.ScpiQueryBlock<T>(string)` it tried to emit a non-generic override using unresolved type parameters, producing IL that the ARM64 JIT rejected with `BadImageFormatException` (HRESULT 0x8007000B). Symptom: `class BasicScpiInstrument(OpenTap.ScpiInstrument): pass` failed at *class definition time* on the RevPi but worked on x64 (where the JIT was more permissive about the bogus IL).

**Fix (`pythonnet@73f7185`).** A single guard in the virtual-method loop:

```csharp
if (method.IsGenericMethodDefinition) { continue; }
```

Generic virtuals can't currently be overridden from Python anyway; skipping them is strictly better than emitting broken IL.

---

## 5. `ToPython()` aborted the process when the runtime was shutting down

**Root cause.** Inside `UnsafeReferenceWithRun.CheckRun()` the existing path was:

* `RawObj == IntPtr.Zero` → throw `RuntimeShutdownException(IntPtr.Zero)`.
* `RuntimeShutdownException`'s base ctor (`FinalizationException`) called `ArgumentNullException.ThrowIfNull` on the pointer and itself threw, which **bypassed** the `catch (RuntimeShutdownException)` filter further up the stack.
* Result: an unhandled exception in a finalizer thread → process abort.

**Fix (`pythonnet@73f7185`).**

* `CheckRun()` now checks `RawObj == IntPtr.Zero` first and throws `InvalidOperationException` instead of feeding zero into `FinalizationException`.
* `ToPython()` catches the broader `Exception when (...)` filter so that runtime-shutdown errors propagate cleanly and don't tear down the host.

---

## 6. `.NET attribute support` rough edges

Upstream commit `42ee643` introduced `__clr_attributes__` for declaring .NET attributes from Python, but only supported the form `@attribute(SomeAttribute, ctorArg1, ctorArg2)`. Real Python users want both forms and they want sensible namespaces.

**Fixes (`pythonnet@4d55b01`, `4b2aa8d`).**

* `clr.attribute` / `clr.property` are now full-fledged classes with `add_attribute()` helpers; both `_clr_attributes_` and `__clr_attributes__` naming conventions are accepted.
* `ClassDerived.GetAttributeBuilder` got a `BuildAttributeFromInstance` path: if the user passes a *pre-constructed* `Attribute` (e.g. `@attribute(OpenTap.Display("Name", "..."))`), it reverse-engineers a `CustomAttributeBuilder` by matching constructor parameters against instance-property values.
* `MetaType.tp_new` now creates derived CLR types for *any* base with a parameterless ctor (not just bases where the user explicitly set `__namespace__`/`__assembly__`).
* When `__namespace__` is missing, it is auto-derived from `__module__`, so a plugin Python file `ot_python_package/DelayStep.py` produces the CLR type `ot_python_package.DelayStep.DelayStep` instead of a synthetic `pyderived.DelayStep` that the OpenTAP type cache then can't find.
* `PythonTypeAttribute` gives the IL-generated `PyObject` properties a stable marker so `PyObjectAnnotator` and `PyObjectSerializer` can recognise them.

These changes together fix `Unable to locate type` errors that previously crashed plugin discovery for every Python-defined step, DUT or instrument.

---

## 7. Python 3.11–3.13 ABI compatibility

**Root cause.** CPython rearranged `PyTypeObject` slot offsets in 3.11, again in 3.12, and again in 3.13. Pythonnet hard-codes those offsets in `Native/TypeOffset3xx.cs` files. The upstream tables were missing or wrong for ≥3.11, so:

* Type allocation produced empty GC slots → segfault at first GC.
* `_PyThreadState_UncheckedGet` was removed in 3.13 → `MissingMethodException` at startup.
* `TypeManager.CreateMetatypeWithGCHandleOffset` left the GC slots blank.

**Fixes (`pythonnet@4d55b01`, `73f7185`).**

* New `Native/TypeOffset311.cs`, regenerated `TypeOffset312.cs`, `TypeOffset313.cs` with the correct offsets.
* `TypeManager` now installs `tp_traverse`/`tp_clear` defaults (commit `da082ac`'s "Enforce tp_traverse/clear" enforcement) so dynamically-created types participate in GC properly.
* `PythonException.TryDecodePyErr` is null-safe during early init, where the type cache is still being warmed.

For 3.13 specifically: pythonnet still binds the removed `_PyThreadState_UncheckedGet`, so we recommend **Python 3.11 or 3.12** with this build. The OpenTAP `Settings/Python.xml` ships pointing at a conda 3.11 environment for that reason.

---

## 8. Plugin-side discovery cleanup

`OpenTap.Python@5b22d22` rewrote `PythonPluginProvider.Search`:

* The previous `ToImmutableDictionary(td => td.Name, …)` threw on duplicate keys, which is exactly what happens when several Python modules each `from opentap import PyTestStep` — the same wrapper type appears in each module's `__dict__`. The new code uses a plain `Dictionary` and logs the duplicate at `Debug` level instead of crashing the whole search.
* `PyDict` keys/values and module objects are now disposed deterministically via `using`, fixing a slow PyObject leak in plugin discovery itself.
* The verbose per-type debug logging that was added during the leak investigation has been removed; only the duplicate-skip and completion summary remain.

---

## How to verify the fixes

```powershell
# Build the patched pythonnet
cd c:\Users\marcel.volz\source\repos\pythonnet
dotnet build src/runtime/Python.Runtime.csproj -c Release --no-incremental

# Drop into the plugin's bin/Release
Copy-Item src\runtime\obj\Release\Python.Runtime.dll `
  C:\Users\marcel.volz\source\repos\OpenTap.Python\bin\Release\Python.Runtime.dll -Force

# Run the leak repro (Python 3.11 env required, see Settings/Python.xml)
cd C:\Users\marcel.volz\source\repos\OpenTap.Python
dotnet build mem_leak_repro/mem_leak_repro.csproj -c Release
Copy-Item mem_leak_repro\leak_step.py bin\Release\Packages\PythonExamples\leak_step.py -Force
cd bin\Release; .\mem_leak_repro.exe
```

Expected output on the patched build:

```
Baseline:           14.71 MB
After run   5:    14.82 MB  (growth:   +0.10 MB)
…
After run  50:    15.76 MB  (growth:   +1.05 MB)
```

To compare against the unpatched behaviour, neutralize the eviction (early `return 0;` in `CLRObject.EvictAbandonedObjects`), rebuild, redeploy, and re-run — growth will climb to ~+39 MB over the same run.

---

## Deployment notes (RevPi)

The patched `Python.Runtime.dll` must be copied to **all three** locations on the device:

```
~/.local/share/opentap/Packages/Python/
~/.local/share/opentap/Dependencies/Python.Runtime.3.0.5.0/
~/.local/share/opentap/Dependencies/OpenTap.Python.3.2.1.0/
```

OpenTAP loads from each of those paths under different conditions; missing one will silently revert the dependent module to whatever stock build is found first.

There is one Pi-only Python source change: `Packages/PythonVisa/PyVisa.py` must not call `ComponentSettings.GetCurrent(PyVisaSettings)` from a class body — that triggers a deadlock between OpenTAP type loading and `ComponentSettings` initialization. Move the call into `__init__` (or any non-class-body location).
