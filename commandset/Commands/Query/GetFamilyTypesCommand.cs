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
    /// Get family types (loaded families and their types).
    /// Returns a summary by default; use include_types=true for full detail.
    ///
    /// Parameters:
    ///   category       (string, optional) — Filter by category name
    ///   family_name    (string, optional) — Filter by family name (contains match)
    ///   include_types  (bool, optional)   — Include individual type details (default: false)
    /// </summary>
    public class GetFamilyTypesCommand : IRevitCommand
    {
        public string Name => "get_family_types";
        public string Category => "Query";

        public Task<CommandResult> ExecuteAsync(
            Document doc,
            Dictionary<string, object> parameters,
            CancellationToken cancellationToken)
        {
            try
            {
                var categoryFilter = parameters != null
                    && parameters.TryGetValue("category", out var cat)
                    ? cat?.ToString() : null;

                var familyFilter = parameters != null
                    && parameters.TryGetValue("family_name", out var fn)
                    ? fn?.ToString() : null;

                var includeTypes = parameters != null
                    && parameters.TryGetValue("include_types", out var it)
                    && Convert.ToBoolean(it);

                var families = new FilteredElementCollector(doc)
                    .OfClass(typeof(Family))
                    .Cast<Family>()
                    .Where(f =>
                    {
                        if (!string.IsNullOrEmpty(categoryFilter))
                        {
                            var catName = f.FamilyCategory?.Name ?? "";
                            if (!catName.Equals(categoryFilter, StringComparison.OrdinalIgnoreCase))
                                return false;
                        }
                        if (!string.IsNullOrEmpty(familyFilter))
                        {
                            if (f.Name.IndexOf(familyFilter, StringComparison.OrdinalIgnoreCase) < 0)
                                return false;
                        }
                        return true;
                    })
                    .OrderBy(f => f.FamilyCategory?.Name ?? "")
                    .ThenBy(f => f.Name)
                    .ToList();

                cancellationToken.ThrowIfCancellationRequested();

                var familyList = families.Select(f =>
                {
                    var info = new Dictionary<string, object>
                    {
                        ["id"] = f.Id.IntegerValue,
                        ["name"] = f.Name,
                        ["category"] = f.FamilyCategory?.Name ?? "Unknown",
                        ["is_in_place"] = f.IsInPlace,
                        ["type_count"] = f.GetFamilySymbolIds()?.Count ?? 0
                    };

                    if (includeTypes)
                    {
                        var symbolIds = f.GetFamilySymbolIds();
                        if (symbolIds != null)
                        {
                            var types = symbolIds
                                .Select(id => doc.GetElement(id) as FamilySymbol)
                                .Where(s => s != null)
                                .OrderBy(s => s.Name)
                                .Select(s => new Dictionary<string, object>
                                {
                                    ["id"] = s.Id.IntegerValue,
                                    ["name"] = s.Name,
                                    ["is_active"] = s.IsActive
                                })
                                .ToList();
                            info["types"] = types;
                        }
                    }

                    return info;
                }).ToList();

                // By category summary
                var byCategory = familyList
                    .GroupBy(f => f["category"].ToString())
                    .ToDictionary(g => g.Key, g => g.Count());

                return Task.FromResult(CommandResult.Ok(new Dictionary<string, object>
                {
                    ["count"] = familyList.Count,
                    ["by_category"] = byCategory,
                    ["families"] = familyList
                }));
            }
            catch (OperationCanceledException)
            {
                return Task.FromResult(CommandResult.Fail(
                    "Query was cancelled due to timeout.",
                    "Try filtering by category or family_name to reduce results."));
            }
            catch (Exception ex)
            {
                return Task.FromResult(CommandResult.Fail(
                    $"Failed to get family types: {ex.Message}",
                    "Ensure a Revit document is open."));
            }
        }
    }
}
