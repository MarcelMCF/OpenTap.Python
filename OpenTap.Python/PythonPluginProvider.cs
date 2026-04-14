using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using OpenTap.Package;
using Python.Runtime;

namespace OpenTap.Python
{
    [PluginOrder(typeof(DotNetTypeDataSearcher))]
    public class PythonPluginProvider : ITypeDataSearcher, ITypeDataProvider, ITypeDataSourceProvider
    {
        class PythonTypeDataSource : ITypeDataSource
        {
            public string Name { get; }
            public string Location { get; }
            public IEnumerable<ITypeData> Types => DiscoveredTypes.Values;
            public IEnumerable<object> Attributes => Array.Empty<object>();

            public IEnumerable<ITypeDataSource> References => new ITypeDataSource[]
                {TypeData.GetTypeDataSource(TypeData.FromType(typeof(PythonTypeDataSource)))};

            public string Version { get; }
            public Dictionary<string, TypeData> DiscoveredTypes { get; } = new Dictionary<string, TypeData>();

            public PythonTypeDataSource(string name, string location)
            {
                Name = name;
                Location = location;
                Version = Installation.Current.FindPackageContainingFile(Location)?.Version?.ToString() ?? "0.1";
            }
        }

        public PythonPluginProvider()
        {
            if (!PythonEngine.IsInitialized) TapThread.Start(() => PythonInitializer.LoadPython());
        }

        static readonly TraceSource log = Log.CreateSource("Python");

        public void Search()
        {
            // ensure that it's loaded.
            PythonInitializer.LoadPython();
            if (!PythonEngine.IsInitialized) return; //something went wrong and python is not initialized.
            using (Py.GIL())
            {
                var types = new List<TypeData>();

                var modules = new List<string>();

                foreach (var sp in PythonSettings.Current.GetSearchList())
                {
                    if (Directory.Exists(sp) == false) continue;
                    var mod2 = Directory.EnumerateDirectories(sp, "*", SearchOption.TopDirectoryOnly)
                        .Where(dir => Directory.EnumerateFiles(dir, "*.py").Any()).ToList();
                    modules.AddRange(mod2);
                }
                if(modules.Any())
                {
                    using var opentapModule = Py.Import("opentap");
                }

                var sources = new Dictionary<string, PythonTypeDataSource>();

                var visitedTypes2 = new HashSet<PyType>();
                try
                {
                    foreach (var file in modules)
                    {
                        var baseModule = Path.GetFileNameWithoutExtension(file);

                        try
                        {

                            var pyFiles = Directory.EnumerateFiles(file, "*.py");
                            foreach (var py in pyFiles)
                            {

                                var subName = Path.GetFileNameWithoutExtension(py);
                                var moduleName = baseModule;
                                if (subName != "__init__")
                                {
                                    moduleName = baseModule + "." + subName;
                                }

                                if (moduleName == "Python.opentap")
                                    continue;

                                log.Debug("Loading: {0}", moduleName);

                                PyObject module;
                                try
                                {
                                    module = Py.Import(moduleName);
                                }
                                catch (Exception e)
                                {
                                    log.Error("Caught exception loading {0}: {1}", moduleName, e.Message);
                                    log.Debug(e);
                                    continue;
                                }

                                try
                                {
                                    using var files = module.GetAttr("__dict__");
                                    using var objValues = new PyDict(files);
                                    var values = objValues.Values().ToArray();

                                    try
                                    {
                                        for (int i = 0; i < values.Length; i++)
                                        {
                                            var _item = values[i];
                                            using var itemType = _item.GetPythonType();
                                            var name = itemType.Name;

                                            if (name != "CLRMetatype") continue;

                                            var pyType = new PyType(_item);
                                            Type type;
                                            try
                                            {
                                                type = (Type)_item.AsManagedObject(typeof(Type));
                                            }
                                            catch (Exception ex)
                                            {
                                                log.Warning("AsManagedObject failed for type in {0}: {1}", moduleName, ex.Message);
                                                pyType.Dispose();
                                                continue;
                                            }

                                            if (!type.Assembly.IsDynamic)
                                            {
                                                pyType.Dispose();
                                                continue;
                                            }

                                            if (!pyType.HasAttr("__module__"))
                                            {
                                                pyType.Dispose();
                                                continue;
                                            }

                                            using var modAttr = pyType.GetAttr("__module__");
                                            var mod = modAttr.As<string>();
                                            if (!sources.TryGetValue(mod, out var asm))
                                            {
                                                asm = sources[mod] = new PythonTypeDataSource(mod, py);
                                            }

                                            var td = TypeData.FromType(type);
                                            if (visitedTypes2.Add(pyType))
                                                types.Add(td);
                                            else
                                                pyType.Dispose();
                                            asm.DiscoveredTypes[td.Name] = td;
                                        }
                                    }
                                    finally
                                    {
                                        foreach (var v in values)
                                            v.Dispose();
                                    }
                                }
                                finally
                                {
                                    module.Dispose();
                                }
                            }
                        }
                        catch (Exception e)
                        {
                            log.Error("Caught exception loading {0}: {1}", file, e.Message);
                            log.Debug(e);
                        }
                    }
                }
                finally
                {
                    foreach (var pt in visitedTypes2)
                        pt.Dispose();
                }

                // Build the types dictionary, skipping duplicate names (can happen when
                // multiple Python modules import the same base class like PyTestStep).
                var typesDict = new Dictionary<string, PythonTypeDataWrapper>();
                foreach (var td in types)
                {
                    if (typesDict.ContainsKey(td.Name))
                    {
                        log.Debug("Skipping duplicate Python type: {0}", td.Name);
                        continue;
                    }
                    typesDict[td.Name] = new PythonTypeDataWrapper(td);
                }
                PythonPluginProvider.types = typesDict.ToImmutableDictionary();
                
                var sourcesDict = new Dictionary<PythonTypeDataWrapper, PythonTypeDataSource>();
                foreach (var src in sources.Values)
                {
                    foreach (var kv in src.DiscoveredTypes)
                    {
                        var wrapper = new PythonTypeDataWrapper(kv.Value);
                        if (!sourcesDict.ContainsKey(wrapper))
                            sourcesDict[wrapper] = src;
                    }
                }
                PythonPluginProvider.typeDataSources = sourcesDict.ToImmutableDictionary();
                
                log.Debug("Python type search complete: {0} unique types registered", typesDict.Count);
            }
        }

        static ImmutableDictionary<PythonTypeDataWrapper, PythonTypeDataSource> typeDataSources =
            ImmutableDictionary<PythonTypeDataWrapper, PythonTypeDataSource>.Empty;

        static ImmutableDictionary<string, PythonTypeDataWrapper> types =
            ImmutableDictionary<string, PythonTypeDataWrapper>.Empty;

        public IEnumerable<ITypeData> Types => types.Values;

        public ITypeDataSource GetSource(ITypeData typeData)
        {
            while (!(typeData is TypeData) && typeData != null)
                typeData = typeData.BaseType;

            if (typeData is TypeData td && td.Type.Assembly.IsDynamic &&
                typeDataSources.TryGetValue(new PythonTypeDataWrapper(td), out var src))
                return src;


            return null;
        }

        public ITypeData GetTypeData(string identifier) => types.GetValueOrDefault(identifier);

        public ITypeData GetTypeData(object obj)
        {
            return null;
        }

        public double Priority => 1.0;
    }
}