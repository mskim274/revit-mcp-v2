using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using RevitMCP.CommandSet.Interfaces;

namespace RevitMCP.CommandSet.Commands.Modify
{
    /// <summary>
    /// Delete one or more elements from the Revit document.
    ///
    /// Parameters:
    ///   element_ids (int[], required) — Array of element IDs to delete
    /// </summary>
    public class DeleteElementsCommand : IRevitCommand
    {
        public string Name => "delete_elements";
        public string Category => "Modify";

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
                        "Provide an array of element IDs to delete."));

                // Parse element IDs from various input formats
                var elementIds = ParseElementIds(idsObj);
                if (elementIds.Count == 0)
                    return Task.FromResult(CommandResult.Fail(
                        "No valid element IDs provided.",
                        "Provide at least one integer element ID."));

                // Safety limit
                if (elementIds.Count > 100)
                    return Task.FromResult(CommandResult.Fail(
                        $"Too many elements ({elementIds.Count}). Maximum is 100 per call.",
                        "Delete in batches of 100 or fewer."));

                // Validate elements exist
                var validIds = new List<ElementId>();
                var invalidIds = new List<long>();
                var elementNames = new List<string>();

                foreach (var id in elementIds)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var elem = doc.GetElement(ElementIdCompatibility.Create(id));
                    if (elem != null)
                    {
                        validIds.Add(ElementIdCompatibility.Create(id));
                        elementNames.Add($"{elem.Name} ({elem.Category?.Name ?? "Unknown"})");
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

                // Execute deletion in transaction
                ICollection<ElementId> deletedIds;
                using (var tx = new Transaction(doc, $"MCP: Delete {validIds.Count} elements"))
                {
                    tx.Start();
                    deletedIds = doc.Delete(validIds);
                    cancellationToken.ThrowIfCancellationRequested();
                    tx.CommitOrThrow();
                }

                var verification = new Dictionary<string, object>();
                try
                {
                    var remaining = validIds
                        .Where(id => doc.GetElement(id) != null)
                        .Select(id => id.GetValue())
                        .ToList();
                    verification["performed"] = true;
                    verification["deleted_targets"] = validIds.Count - remaining.Count;
                    verification["remaining_target_ids"] = remaining;
                    verification["match"] = remaining.Count == 0;
                }
                catch (Exception verificationError)
                {
                    verification["performed"] = false;
                    verification["match"] = false;
                    verification["error"] = verificationError.Message;
                }

                return Task.FromResult(CommandResult.Ok(new Dictionary<string, object>
                {
                    ["deleted_count"] = validIds.Count,
                    ["total_affected"] = deletedIds?.Count ?? validIds.Count,
                    ["deleted_elements"] = elementNames,
                    ["invalid_ids"] = invalidIds,
                    ["warning"] = deletedIds != null && deletedIds.Count > validIds.Count
                        ? $"Deleting {validIds.Count} elements also removed {deletedIds.Count - validIds.Count} dependent elements."
                        : null,
                    ["mutation_committed"] = true,
                    ["verification"] = verification
                }));
            }
            catch (OperationCanceledException)
            {
                return Task.FromResult(CommandResult.Fail(
                    "Operation cancelled.",
                    "Try again."));
            }
            catch (Exception ex)
            {
                return Task.FromResult(CommandResult.Fail(
                    $"Failed to delete elements: {ex.Message}",
                    "Some elements may be protected or have dependencies. Try deleting individually."));
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
                // Handle comma-separated string
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
