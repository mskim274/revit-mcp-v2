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
    /// Get all grids in the project with geometry info.
    /// </summary>
    public class GetGridsCommand : IRevitCommand
    {
        public string Name => "get_grids";
        public string Category => "Query";

        public Task<CommandResult> ExecuteAsync(
            Document doc,
            Dictionary<string, object> parameters,
            CancellationToken cancellationToken)
        {
            try
            {
                var grids = new FilteredElementCollector(doc)
                    .OfClass(typeof(Grid))
                    .Cast<Grid>()
                    .OrderBy(g => g.Name)
                    .Select(g =>
                    {
                        var curve = g.Curve;
                        var info = new Dictionary<string, object>
                        {
                            ["id"] = g.Id.IntegerValue,
                            ["name"] = g.Name
                        };

                        if (curve != null)
                        {
                            info["start"] = new Dictionary<string, double>
                            {
                                ["x"] = Math.Round(curve.GetEndPoint(0).X, 4),
                                ["y"] = Math.Round(curve.GetEndPoint(0).Y, 4)
                            };
                            info["end"] = new Dictionary<string, double>
                            {
                                ["x"] = Math.Round(curve.GetEndPoint(1).X, 4),
                                ["y"] = Math.Round(curve.GetEndPoint(1).Y, 4)
                            };
                            info["length"] = Math.Round(curve.Length, 4);
                            info["is_curved"] = !(curve is Line);
                        }

                        return info;
                    })
                    .ToList();

                return Task.FromResult(CommandResult.Ok(new Dictionary<string, object>
                {
                    ["count"] = grids.Count,
                    ["grids"] = grids
                }));
            }
            catch (Exception ex)
            {
                return Task.FromResult(CommandResult.Fail(
                    $"Failed to get grids: {ex.Message}",
                    "Ensure a Revit document is open."));
            }
        }
    }
}
