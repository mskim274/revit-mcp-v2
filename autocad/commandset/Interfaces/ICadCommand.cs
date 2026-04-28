using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;

namespace AutoCADMCP.CommandSet.Interfaces
{
    /// <summary>
    /// Interface for all AutoCAD MCP commands. Mirrors IRevitCommand from
    /// the Revit MCP. Each command implements this interface and is
    /// auto-discovered via reflection by CommandDispatcher.
    /// </summary>
    public interface ICadCommand
    {
        /// <summary>Wire name (matches MCP tool name without "cad_" prefix).</summary>
        string Name { get; }

        /// <summary>Grouping: "Utility", "Query", "Create", "Modify", "View", "Export".</summary>
        string Category { get; }

        /// <summary>
        /// Execute against the current AutoCAD document. Runs on the document's
        /// main thread (marshalled by the dispatcher via
        /// Application.DocumentManager.ExecuteInCommandContextAsync).
        ///
        /// Note: AutoCAD requires a Transaction even for read-only operations.
        /// The dispatcher hands the command an already-open Transaction so
        /// command bodies don't need to start one themselves for simple reads.
        /// For mutations, command bodies SHOULD start their own nested
        /// transaction (or use the supplied one and Commit before returning).
        /// </summary>
        Task<CommandResult> ExecuteAsync(
            Database db,
            Transaction tr,
            Dictionary<string, object> parameters,
            CancellationToken cancellationToken
        );
    }

    /// <summary>
    /// Result wrapper for command execution. Mirror of Revit MCP's CommandResult.
    /// </summary>
    public class CommandResult
    {
        public bool Success { get; set; }
        public object Data { get; set; }
        public string ErrorMessage { get; set; }
        public string Suggestion { get; set; }

        public static CommandResult Ok(object data)
            => new CommandResult { Success = true, Data = data };

        public static CommandResult Fail(string message, string suggestion = null)
            => new CommandResult { Success = false, ErrorMessage = message, Suggestion = suggestion };
    }
}
