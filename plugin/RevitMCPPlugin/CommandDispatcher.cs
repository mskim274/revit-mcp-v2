using System;
using System.Collections.Generic;
using System.Linq;
using RevitMCP.CommandSet.Interfaces;
using RevitMCP.Plugin.Commands;
using RevitMCP.Plugin.Services;

namespace RevitMCP.Plugin
{
    /// <summary>
    /// Routes stable host commands and the currently active reloadable
    /// CommandSet generation.
    /// </summary>
    public sealed class CommandDispatcher : IDisposable
    {
        private readonly CommandSetRuntime _runtime;
        private readonly Dictionary<string, IRevitCommand> _hostCommands;

        public CommandDispatcher(string revitVersion)
        {
            _runtime = new CommandSetRuntime(revitVersion);
            _hostCommands = new Dictionary<string, IRevitCommand>(
                StringComparer.Ordinal)
            {
                ["get_commandset_status"] =
                    new GetCommandSetStatusCommand(_runtime),
                ["reload_commandset"] =
                    new ReloadCommandSetCommand(_runtime)
            };
        }

        public IRevitCommand GetCommand(string name)
        {
            if (_hostCommands.TryGetValue(name, out var hostCommand))
                return hostCommand;

            var command = _runtime.GetCommand(name);
            if (command != null)
                return command;

            throw new ArgumentException(
                $"Unknown command: '{name}'. Available commands: " +
                string.Join(", ", GetCommandNames()));
        }

        public bool HasCommand(string name)
        {
            return _hostCommands.ContainsKey(name) ||
                   _runtime.HasCommand(name);
        }

        public IEnumerable<string> GetCommandNames()
        {
            return _hostCommands.Keys
                .Concat(_runtime.GetCommandNames())
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
        }

        public string GetSuggestion(string command, Exception ex)
        {
            if (ex.Message.Contains("not found") ||
                ex.Message.Contains("찾을 수"))
                return "The specified element or category was not found. " +
                       "Use revit_get_all_categories to list valid names.";

            if (ex.Message.Contains("permission") ||
                ex.Message.Contains("권한"))
                return "This operation may require document edit permissions. " +
                       "Ensure the document is not read-only.";

            if (ex.Message.Contains("transaction"))
                return "A Revit transaction error occurred. The document may " +
                       "be in an invalid state. Try again.";

            return $"Command '{command}' failed. Check the parameters and try again.";
        }

        public void Dispose()
        {
            _runtime.Dispose();
        }
    }
}
