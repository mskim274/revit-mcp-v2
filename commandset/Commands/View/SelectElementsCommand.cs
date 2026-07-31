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
                if (elementIds.Count > 500)
                    return Task.FromResult(CommandResult.Fail(
                        $"Too many element IDs: {elementIds.Count} (max 500).",
                        "Select in batches of at most 500 elements."));

                // Validate elements exist and collect info
                var validIds = new List<long>();
                var invalidIds = new List<long>();
                var elementInfos = new List<Dictionary<string, object>>();

                foreach (var id in elementIds)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var elem = doc.GetElement(ElementIdCompatibility.Create(id));
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
                    else
                    {
                        invalidIds.Add(id);
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
                    ["requested_count"] = elementIds.Count,
                    ["valid_count"] = validIds.Count,
                    ["invalid_ids"] = invalidIds,
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
