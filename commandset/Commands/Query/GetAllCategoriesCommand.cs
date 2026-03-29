using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using RevitMCP.CommandSet.Interfaces;

namespace RevitMCP.CommandSet.Commands.Query
{
    /// <summary>
    /// Get all categories in the current document that contain at least one element.
    /// Optimized: single-pass counting instead of per-category FilteredElementCollector.
    ///
    /// Parameters:
    ///   include_empty (bool, optional) — Include categories with zero elements (default: false)
    /// </summary>
    public class GetAllCategoriesCommand : IRevitCommand
    {
        public string Name => "get_all_categories";
        public string Category => "Query";

        public Task<CommandResult> ExecuteAsync(
            Document doc,
            Dictionary<string, object> parameters,
            CancellationToken cancellationToken)
        {
            try
            {
                var includeEmpty = parameters != null
                    && parameters.TryGetValue("include_empty", out var ie)
                    && Convert.ToBoolean(ie);

                // Single-pass: count all instances grouped by category
                var instanceCounts = new Dictionary<int, int>();
                var allInstances = new FilteredElementCollector(doc)
                    .WhereElementIsNotElementType();

                foreach (var elem in allInstances)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (elem.Category == null) continue;
                    var catId = elem.Category.Id.IntegerValue;
                    instanceCounts[catId] = instanceCounts.TryGetValue(catId, out var c) ? c + 1 : 1;
                }

                // Build result from document categories
                var categories = doc.Settings.Categories;
                var result = new List<Dictionary<string, object>>();

                foreach (Category cat in categories)
                {
                    if (cat == null || cat.Id == null) continue;
                    if (cat.CategoryType != CategoryType.Model) continue;

                    var catIdInt = cat.Id.IntegerValue;
                    instanceCounts.TryGetValue(catIdInt, out var count);

                    if (!includeEmpty && count == 0) continue;

                    result.Add(new Dictionary<string, object>
                    {
                        ["name"] = cat.Name,
                        ["built_in_category"] = ((BuiltInCategory)catIdInt).ToString(),
                        ["instance_count"] = count
                    });
                }

                var sorted = result
                    .OrderByDescending(c => (int)c["instance_count"])
                    .ToList();

                return Task.FromResult(CommandResult.Ok(new Dictionary<string, object>
                {
                    ["count"] = sorted.Count,
                    ["categories"] = sorted
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
                    $"Failed to get categories: {ex.Message}",
                    "Ensure a Revit document is open."));
            }
        }
    }
}
