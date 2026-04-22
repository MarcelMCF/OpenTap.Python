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