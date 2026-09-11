using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using RevitMCP.CommandSet.Interfaces;

namespace RevitMCP.CommandSet.Commands.Query
{
    /// <summary>List sheets and the view IDs placed through standard viewports.</summary>
    public class GetSheetsCommand : IRevitCommand
    {
        public string Name => "get_sheets";
        public string Category => "Query";

        public Task<CommandResult> ExecuteAsync(
            Document doc,
            Dictionary<string, object> parameters,
            CancellationToken cancellationToken)
        {
            try
            {
                var sheets = new List<Dictionary<string, object>>();
                foreach (var sheet in new FilteredElementCollector(doc)
                             .OfClass(typeof(ViewSheet))
                             .Cast<ViewSheet>()
                             .OrderBy(item => item.SheetNumber, StringComparer.OrdinalIgnoreCase))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var viewportIds = sheet.GetAllViewports().ToList();
                    var viewIds = new List<long>();
                    foreach (var viewportId in viewportIds)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (doc.GetElement(viewportId) is Viewport viewport)
                            viewIds.Add(viewport.ViewId.GetValue());
                    }

                    sheets.Add(new Dictionary<string, object>
                    {
                        ["id"] = sheet.Id.GetValue(),
                        ["number"] = sheet.SheetNumber ?? string.Empty,
                        ["name"] = sheet.Name ?? string.Empty,
                        ["viewport_count"] = viewportIds.Count,
                        ["viewport_ids"] = viewportIds.Select(id => id.GetValue()).ToList(),
                        ["viewport_view_ids"] = viewIds,
                    });
                }

                return Task.FromResult(CommandResult.Ok(new Dictionary<string, object>
                {
                    ["count"] = sheets.Count,
                    ["sheets"] = sheets,
                    ["note"] = sheets.Count == 0
                        ? "No ViewSheet elements exist in the current document."
                        : "viewport_view_ids excludes schedules, which are placed as ScheduleSheetInstance rather than Viewport.",
                }));
            }
            catch (OperationCanceledException)
            {
                return Task.FromResult(CommandResult.Fail(
                    "Sheet discovery was cancelled.",
                    "Retry after Revit is idle."));
            }
            catch (Exception ex)
            {
                return Task.FromResult(CommandResult.Fail(
                    $"Failed to list sheets: {ex.Message}",
                    "Retry in a project document with sheets available."));
            }
        }
    }
}
