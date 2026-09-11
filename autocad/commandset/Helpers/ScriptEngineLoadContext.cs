using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

namespace AutoCADMCP.CommandSet.Helpers
{
    /// <summary>
    /// Private compiler dependencies, shared host/API contracts. This is version
    /// isolation, NOT a security sandbox. Keep file locations for Roslyn metadata.
    /// One context lives for the AutoCAD process; no API objects are copied.
    /// </summary>
    internal sealed class ScriptEngineLoadContext : AssemblyLoadContext
    {
        private readonly string _directory;
        private readonly Dictionary<string, Assembly> _shared;

        public ScriptEngineLoadContext(string directory, params Assembly[] shared)
            : base("AutoCADMCP.ScriptEngine", isCollectible: false)
        {
            _directory = Path.GetFullPath(directory);
            _shared = shared.Distinct().ToDictionary(
                assembly => assembly.GetName().Name,
                StringComparer.OrdinalIgnoreCase);
        }

        protected override Assembly Load(AssemblyName name)
        {
            if (_shared.TryGetValue(name.Name, out var assembly))
                return assembly;

            // Never fall back to AutoCAD's older compiler if our dependency is
            // missing. The four compiler DLLs must come from this one bundle.
            if (name.Name == "AutoCADMCP.ScriptEngine" ||
                name.Name == "Microsoft.CodeAnalysis" ||
                name.Name == "Microsoft.CodeAnalysis.CSharp" ||
                name.Name == "Microsoft.CodeAnalysis.Scripting" ||
                name.Name == "Microsoft.CodeAnalysis.CSharp.Scripting")
            {
                var path = Path.Combine(_directory, name.Name + ".dll");
                if (!File.Exists(path))
                    throw new FileNotFoundException(
                        "AutoCAD MCP private script engine dependency is missing.", path);
                return LoadFromAssemblyPath(path);
            }

            // Runtime/framework and Autodesk dependencies retain host identity.
            return null;
        }
    }
}
