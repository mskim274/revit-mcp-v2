using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using RevitMCP.CommandSet.Interfaces;

namespace RevitMCP.CommandSet.Commands.Modify
{
    /// <summary>
    /// Move one or more elements by a translation vector.
    ///
    /// Parameters:
    ///   element_ids (int[], required) — Element IDs to move
    ///   dx          (double, required) — Translation in X (feet)
    ///   dy          (double, required) — Translation in Y (feet)
    ///   dz          (double, optional) — Translation in Z (feet, default: 0)
    /// </summary>
    public class MoveElementCommand : IRevitCommand
    {
        public string Name => "move_elements";
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
                        "Provide element IDs to move."));

                var elementIds = ParseElementIds(idsObj);
                if (elementIds.Count == 0)
                    return Task.FromResult(CommandResult.Fail(
                        "No valid element IDs provided.",
                        "Use revit_query_elements to find element IDs."));

                if (elementIds.Count > 500)
                    return Task.FromResult(CommandResult.Fail(
                        $"Too many elements ({elementIds.Count}). Maximum is 500 per call.",
                        "Move in smaller batches."));

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

                var translation = new XYZ(dx, dy, dz);

                if (translation.IsZeroLength())
                    return Task.FromResult(CommandResult.Fail(
                        "Translation vector is zero — no movement needed.",
                        "Provide non-zero dx, dy, or dz values."));

                // Validate elements
                var validIds = new List<ElementId>();
                foreach (var id in elementIds)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var elem = doc.GetElement(new ElementId(id));
                    if (elem != null) validIds.Add(new ElementId(id));
                }

                if (validIds.Count == 0)
                    return Task.FromResult(CommandResult.Fail(
                        "None of the provided element IDs are valid.",
                        "Use revit_query_elements to find valid element IDs."));

                // Execute move
                using (var tx = new Transaction(doc, $"MCP: Move {validIds.Count} elements"))
                {
                    tx.Start();
                    ElementTransformUtils.MoveElements(doc, validIds, translation);
                    tx.Commit();
                }

                return Task.FromResult(CommandResult.Ok(new Dictionary<string, object>
                {
                    ["moved_count"] = validIds.Count,
                    ["translation"] = new Dictionary<string, double>
                    {
                        ["dx_feet"] = dx,
                        ["dy_feet"] = dy,
                        ["dz_feet"] = dz,
                        ["dx_mm"] = Math.Round(dx * 304.8, 1),
                        ["dy_mm"] = Math.Round(dy * 304.8, 1),
                        ["dz_mm"] = Math.Round(dz * 304.8, 1)
                    }
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
                    $"Failed to move elements: {ex.Message}",
                    "Some elements may be pinned or constrained. Try unpinning first."));
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
