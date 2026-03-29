using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;

namespace RevitMCP.CommandSet.Interfaces
{
    /// <summary>
    /// Interface for all Revit MCP commands.
    /// Each command implements this interface and is auto-discovered via reflection.
    /// Adding a new command = adding one C# file that implements IRevitCommand.
    /// </summary>
    public interface IRevitCommand
    {
        /// <summary>
        /// Unique command name matching the MCP tool name (without "revit_" prefix).
        /// Example: "ping", "query_elements", "create_wall"
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Command category for grouping: "Utility", "Query", "Create", "Modify", "View", "Export"
        /// </summary>
        string Category { get; }

        /// <summary>
        /// Execute the command against the Revit document.
        /// This method runs on the Revit main thread via Revit.Async.
        /// </summary>
        /// <param name="doc">Active Revit document</param>
        /// <param name="parameters">Command parameters from MCP tool call</param>
        /// <param name="cancellationToken">Cancellation token for timeout</param>
        /// <returns>Command result as serializable object</returns>
        Task<CommandResult> ExecuteAsync(
            Document doc,
            Dictionary<string, object> parameters,
            CancellationToken cancellationToken
        );
    }

    /// <summary>
    /// Result wrapper for command execution
    /// </summary>
    public class CommandResult
    {
        public bool Success { get; set; }
        public object Data { get; set; }
        public string ErrorMessage { get; set; }
        public string Suggestion { get; set; }

        public static CommandResult Ok(object data)
        {
            return new CommandResult { Success = true, Data = data };
        }

        public static CommandResult Fail(string message, string suggestion = null)
        {
            return new CommandResult
            {
                Success = false,
                ErrorMessage = message,
                Suggestion = suggestion
            };
        }
    }
}
