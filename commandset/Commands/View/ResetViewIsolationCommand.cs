using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using RevitMCP.CommandSet.Interfaces;

namespace RevitMCP.CommandSet.Commands.View
{
    /// <summary>
    /// Reset temporary element isolation/hiding in a view (show all elements again).
    ///
    /// Parameters:
    ///   view_id (int, optional) — Target view ID (default: active view)
    /// </summary>
    public class ResetViewIsolationCommand : IRevitCommand
    {
        public string Name => "reset_view_isolation";
        public string Category => "View";

        public Task<CommandResult> ExecuteAsync(
            Document doc,
            Dictionary<string, object> parameters,
            CancellationToken cancellationToken)
        {
            try
            {
                // Resolve view
                global::Autodesk.Revit.DB.View view = null;
                if (parameters != null && parameters.TryGetValue("view_id", out var vidObj) && vidObj != null)
                {
                    var viewId = Convert.ToInt32(vidObj);
                    view = doc.GetElement(new ElementId(viewId)) as global::Autodesk.Revit.DB.View;
                }

                if (view == null)
                    view = doc.ActiveView;

                if (view == null)
                    return Task.FromResult(CommandResult.Fail(
                        "No active view found.",
                        "Open a view first or provide view_id."));

                // Check if there's any isolation active
                var hasIsolation = view.IsInTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);

                using (var tx = new Transaction(doc, "MCP: Reset View Isolation"))
                {
                    tx.Start();
                    view.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
                    tx.Commit();
                }

                return Task.FromResult(CommandResult.Ok(new Dictionary<string, object>
                {
                    ["view_name"] = view.Name,
                    ["view_id"] = view.Id.IntegerValue,
                    ["had_isolation"] = hasIsolation,
                    ["status"] = "All elements are now visible."
                }));
            }
            catch (OperationCanceledException)
            {
                return Task.FromResult(CommandResult.Fail(
                    "Operation cancelled.", "Try again."));
            }
            catch (Exception ex)
            {
                return Task.FromResult(CommandResult.Fail(
                    $"Failed to reset view isolation: {ex.Message}",
                    "Ensure a view is active."));
            }
        }
    }
}
