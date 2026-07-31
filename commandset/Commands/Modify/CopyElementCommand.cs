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
    /// Copy one or more elements by a translation vector.
    ///
    /// Parameters:
    ///   element_ids (int[], required) — Element IDs to copy
    ///   dx          (double, required) — Translation in X (feet)
    ///   dy          (double, required) — Translation in Y (feet)
    ///   dz          (double, optional) — Translation in Z (feet, default: 0)
    /// </summary>
    public class CopyElementCommand : IRevitCommand
    {
        public string Name => "copy_elements";
        public string Category => "Modify";

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
                        "Provide element_ids, dx, and dy."));

                // Parse element IDs
                if (!parameters.TryGetValue("element_ids", out var idsObj))
                    return Task.FromResult(CommandResult.Fail(
                        "Missing required parameter: element_ids",
                        "Provide element IDs to copy."));

                var elementIds = ParseElementIds(idsObj);
                if (elementIds.Count == 0)
                    return Task.FromResult(CommandResult.Fail(
                        "No valid element IDs provided.",
                        "Use revit_query_elements to find element IDs."));

                if (elementIds.Count > 100)
                    return Task.FromResult(CommandResult.Fail(
                        $"Too many elements ({elementIds.Count}). Maximum is 100 per call.",
                        "Copy in smaller batches."));

                // Parse translation vector
                if (!parameters.TryGetValue("dx", out var dxObj))
                    return Task.FromResult(CommandResult.Fail(
                        "Missing required parameter: dx",
                        "Provide X translation distance in feet."));

                if (!parameters.TryGetValue("dy", out var dyObj))
                    return Task.FromResult(CommandResult.Fail(
                        "Missing required parameter: dy",
                        "Provide Y translation distance in feet."));

                var dx = Convert.ToDouble(dxObj);
                var dy = Convert.ToDouble(dyObj);
                var dz = parameters.TryGetValue("dz", out var dzObj) ? Convert.ToDouble(dzObj) : 0.0;
                if (!IsFinite(dx) || !IsFinite(dy) || !IsFinite(dz))
                    return Task.FromResult(CommandResult.Fail(
                        "dx, dy, and dz must be finite numbers.",
                        "Replace NaN or Infinity with finite distances in Revit internal feet."));

                var translation = new XYZ(dx, dy, dz);
                if (translation.IsZeroLength())
                    return Task.FromResult(CommandResult.Fail(
                        "Translation vector is zero; this would create overlapping duplicates.",
                        "Provide a non-zero dx, dy, or dz."));

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

                // Execute copy
                ICollection<ElementId> newIds;
                using (var tx = new Transaction(doc, $"MCP: Copy {validIds.Count} elements"))
                {
                    tx.Start();
                    newIds = ElementTransformUtils.CopyElements(doc, validIds, translation);
                    cancellationToken.ThrowIfCancellationRequested();
                    tx.CommitOrThrow();
                }

                // Gather info about new elements
                var newElements = new List<Dictionary<string, object>>();
                foreach (var newId in newIds)
                {
                    var elem = doc.GetElement(newId);
                    if (elem != null)
                    {
                        newElements.Add(new Dictionary<string, object>
                        {
                            ["id"] = newId.GetValue(),
                            ["name"] = elem.Name ?? "",
                            ["category"] = elem.Category?.Name ?? "Unknown"
                        });
                    }
                }

                var verification = new Dictionary<string, object>();
                try
                {
                    var missing = newIds
                        .Where(id => doc.GetElement(id) == null)
                        .Select(id => id.GetValue())
                        .ToList();
                    verification["performed"] = true;
                    verification["expected_count"] = newIds.Count;
                    verification["verified_count"] = newIds.Count - missing.Count;
                    verification["missing_new_ids"] = missing;
                    verification["match"] = missing.Count == 0;
                }
                catch (Exception verificationError)
                {
                    verification["performed"] = false;
                    verification["match"] = false;
                    verification["error"] = verificationError.Message;
                }

                return Task.FromResult(CommandResult.Ok(new Dictionary<string, object>
                {
                    ["requested_count"] = elementIds.Count,
                    ["valid_source_count"] = validIds.Count,
                    ["invalid_ids"] = invalidIds,
                    ["copied_count"] = newIds.Count,
                    ["new_elements"] = newElements,
                    ["translation"] = new Dictionary<string, double>
                    {
                        ["dx_feet"] = dx,
                        ["dy_feet"] = dy,
                        ["dz_feet"] = dz,
                        ["dx_mm"] = Math.Round(dx * 304.8, 1),
                        ["dy_mm"] = Math.Round(dy * 304.8, 1),
                        ["dz_mm"] = Math.Round(dz * 304.8, 1)
                    },
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
                    $"Failed to copy elements: {ex.Message}",
                    "Some elements may not be copyable. Try copying individually."));
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

        private static bool IsFinite(double value)
            => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
