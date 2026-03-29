using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using RevitMCP.CommandSet.Interfaces;

namespace RevitMCP.CommandSet.Commands.View
{
    /// <summary>
    /// Set the active view in Revit by view name or ID.
    /// NOTE: This command needs UIDocument access — it returns the view info
    /// but actual view activation must happen via UIDocument.ActiveView on the plugin side.
    ///
    /// Parameters:
    ///   view_name (string, optional) — View name to activate
    ///   view_id   (int, optional)    — View ID to activate (takes precedence over name)
    /// </summary>
    public class SetActiveViewCommand : IRevitCommand
    {
        public string Name => "set_active_view";
        public string Category => "View";

        public Task<CommandResult> ExecuteAsync(
            Document doc,
            Dictionary<string, object> parameters,
            CancellationToken cancellationToken)
        {
            try
            {
                if (parameters == null)
                    return Task.FromResult(CommandResult.Fail(
                        "No parameters provided.",
                        "Provide view_name or view_id."));

                global::Autodesk.Revit.DB.View targetView = null;

                // Try by ID first
                if (parameters.TryGetValue("view_id", out var vidObj) && vidObj != null)
                {
                    var viewId = Convert.ToInt32(vidObj);
                    var elem = doc.GetElement(new ElementId(viewId));
                    targetView = elem as global::Autodesk.Revit.DB.View;

                    if (targetView == null)
                        return Task.FromResult(CommandResult.Fail(
                            $"Element {viewId} is not a view.",
                            "Use revit_get_views to find valid view IDs."));
                }
                // Then by name
                else if (parameters.TryGetValue("view_name", out var vnObj) && vnObj != null)
                {
                    var viewName = vnObj.ToString();
                    targetView = new FilteredElementCollector(doc)
                        .OfClass(typeof(global::Autodesk.Revit.DB.View))
                        .Cast<global::Autodesk.Revit.DB.View>()
                        .Where(v => !v.IsTemplate)
                        .FirstOrDefault(v => v.Name.Equals(viewName, StringComparison.OrdinalIgnoreCase));

                    if (targetView == null)
                    {
                        // Try partial match
                        targetView = new FilteredElementCollector(doc)
                            .OfClass(typeof(Autodesk.Revit.DB.View))
                            .Cast<Autodesk.Revit.DB.View>()
                            .Where(v => !v.IsTemplate)
                            .FirstOrDefault(v => v.Name.IndexOf(viewName, StringComparison.OrdinalIgnoreCase) >= 0);
                    }

                    if (targetView == null)
                        return Task.FromResult(CommandResult.Fail(
                            $"View '{viewName}' not found.",
                            "Use revit_get_views to see available views."));
                }
                else
                {
                    return Task.FromResult(CommandResult.Fail(
                        "Provide either view_name or view_id.",
                        "Use revit_get_views to find views."));
                }

                // Return view info — actual activation happens in the plugin layer
                // via UIDocument.ActiveView (requires UIApplication context)
                return Task.FromResult(CommandResult.Ok(new Dictionary<string, object>
                {
                    ["view_id"] = targetView.Id.IntegerValue,
                    ["view_name"] = targetView.Name,
                    ["view_type"] = targetView.ViewType.ToString(),
                    ["scale"] = targetView.Scale,
                    ["action"] = "activate_view"
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
                    $"Failed to set active view: {ex.Message}",
                    "Use revit_get_views to find valid view names."));
            }
        }
    }
}
