using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using RevitMCP.CommandSet.Interfaces;

namespace RevitMCP.Plugin
{
    /// <summary>
    /// Auto-discovers and dispatches commands via reflection.
    /// Adding a new command = adding a C# file to commandset/Commands/.
    /// No registration code needed.
    /// </summary>
    public class CommandDispatcher
    {
        private readonly Dictionary<string, IRevitCommand> _commands;

        public CommandDispatcher()
        {
            _commands = new Dictionary<string, IRevitCommand>();
            DiscoverCommands();
        }

        /// <summary>
        /// Get a command by name
        /// </summary>
        public IRevitCommand GetCommand(string name)
        {
            if (_commands.TryGetValue(name, out var command))
                return command;

            throw new ArgumentException(
                $"Unknown command: '{name}'. Available commands: {string.Join(", ", _commands.Keys)}");
        }

        /// <summary>
        /// Check if a command exists
        /// </summary>
        public bool HasCommand(string name) => _commands.ContainsKey(name);

        /// <summary>
        /// Get all registered command names
        /// </summary>
        public IEnumerable<string> GetCommandNames() => _commands.Keys;

        /// <summary>
        /// Get a recovery suggestion for a failed command
        /// </summary>
        public string GetSuggestion(string command, Exception ex)
        {
            // Common error patterns and suggestions
            if (ex.Message.Contains("not found") || ex.Message.Contains("찾을 수 없"))
                return $"The specified element or category was not found. Use revit_get_all_categories to list valid names.";

            if (ex.Message.Contains("permission") || ex.Message.Contains("권한"))
                return "This operation may require document edit permissions. Ensure the document is not read-only.";

            if (ex.Message.Contains("transaction"))
                return "A Revit transaction error occurred. The document may be in an invalid state. Try again.";

            return $"Command '{command}' failed. Check the parameters and try again.";
        }

        /// <summary>
        /// Auto-discover all IRevitCommand implementations in the CommandSet assembly
        /// </summary>
        private void DiscoverCommands()
        {
            var commandSetAssembly = Assembly.GetAssembly(typeof(IRevitCommand));
            if (commandSetAssembly == null)
            {
                System.Diagnostics.Debug.WriteLine("[RevitMCP] WARNING: CommandSet assembly not found");
                return;
            }

            var commandTypes = commandSetAssembly.GetTypes()
                .Where(t => typeof(IRevitCommand).IsAssignableFrom(t)
                         && !t.IsAbstract
                         && !t.IsInterface);

            foreach (var type in commandTypes)
            {
                try
                {
                    var command = (IRevitCommand)Activator.CreateInstance(type);
                    _commands[command.Name] = command;
                    System.Diagnostics.Debug.WriteLine(
                        $"[RevitMCP] Registered command: {command.Name} ({command.Category})");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[RevitMCP] Failed to register {type.Name}: {ex.Message}");
                }
            }

            System.Diagnostics.Debug.WriteLine(
                $"[RevitMCP] Total commands registered: {_commands.Count}");
        }
    }
}
