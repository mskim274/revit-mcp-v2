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
    /// Select elements in the Revit UI (highlights them in the current view).
    /// NOTE: Actual selection requires UIDocument — this command returns
    /// validated element IDs for the plugin layer to select.
    ///
    /// Parameters:
    ///   element_ids (int[], required) — Element IDs to select
    /// </summary>
    public class SelectElementsCommand : IRevitCommand
    {
        public string Name => "select_elements";
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
                        "Provide an array of element IDs to select."));

                var elementIds = ParseElementIds(idsObj);
                if (elementIds.Count == 0)
                    return Task.FromResult(CommandResult.Fail(
                        "No valid element IDs provided.",
                        "Use revit_query_elements to find element IDs."));

                // Validate elements exist and collect info
                var validIds = new List<int>();
                var elementInfos = new List<Dictionary<string, object>>();

                foreach (var id in elementIds)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var elem = doc.GetElement(new ElementId(id));
                    if (elem != null)
                    {
                        validIds.Add(id);
                        elementInfos.Add(new Dictionary<string, object>
                        {
                            ["id"] = id,
                            ["name"] = elem.Name ?? "",
                            ["category"] = elem.Category?.Name ?? "Unknown"
                        });
                    }
                }

                if (validIds.Count == 0)
                    return Task.FromResult(CommandResult.Fail(
                        "None of the provided element IDs are valid.",
                        "Use revit_query_elements to find valid element IDs."));

                // Return validated IDs — actual UI selection handled by plugin layer
                return Task.FromResult(CommandResult.Ok(new Dictionary<string, object>
                {
                    ["action"] = "select_elements",
                    ["selected_count"] = validIds.Count,
                    ["element_ids"] = validIds,
                    ["elements"] = elementInfos
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
                    $"Failed to select elements: {ex.Message}",
                    "Use revit_query_elements to find valid element IDs."));
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
