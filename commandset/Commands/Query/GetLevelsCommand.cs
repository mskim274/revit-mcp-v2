using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using RevitMCP.CommandSet.Interfaces;

namespace RevitMCP.CommandSet.Commands.Query
{
    /// <summary>
    /// Get all levels in the project, sorted by elevation.
    /// </summary>
    public class GetLevelsCommand : IRevitCommand
    {
        public string Name => "get_levels";
        public string Category => "Query";

        public Task<CommandResult> ExecuteAsync(
            Document doc,
            Dictionary<string, object> parameters,
            CancellationToken cancellationToken)
        {
            try
            {
                var levels = new FilteredElementCollector(doc)
                    .OfClass(typeof(Level))
                    .Cast<Level>()
                    .OrderBy(l => l.Elevation)
                    .Select(l => new Dictionary<string, object>
                    {
                        ["id"] = l.Id.IntegerValue,
                        ["name"] = l.Name,
                        ["elevation"] = Math.Round(l.Elevation, 4),
                        ["elevation_mm"] = Math.Round(UnitUtils.ConvertFromInternalUnits(l.Elevation, UnitTypeId.Millimeters), 1)
                    })
                    .ToList();

                return Task.FromResult(CommandResult.Ok(new Dictionary<string, object>
                {
                    ["count"] = levels.Count,
                    ["levels"] = levels
                }));
            }
            catch (Exception ex)
            {
                return Task.FromResult(CommandResult.Fail(
                    $"Failed to get levels: {ex.Message}",
                    "Ensure a Revit document is open."));
            }
        }
    }
}
