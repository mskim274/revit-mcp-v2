#if NET8_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.CommandSet.Interfaces;

namespace RevitMCP.Plugin.Services
{
    /// <summary>
    /// Collectible load context for one immutable CommandSet generation.
    /// Revit API and the host contract are deliberately shared from the
    /// default context so IRevitCommand remains cast-compatible.
    /// </summary>
    internal sealed class CommandSetLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;
        private readonly Dictionary<string, Assembly> _sharedAssemblies;

        public CommandSetLoadContext(string commandSetPath, string generation)
            : base($"RevitMCP.CommandSet:{generation}", isCollectible: true)
        {
            _resolver = new AssemblyDependencyResolver(commandSetPath);
            _sharedAssemblies = new Dictionary<string, Assembly>(
                StringComparer.OrdinalIgnoreCase)
            {
                [typeof(IRevitCommand).Assembly.GetName().Name] =
                    typeof(IRevitCommand).Assembly,
                [typeof(Document).Assembly.GetName().Name] =
                    typeof(Document).Assembly,
                [typeof(UIApplication).Assembly.GetName().Name] =
                    typeof(UIApplication).Assembly
            };
        }

        public Assembly LoadMainAssembly(string assemblyPath)
        {
            return LoadManagedAssembly(assemblyPath);
        }

        protected override Assembly Load(AssemblyName assemblyName)
        {
            if (_sharedAssemblies.TryGetValue(
                    assemblyName.Name ?? "",
                    out var sharedAssembly))
            {
                return sharedAssembly;
            }

            if (string.Equals(
                    assemblyName.Name,
                    "RevitMCPPlugin",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Reloadable CommandSet code must not reference the Revit MCP host assembly.");
            }

            var assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
            return assemblyPath == null
                ? null
                : LoadManagedAssembly(assemblyPath);
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            var libraryPath =
                _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return libraryPath == null
                ? IntPtr.Zero
                : LoadUnmanagedDllFromPath(libraryPath);
        }

        private Assembly LoadManagedAssembly(string assemblyPath)
        {
            using var assemblyStream = OpenReadWithoutLock(assemblyPath);
            var symbolPath = Path.ChangeExtension(assemblyPath, ".pdb");
            if (!File.Exists(symbolPath))
                return LoadFromStream(assemblyStream);

            using var symbolStream = OpenReadWithoutLock(symbolPath);
            return LoadFromStream(assemblyStream, symbolStream);
        }

        private static FileStream OpenReadWithoutLock(string path)
        {
            return new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
        }
    }
}
#endif
