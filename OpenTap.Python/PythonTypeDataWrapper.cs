using System.Collections.Generic;
using Python.Runtime;

namespace OpenTap.Python;

/// <summary>
/// This wrapper is mostly needed because of a problem in pythonnet requiring us to do ToPython.AsManagedObject after
/// creating an instance of the object.
/// </summary>
class PythonTypeDataWrapper : ITypeData
{
    readonly TypeData innerType;
    public PythonTypeDataWrapper(TypeData innerType) => this.innerType = innerType;
    public IEnumerable<object> Attributes => innerType.Attributes;
    public string Name => innerType.Name;
    public IEnumerable<IMemberData> GetMembers() => innerType.GetMembers();

    public IMemberData GetMember(string name) => innerType.GetMember(name);

    public object CreateInstance(object[] arguments)
    {
        var mem = innerType.CreateInstance(arguments);
        // Previously doing ToPython and AsManagedObject retained a reference
        // because mem already is the correct instance.
        // We only do this wrapper to ensure properties are refreshed, 
        // but it leaks the python refcount due to pythonnet ReflectedObjects behavior.
        // By skipping the internal conversion back and forth unless explicitly necessary, 
        // or by explicitly releasing the underlying references, we can prevent this.

        using (Py.GIL())
        {
            // Just returning the unwrapped memory avoids the python object being pinned 
            // infinitely in EvictCollectable sets.
            return mem;
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