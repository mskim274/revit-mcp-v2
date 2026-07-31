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
                if (elementIds.Count > 500)
                    return Task.FromResult(CommandResult.Fail(
                        $"Too many element IDs: {elementIds.Count} (max 500).",
                        "Split the operation into smaller calls or use a narrower query."));

                // Get mode
                var mode = "isolate";
                if (parameters.TryGetValue("mode", out var modeObj) && modeObj != null)
                    mode = modeObj.ToString().ToLowerInvariant();
                if (mode != "isolate" && mode != "hide")
                    return Task.FromResult(CommandResult.Fail(
                        $"Invalid mode '{mode}'.",
                        "Use mode=\"isolate\" or mode=\"hide\"."));

                // Resolve view
                global::Autodesk.Revit.DB.View view = null;
                if (parameters.TryGetValue("view_id", out var vidObj) && vidObj != null)
                {
                    var viewId = Convert.ToInt64(vidObj);
                    view = doc.GetElement(ElementIdCompatibility.Create(viewId)) as global::Autodesk.Revit.DB.View;
                    if (view == null)
                        return Task.FromResult(CommandResult.Fail(
                            $"view_id {viewId} is not a valid view.",
                            "Use revit_get_views to choose a non-template graphical view."));
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
                var invalidIds = new List<long>();
                foreach (var id in elementIds)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var elem = doc.GetElement(ElementIdCompatibility.Create(id));
                    if (elem != null)
                        validIds.Add(ElementIdCompatibility.Create(id));
                    else
                        invalidIds.Add(id);
                }

                if (validIds.Count == 0)
                    return Task.FromResult(CommandResult.Fail(
                        "None of the provided element IDs are valid.",
                        "Use revit_query_elements to find valid element IDs."));

                return Task.FromResult(CommandResult.Ok(new Dictionary<string, object>
                {
                    ["action"] = "temporary_hide_isolate",
                    ["mode"] = mode,
                    ["requested_count"] = elementIds.Count,
                    ["valid_count"] = validIds.Count,
                    ["invalid_ids"] = invalidIds,
                    ["element_count"] = validIds.Count,
                    ["element_ids"] = validIds.Select(id => id.GetValue()).ToList(),
                    ["view_name"] = view.Name,
                    ["view_id"] = view.Id.GetValue(),
                    ["temporary"] = true,
                    ["note"] = "The plugin will apply temporary hide/isolate to this exact view. Use reset_view_isolation to restore."
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

        private List<long> ParseElementIds(object idsObj)
        {
            var result = new List<long>();
            if (idsObj is IEnumerable<object> enumerable)
            {
                foreach (var item in enumerable)
                {
                    if (item == null || !long.TryParse(item.ToString(), out var id) || id <= 0)
                        throw new ArgumentException($"element_ids contains an invalid positive integer ID: '{item}'.");
                    result.Add(id);
                }
            }
            else if (idsObj is string str)
            {
                foreach (var part in str.Split(','))
                {
                    if (!long.TryParse(part.Trim(), out var id) || id <= 0)
                        throw new ArgumentException($"element_ids contains an invalid positive integer ID: '{part}'.");
                    result.Add(id);
                }
            }
            else if (long.TryParse(idsObj?.ToString(), out var singleId) && singleId > 0)
            {
                result.Add(singleId);
            }
            else
            {
                throw new ArgumentException("element_ids must contain positive integer element IDs.");
            }
            return result.Distinct().ToList();
        }
    }
}
