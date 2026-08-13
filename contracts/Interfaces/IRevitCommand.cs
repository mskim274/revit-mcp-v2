using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;

namespace RevitMCP.CommandSet.Interfaces
{
    /// <summary>
    /// Stable contract shared by the long-lived Revit host and reloadable
    /// CommandSet generations. Keep this assembly small: changing the contract
    /// still requires a Revit process restart.
    /// </summary>
    public interface IRevitCommand
    {
        string Name { get; }
        string Category { get; }

        Task<CommandResult> ExecuteAsync(
            Document doc,
            Dictionary<string, object> parameters,
            CancellationToken cancellationToken);
    }

    /// <summary>
    /// JSON-safe result envelope that crosses the AssemblyLoadContext boundary.
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
