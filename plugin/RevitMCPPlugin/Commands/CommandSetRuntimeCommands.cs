using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using RevitMCP.CommandSet.Interfaces;
using RevitMCP.Plugin.Services;

namespace RevitMCP.Plugin.Commands
{
    internal sealed class GetCommandSetStatusCommand : IRevitCommand
    {
        private readonly CommandSetRuntime _runtime;

        public GetCommandSetStatusCommand(CommandSetRuntime runtime)
        {
            _runtime = runtime;
        }

        public string Name => "get_commandset_status";
        public string Category => "Utility";

        public Task<CommandResult> ExecuteAsync(
            Document doc,
            Dictionary<string, object> parameters,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CommandResult.Ok(_runtime.GetStatus()));
        }
    }

    internal sealed class ReloadCommandSetCommand : IRevitCommand
    {
        private readonly CommandSetRuntime _runtime;

        public ReloadCommandSetCommand(CommandSetRuntime runtime)
        {
            _runtime = runtime;
        }

        public string Name => "reload_commandset";
        public string Category => "Utility";

        public Task<CommandResult> ExecuteAsync(
            Document doc,
            Dictionary<string, object> parameters,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var generation = GetOptionalString(parameters, "generation");
                var allowRemoval = GetOptionalBoolean(
                    parameters,
                    "allow_command_removal",
                    false);
                var persist = GetOptionalBoolean(
                    parameters,
                    "persist",
                    true);
                var result = _runtime.Reload(
                    generation,
                    allowRemoval,
                    persist);
                return Task.FromResult(CommandResult.Ok(result));
            }
            catch (Exception ex)
            {
                return Task.FromResult(CommandResult.Fail(
                    $"CommandSet reload failed: {ex.Message}",
                    "Run scripts\\stage-commandset.ps1, call " +
                    "revit_get_commandset_status, then retry with an exact " +
                    "valid generation. The previous generation remains active."));
            }
        }

        private static string GetOptionalString(
            Dictionary<string, object> parameters,
            string key)
        {
            if (!parameters.TryGetValue(key, out var value) || value == null)
                return null;
            if (!(value is string text) || string.IsNullOrWhiteSpace(text))
                throw new InvalidOperationException(
                    $"{key} must be a non-empty string when supplied.");
            return text.Trim();
        }

        private static bool GetOptionalBoolean(
            Dictionary<string, object> parameters,
            string key,
            bool defaultValue)
        {
            if (!parameters.TryGetValue(key, out var value) || value == null)
                return defaultValue;
            if (value is bool result)
                return result;
            throw new InvalidOperationException(
                $"{key} must be a JSON boolean.");
        }
    }
}
