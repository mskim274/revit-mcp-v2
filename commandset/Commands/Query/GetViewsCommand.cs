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
    /// Get all views in the project, optionally filtered by type.
    ///
    /// Parameters:
    ///   view_type (string, optional) — Filter: "FloorPlan", "Section", "Elevation", "3D", "Sheet", etc.
    /// </summary>
    public class GetViewsCommand : IRevitCommand
    {
        public string Name => "get_views";
        public string Category => "Query";

        public Task<CommandResult> ExecuteAsync(
            Document doc,
            Dictionary<string, object> parameters,
            CancellationToken cancellationToken)
        {
            try
            {
                var viewTypeFilter = parameters != null
                    && parameters.TryGetValue("view_type", out var vt)
                    ? vt?.ToString() : null;

                var views = new FilteredElementCollector(doc)
                    .OfClass(typeof(Autodesk.Revit.DB.View))
                    .Cast<Autodesk.Revit.DB.View>()
                    .Where(v => !v.IsTemplate)
                    .Where(v =>
                    {
                        if (string.IsNullOrEmpty(viewTypeFilter)) return true;
                        return v.ViewType.ToString().Equals(viewTypeFilter, StringComparison.OrdinalIgnoreCase);
                    })
                    .OrderBy(v => v.ViewType.ToString())
                    .ThenBy(v => v.Name)
                    .Select(v =>
                    {
                        var info = new Dictionary<string, object>
                        {
                            ["id"] = v.Id.IntegerValue,
                            ["name"] = v.Name,
                            ["view_type"] = v.ViewType.ToString(),
                            ["is_template"] = v.IsTemplate,
                            ["scale"] = v.Scale,
                            ["detail_level"] = v.DetailLevel.ToString()
                        };

                        // Associated level for floor plans
                        if (v.GenLevel != null)
                            info["level"] = v.GenLevel.Name;

                        return info;
                    })
                    .ToList();

                // Group summary
                var byType = views.GroupBy(v => v["view_type"].ToString())
                    .ToDictionary(g => g.Key, g => g.Count());

                return Task.FromResult(CommandResult.Ok(new Dictionary<string, object>
                {
                    ["count"] = views.Count,
                    ["by_type"] = byType,
                    ["views"] = views
                }));
            }
            catch (Exception ex)
            {
                return Task.FromResult(CommandResult.Fail(
                    $"Failed to get views: {ex.Message}",
                    "Ensure a Revit document is open."));
            }
        }
    }
}
