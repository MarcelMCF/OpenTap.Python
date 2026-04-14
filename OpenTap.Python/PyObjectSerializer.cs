using System;
using System.Xml.Linq;
using Python.Runtime;

namespace OpenTap.Python;

/// <summary>
/// Uses Python pickle to serialize any PyObject to base64 and back.
/// This might break in some cases, but it is the best guess we can do.
/// This is for example used for enums.
/// </summary>
public class PyObjectSerializer : ITapSerializerPlugin
{
    // Cache module imports to avoid leaking a new PyObject handle per call.
    static PyObject _pickle;
    static PyObject _codecs;

    static PyObject GetPickle() => _pickle ??= Py.Import("pickle");
    static PyObject GetCodecs() => _codecs ??= Py.Import("codecs");

    public bool Deserialize(XElement node, ITypeData t, Action<object> setter)
    {
        if (t.DescendsTo(typeof(PyObject)))
        {
            using (Py.GIL())
            {
                var pickle = GetPickle();
                var codecs = GetCodecs();

                // pickle.loads(codecs.decode(node.Value.encode(), "base64"))
                using var nodeValuePy = node.Value.ToPython();
                using var encoded = nodeValuePy.InvokeMethod("encode");
                using var base64Py = "base64".ToPython();
                using var decoded = codecs.InvokeMethod("decode", encoded, base64Py);
                var val = pickle.InvokeMethod("loads", decoded);

                setter(val);
            }

            return true;
        }
        return false;
    }

    public bool Serialize(XElement node, object obj, ITypeData expectedType)
    {
        if (obj is PyObject py)
        {
            using (Py.GIL())
            {
                var pickle = GetPickle();
                var codecs = GetCodecs();

                // codecs.encode(pickle.dumps(obj), "base64").decode()
                using var zeroPy = 0.ToPython();
                using var dumped = pickle.InvokeMethod("dumps", py, zeroPy);
                using var base64Py = "base64".ToPython();
                using var encodedBytes = codecs.InvokeMethod("encode", dumped, base64Py);
                using var decodedStr = encodedBytes.InvokeMethod("decode");
                node.SetValue(decodedStr);
            }
        }

        return false;
    }

    public double Order { get; }
}