using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AutoCADMCP.CommandSet.Interfaces;

namespace AutoCADMCP.Plugin
{
    /// <summary>
    /// Reflection-based command registry. Walks every assembly that's been
    /// loaded into AutoCAD's domain, finds every concrete class implementing
    /// ICadCommand, and registers it by Name. Adding a new command = adding
    /// one C# file to autocad/commandset/Commands/.
    /// </summary>
    public class CommandDispatcher
    {
        private readonly Dictionary<string, ICadCommand> _commands = new(StringComparer.Ordinal);

        public CommandDispatcher() { DiscoverCommands(); }

        public ICadCommand GetCommand(string name)
        {
            if (_commands.TryGetValue(name, out var cmd)) return cmd;
            throw new ArgumentException($"Unknown command '{name}'.");
        }

        public bool HasCommand(string name) => _commands.ContainsKey(name);

        public IEnumerable<string> GetCommandNames() => _commands.Keys;

        private void DiscoverCommands()
        {
            // Look in the CommandSet assembly + this plugin assembly. Both
            // ship in the same autoloader bundle / NETLOAD'd folder, so they
            // are both already loaded when AcadMCPApp.Initialize() runs.
            var assemblies = new[]
            {
                Assembly.GetExecutingAssembly(),
                typeof(ICadCommand).Assembly,
            };

            foreach (var asm in assemblies.Distinct())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types?.Where(t => t != null).ToArray() ?? Array.Empty<Type>();
                }

                foreach (var t in types)
                {
                    if (t == null || t.IsAbstract || t.IsInterface) continue;
                    if (!typeof(ICadCommand).IsAssignableFrom(t)) continue;

                    try
                    {
                        var instance = (ICadCommand)Activator.CreateInstance(t);
                        if (!string.IsNullOrEmpty(instance.Name))
                            _commands[instance.Name] = instance;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[AutoCADMCP] Failed to register {t.FullName}: {ex.Message}");
                    }
                }
            }

            System.Diagnostics.Debug.WriteLine(
                $"[AutoCADMCP] Discovered {_commands.Count} command(s): {string.Join(", ", _commands.Keys)}");
        }
    }
}
