using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using RevitMCP.CommandSet.Interfaces;

namespace RevitMCP.CommandSet.Commands.Utility
{
    /// <summary>
    /// Ping command — returns Revit version, document name, and basic stats.
    /// Used by Claude to verify the connection is alive.
    /// </summary>
    public class PingCommand : IRevitCommand
    {
        public string Name => "ping";
        public string Category => "Utility";

        public Task<CommandResult> ExecuteAsync(
            Document doc,
            Dictionary<string, object> parameters,
            CancellationToken cancellationToken)
        {
            try
            {
                var result = new Dictionary<string, object>
                {
                    ["revit_version"] = doc.Application.VersionNumber,
                    ["revit_build"] = doc.Application.VersionBuild,
                    ["document_name"] = doc.Title ?? "Untitled",
                    ["document_path"] = doc.PathName ?? "",
                    ["is_workshared"] = doc.IsWorkshared,
                    ["element_count"] = new FilteredElementCollector(doc)
                        .WhereElementIsNotElementType()
                        .GetElementCount(),
                    ["timestamp"] = DateTime.UtcNow.ToString("o")
                };

                return Task.FromResult(CommandResult.Ok(result));
            }
            catch (Exception ex)
            {
                return Task.FromResult(CommandResult.Fail(
                    $"Ping failed: {ex.Message}",
                    "Ensure a Revit document is open."
                ));
            }
        }
    }
}
