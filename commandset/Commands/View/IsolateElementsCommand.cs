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
    /// Isolate specific elements in the active view (temporary hide/isolate).
    ///
    /// Parameters:
    ///   element_ids (int[], required) — Element IDs to isolate
    ///   mode        (string, optional) — "isolate" to show only these elements, "hide" to hide them (default: "isolate")
    ///   view_id     (int, optional)    — Target view ID (default: active view)
    /// </summary>
    public class IsolateElementsCommand : IRevitCommand
    {
        public string Name => "isolate_elements";
        public string Category => "View";

        public Task<CommandResult> ExecuteAsync(
            Document doc,
            Dictionary<string, object> parameters,
            CancellationToken cancellationToken)
        {
            try
            {
                if (parameters == null || !parameters.TryGetValue("element_ids", out var idsObj))
                    return Task.FromResult(CommandResult.Fail(
                        "Missing required parameter: element_ids",
                        "Provide an array of element IDs to isolate or hide."));

                var elementIds = ParseElementIds(idsObj);
                if (elementIds.Count == 0)
                    return Task.FromResult(CommandResult.Fail(
                        "No valid element IDs provided.",
                        "Use revit_query_elements to find element IDs."));

                // Get mode
                var mode = "isolate";
                if (parameters.TryGetValue("mode", out var modeObj) && modeObj != null)
                    mode = modeObj.ToString().ToLower();

                // Resolve view
                global::Autodesk.Revit.DB.View view = null;
                if (parameters.TryGetValue("view_id", out var vidObj) && vidObj != null)
                {
                    var viewId = Convert.ToInt32(vidObj);
                    view = doc.GetElement(new ElementId(viewId)) as global::Autodesk.Revit.DB.View;
                }

                // Use active view if not specified
                if (view == null)
                    view = doc.ActiveView;

                if (view == null)
                    return Task.FromResult(CommandResult.Fail(
                        "No active view found.",
                        "Open a view first or provide view_id."));

                if (view.IsTemplate)
                    return Task.FromResult(CommandResult.Fail(
                        "Cannot isolate elements in a view template.",
                        "Provide a non-template view ID."));

                // Validate elements exist
                var validIds = new List<ElementId>();
                foreach (var id in elementIds)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var elem = doc.GetElement(new ElementId(id));
                    if (elem != null)
                        validIds.Add(new ElementId(id));
                }

                if (validIds.Count == 0)
                    return Task.FromResult(CommandResult.Fail(
                        "None of the provided element IDs are valid.",
                        "Use revit_query_elements to find valid element IDs."));

                // Execute isolation/hiding
                if (mode == "hide")
                {
                    using (var tx = new Transaction(doc, $"MCP: Hide {validIds.Count} elements"))
                    {
                        tx.Start();
                        view.HideElements(validIds);
                        tx.Commit();
                    }
                }
                else
                {
                    // Return action for plugin layer to handle via UIDocument selection + isolate
                    return Task.FromResult(CommandResult.Ok(new Dictionary<string, object>
                    {
                        ["action"] = "isolate_in_view",
                        ["mode"] = mode,
                        ["element_count"] = validIds.Count,
                        ["element_ids"] = validIds.Select(id => id.IntegerValue).ToList(),
                        ["view_name"] = view.Name,
                        ["view_id"] = view.Id.IntegerValue
                    }));
                }

                return Task.FromResult(CommandResult.Ok(new Dictionary<string, object>
                {
                    ["mode"] = mode,
                    ["element_count"] = validIds.Count,
                    ["view_name"] = view.Name,
                    ["view_id"] = view.Id.IntegerValue,
                    ["note"] = mode == "isolate"
                        ? "Elements are now isolated. Use reset_view_isolation to restore."
                        : "Elements are now hidden. Use reset_view_isolation to restore."
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
                    $"Failed to isolate/hide elements: {ex.Message}",
                    "Some views may not support element isolation. Try a different view."));
            }
        }

        private List<int> ParseElementIds(object idsObj)
        {
            var result = new List<int>();
            if (idsObj is IEnumerable<object> enumerable)
            {
                foreach (var item in enumerable)
                {
                    if (item != null && int.TryParse(item.ToString(), out var id))
                        result.Add(id);
                }
            }
            else if (idsObj is string str)
            {
                foreach (var part in str.Split(','))
                {
                    if (int.TryParse(part.Trim(), out var id))
                        result.Add(id);
                }
            }
            else if (int.TryParse(idsObj?.ToString(), out var singleId))
            {
                result.Add(singleId);
            }
            return result;
        }
    }
}
