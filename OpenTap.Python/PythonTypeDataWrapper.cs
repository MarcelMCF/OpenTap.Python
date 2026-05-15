using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Python.Runtime;

namespace OpenTap.Python;

/// <summary>
/// This wrapper is mostly needed because of a problem in pythonnet requiring us to do ToPython.AsManagedObject after
/// creating an instance of the object.
/// </summary>
class PythonTypeDataWrapper : ITypeData
{
    /// <summary>
    /// Prevent .NET GC from collecting PyObjects returned by ToPython().
    /// Without this anchor, the PyObject goes out of scope after CreateInstance,
    /// gets finalized by .NET GC, which calls XDecref — if rc drops to 0,
    /// CPython's tp_dealloc clears __dict__ (losing Python-side attributes
    /// like self.log set during __init__).
    /// ConditionalWeakTable ties the PyObject lifetime to the .NET object:
    /// as long as OpenTAP holds the .NET step/instrument, the Python wrapper
    /// stays alive with rc >= 1.
    /// </summary>
    static readonly ConditionalWeakTable<object, PyObject> _aliveObjects = new();

    /// <summary>
    /// Parallel weak-reference list of keys currently in <see cref="_aliveObjects"/>.
    /// Required because <see cref="ConditionalWeakTable{TKey,TValue}"/> on
    /// netstandard2.0 does not expose enumeration.  Used by
    /// <see cref="RemoveAnchors"/> to walk the live keys.  Dead entries are
    /// pruned opportunistically during enumeration.
    /// </summary>
    static readonly List<WeakReference<object>> _trackedKeys = new();
    static readonly object _trackedKeysLock = new();

    /// <summary>
    /// Drops the anchoring <see cref="PyObject"/> for any entries in
    /// <see cref="_aliveObjects"/> whose .NET object satisfies
    /// <paramref name="predicate"/>.
    /// <para>
    /// This is required to let
    /// <see cref="Runtime.EvictAbandonedObjects(Func{object,bool})"/>
    /// actually free the Python wrapper:  the anchor here adds an INCREF, so
    /// the wrapper's <c>tp_dealloc</c> never fires while the entry is in
    /// place.  Removing the entry and disposing the <see cref="PyObject"/>
    /// drops that INCREF, breaking the
    /// <c>step → CWT entry → PyObject → Python wrapper → GCHandle → step</c>
    /// reference cycle.
    /// </para>
    /// <para><b>Must be called with the Python GIL held.</b></para>
    /// </summary>
    /// <param name="predicate">Returns <c>true</c> for objects whose anchor
    /// should be released.  Typical usage: <c>inst is ITestStep s &amp;&amp;
    /// s.Parent == null</c>.</param>
    /// <returns>The number of anchors released.</returns>
    public static int RemoveAnchors(Func<object, bool> predicate)
    {
        if (predicate == null) throw new ArgumentNullException(nameof(predicate));

        int released = 0;
        lock (_trackedKeysLock)
        {
            for (int i = _trackedKeys.Count - 1; i >= 0; i--)
            {
                if (!_trackedKeys[i].TryGetTarget(out var key))
                {
                    // Key was collected — prune the dead weak-ref.
                    _trackedKeys.RemoveAt(i);
                    continue;
                }

                bool matches;
                try { matches = predicate(key); }
                catch { matches = false; }

                if (!matches) continue;

                if (_aliveObjects.TryGetValue(key, out var py))
                {
                    _aliveObjects.Remove(key);
                    try { py.Dispose(); } // releases the INCREF on the Python wrapper
                    catch { /* best-effort */ }
                }
                _trackedKeys.RemoveAt(i);
                released++;
            }
        }
        return released;
    }

    readonly TypeData innerType;
    public PythonTypeDataWrapper(TypeData innerType) => this.innerType = innerType;
    public IEnumerable<object> Attributes => innerType.Attributes;
    public string Name => innerType.Name;
    public IEnumerable<IMemberData> GetMembers() => innerType.GetMembers();

    public IMemberData GetMember(string name) => innerType.GetMember(name);

    public object CreateInstance(object[] arguments)
    {
        var mem = innerType.CreateInstance(arguments);

        using (Py.GIL())
        {
            try
            {
                // ToPython resurrects the Python wrapper (whose refcount was
                // dropped to 0 by InvokeCtor's phantomRef.Dispose()).
                var py = mem.ToPython();

                var result = py.AsManagedObject(typeof(object));

                // Anchor the PyObject so .NET GC cannot finalize it (and
                // XDecref / tp_dealloc the Python wrapper) while the .NET
                // object is still alive.
                _aliveObjects.Remove(result);
                _aliveObjects.Add(result, py);

                // Track the key in a parallel weak-ref list so RemoveAnchors
                // can enumerate live keys (CWT itself is not enumerable on
                // netstandard2.0).
                lock (_trackedKeysLock)
                {
                    _trackedKeys.Add(new WeakReference<object>(result));
                }

                return result;
            }
            catch (Exception)
            {
                return mem;
            }
        }
    }

    public ITypeData BaseType => innerType;
    public bool CanCreateInstance => innerType.CanCreateInstance;

    public override int GetHashCode()
    {
        return innerType.GetHashCode() * 73210693;
    }

    public override bool Equals(object obj)
    {
        if (obj is PythonTypeDataWrapper pw && pw.innerType == innerType)
            return true;
        return base.Equals(obj);
    }

    public override string ToString()
    {
        return innerType.ToString();
    }
}