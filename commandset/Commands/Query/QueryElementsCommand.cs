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
    /// Query elements by category with 3-tier pagination support.
    ///
    /// Tier 1 (summary_only=true):  Returns counts grouped by type and level.
    /// Tier 2 (summary_only=false): Returns paginated element details.
    /// Tier 3 (export=true):        Future — CSV file export.
    ///
    /// Parameters:
    ///   category        (string, required) — BuiltInCategory name (e.g. "Walls", "StructuralFraming")
    ///   summary_only    (bool, optional)   — true for Tier 1 summary (default: true)
    ///   limit           (int, optional)    — page size for Tier 2 (default: 50, max: 200)
    ///   cursor          (string, optional) — pagination cursor for Tier 2
    ///   level_filter    (string, optional) — filter by level name
    ///   type_filter     (string, optional) — filter by type name (contains match)
    ///   parameter_name  (string, optional) — filter by parameter name existence
    ///   parameter_value (string, optional) — filter by parameter value (requires parameter_name)
    /// </summary>
    public class QueryElementsCommand : IRevitCommand
    {
        public string Name => "query_elements";
        public string Category => "Query";

        public Task<CommandResult> ExecuteAsync(
            Document doc,
            Dictionary<string, object> parameters,
            CancellationToken cancellationToken)
        {
            try
            {
                // Parse parameters
                var categoryName = GetParam<string>(parameters, "category");
                if (string.IsNullOrEmpty(categoryName))
                    return Task.FromResult(CommandResult.Fail(
                        "Missing required parameter: category",
                        "Provide a category name like 'Walls', 'StructuralFraming', 'Floors'. Use revit_get_all_categories to see available categories."));

                var summaryOnly = GetParam<bool>(parameters, "summary_only", true);
                var limit = GetParam<int>(parameters, "limit", 50);
                var cursor = GetParam<string>(parameters, "cursor", null);
                var levelFilter = GetParam<string>(parameters, "level_filter", null);
                var typeFilter = GetParam<string>(parameters, "type_filter", null);
                var parameterName = GetParam<string>(parameters, "parameter_name", null);
                var parameterValue = GetParam<string>(parameters, "parameter_value", null);

                // Clamp limit
                limit = Math.Max(1, Math.Min(limit, 200));

                // Resolve BuiltInCategory
                if (!TryResolveCategory(categoryName, out BuiltInCategory builtInCat))
                    return Task.FromResult(CommandResult.Fail(
                        $"Unknown category: '{categoryName}'",
                        "Use revit_get_all_categories to see valid category names."));

                // Collect elements
                var collector = new FilteredElementCollector(doc)
                    .OfCategory(builtInCat)
                    .WhereElementIsNotElementType();

                var elements = collector.ToList();
                cancellationToken.ThrowIfCancellationRequested();

                // Apply filters
                if (!string.IsNullOrEmpty(levelFilter))
                {
                    elements = elements.Where(e =>
                    {
                        var levelId = e.LevelId;
                        if (levelId == null || levelId == ElementId.InvalidElementId) return false;
                        var level = doc.GetElement(levelId) as Level;
                        return level != null && level.Name.Equals(levelFilter, StringComparison.OrdinalIgnoreCase);
                    }).ToList();
                }

                if (!string.IsNullOrEmpty(typeFilter))
                {
                    elements = elements.Where(e =>
                    {
                        var typeId = e.GetTypeId();
                        if (typeId == null || typeId == ElementId.InvalidElementId) return false;
                        var type = doc.GetElement(typeId);
                        return type != null && type.Name.IndexOf(typeFilter, StringComparison.OrdinalIgnoreCase) >= 0;
                    }).ToList();
                }

                if (!string.IsNullOrEmpty(parameterName))
                {
                    elements = elements.Where(e =>
                    {
                        var param = e.LookupParameter(parameterName);
                        if (param == null) return false;
                        if (string.IsNullOrEmpty(parameterValue)) return true;
                        return param.AsValueString()?.IndexOf(parameterValue, StringComparison.OrdinalIgnoreCase) >= 0
                            || param.AsString()?.IndexOf(parameterValue, StringComparison.OrdinalIgnoreCase) >= 0;
                    }).ToList();
                }

                cancellationToken.ThrowIfCancellationRequested();

                // Tier 1: Summary mode
                if (summaryOnly)
                {
                    var byType = new Dictionary<string, int>();
                    var byLevel = new Dictionary<string, int>();

                    foreach (var elem in elements)
                    {
                        // Group by type
                        var typeId = elem.GetTypeId();
                        var typeName = (typeId != null && typeId != ElementId.InvalidElementId)
                            ? doc.GetElement(typeId)?.Name ?? "Unknown"
                            : "Unknown";
                        byType[typeName] = byType.TryGetValue(typeName, out var tc) ? tc + 1 : 1;

                        // Group by level
                        var levelId = elem.LevelId;
                        var levelName = (levelId != null && levelId != ElementId.InvalidElementId)
                            ? (doc.GetElement(levelId) as Level)?.Name ?? "No Level"
                            : "No Level";
                        byLevel[levelName] = byLevel.TryGetValue(levelName, out var lc) ? lc + 1 : 1;
                    }

                    return Task.FromResult(CommandResult.Ok(new Dictionary<string, object>
                    {
                        ["mode"] = "summary",
                        ["total"] = elements.Count,
                        ["category"] = categoryName,
                        ["by_type"] = byType.OrderByDescending(kv => kv.Value)
                            .ToDictionary(kv => kv.Key, kv => kv.Value),
                        ["by_level"] = byLevel.OrderByDescending(kv => kv.Value)
                            .ToDictionary(kv => kv.Key, kv => kv.Value),
                        ["filters_applied"] = new Dictionary<string, string>
                        {
                            ["level"] = levelFilter ?? "",
                            ["type"] = typeFilter ?? "",
                            ["parameter"] = parameterName ?? ""
                        }
                    }));
                }

                // Tier 2: Paginated detail
                var offset = ParseCursor(cursor);
                var paged = elements.Skip(offset).Take(limit).ToList();

                var items = paged.Select(e => SerializeElement(doc, e)).ToList();

                return Task.FromResult(CommandResult.Ok(new Dictionary<string, object>
                {
                    ["mode"] = "paginated",
                    ["total_count"] = elements.Count,
                    ["returned_count"] = items.Count,
                    ["offset"] = offset,
                    ["limit"] = limit,
                    ["has_more"] = (offset + items.Count) < elements.Count,
                    ["items"] = items
                }));
            }
            catch (OperationCanceledException)
            {
                return Task.FromResult(CommandResult.Fail(
                    "Query was cancelled due to timeout.",
                    "Try a more specific filter or use summary_only mode for large categories."));
            }
            catch (Exception ex)
            {
                return Task.FromResult(CommandResult.Fail(
                    $"Query failed: {ex.Message}",
                    "Check that the category name is valid. Use revit_get_all_categories to see options."));
            }
        }

        /// <summary>
        /// Serialize a single element to a dictionary with key properties.
        /// </summary>
        private Dictionary<string, object> SerializeElement(Document doc, Element elem)
        {
            var result = new Dictionary<string, object>
            {
                ["id"] = elem.Id.IntegerValue,
                ["name"] = elem.Name ?? "",
                ["category"] = elem.Category?.Name ?? "Unknown"
            };

            // Type name
            var typeId = elem.GetTypeId();
            if (typeId != null && typeId != ElementId.InvalidElementId)
            {
                var typeElem = doc.GetElement(typeId);
                result["type_name"] = typeElem?.Name ?? "Unknown";
                result["family_name"] = (typeElem as ElementType)?.FamilyName ?? "";
            }

            // Level
            var levelId = elem.LevelId;
            if (levelId != null && levelId != ElementId.InvalidElementId)
            {
                var level = doc.GetElement(levelId) as Level;
                result["level"] = level?.Name ?? "Unknown";
                result["level_elevation"] = level?.Elevation ?? 0.0;
            }

            // Location
            if (elem.Location is LocationPoint lp)
            {
                result["location"] = new Dictionary<string, double>
                {
                    ["x"] = Math.Round(lp.Point.X, 4),
                    ["y"] = Math.Round(lp.Point.Y, 4),
                    ["z"] = Math.Round(lp.Point.Z, 4)
                };
            }
            else if (elem.Location is LocationCurve lc)
            {
                result["location_start"] = new Dictionary<string, double>
                {
                    ["x"] = Math.Round(lc.Curve.GetEndPoint(0).X, 4),
                    ["y"] = Math.Round(lc.Curve.GetEndPoint(0).Y, 4),
                    ["z"] = Math.Round(lc.Curve.GetEndPoint(0).Z, 4)
                };
                result["location_end"] = new Dictionary<string, double>
                {
                    ["x"] = Math.Round(lc.Curve.GetEndPoint(1).X, 4),
                    ["y"] = Math.Round(lc.Curve.GetEndPoint(1).Y, 4),
                    ["z"] = Math.Round(lc.Curve.GetEndPoint(1).Z, 4)
                };
                result["length"] = Math.Round(lc.Curve.Length, 4);
            }

            return result;
        }

        /// <summary>
        /// Parse cursor string (base64 "offset:N") to offset integer.
        /// </summary>
        private int ParseCursor(string cursor)
        {
            if (string.IsNullOrEmpty(cursor)) return 0;
            try
            {
                var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
                if (decoded.StartsWith("offset:") && int.TryParse(decoded.Substring(7), out var offset))
                    return Math.Max(0, offset);
            }
            catch { }
            return 0;
        }

        /// <summary>
        /// Try to resolve a user-friendly category name to a BuiltInCategory.
        /// Supports both exact enum names (e.g. "OST_Walls") and friendly names (e.g. "Walls").
        /// </summary>
        private bool TryResolveCategory(string name, out BuiltInCategory category)
        {
            category = default;

            // Try exact enum match first (e.g. "OST_Walls")
            if (Enum.TryParse<BuiltInCategory>(name, true, out category))
                return true;

            // Try with OST_ prefix
            if (Enum.TryParse<BuiltInCategory>("OST_" + name, true, out category))
                return true;

            // Common friendly name mappings
            var mappings = new Dictionary<string, BuiltInCategory>(StringComparer.OrdinalIgnoreCase)
            {
                ["Walls"] = BuiltInCategory.OST_Walls,
                ["Floors"] = BuiltInCategory.OST_Floors,
                ["Roofs"] = BuiltInCategory.OST_Roofs,
                ["Ceilings"] = BuiltInCategory.OST_Ceilings,
                ["Doors"] = BuiltInCategory.OST_Doors,
                ["Windows"] = BuiltInCategory.OST_Windows,
                ["Columns"] = BuiltInCategory.OST_Columns,
                ["StructuralColumns"] = BuiltInCategory.OST_StructuralColumns,
                ["StructuralFraming"] = BuiltInCategory.OST_StructuralFraming,
                ["Beams"] = BuiltInCategory.OST_StructuralFraming,
                ["StructuralFoundation"] = BuiltInCategory.OST_StructuralFoundation,
                ["Foundations"] = BuiltInCategory.OST_StructuralFoundation,
                ["Rooms"] = BuiltInCategory.OST_Rooms,
                ["Furniture"] = BuiltInCategory.OST_Furniture,
                ["Pipes"] = BuiltInCategory.OST_PipeCurves,
                ["Ducts"] = BuiltInCategory.OST_DuctCurves,
                ["CableTray"] = BuiltInCategory.OST_CableTray,
                ["Conduit"] = BuiltInCategory.OST_Conduit,
                ["Stairs"] = BuiltInCategory.OST_Stairs,
                ["Railings"] = BuiltInCategory.OST_StairsRailing,
                ["Ramps"] = BuiltInCategory.OST_Ramps,
                ["Grids"] = BuiltInCategory.OST_Grids,
                ["Levels"] = BuiltInCategory.OST_Levels,
                ["Parking"] = BuiltInCategory.OST_Parking,
                ["GenericModel"] = BuiltInCategory.OST_GenericModel,
                ["Sheets"] = BuiltInCategory.OST_Sheets,
                ["Views"] = BuiltInCategory.OST_Views
            };

            return mappings.TryGetValue(name, out category);
        }

        /// <summary>
        /// Safely get a typed parameter value from the dictionary.
        /// </summary>
        private T GetParam<T>(Dictionary<string, object> parameters, string key, T defaultValue = default)
        {
            if (parameters == null || !parameters.TryGetValue(key, out var value) || value == null)
                return defaultValue;

            try
            {
                if (value is T typed)
                    return typed;

                // Handle JSON deserialization quirks (e.g. long → int, string → bool)
                return (T)Convert.ChangeType(value, typeof(T));
            }
            catch
            {
                return defaultValue;
            }
        }
    }
}
