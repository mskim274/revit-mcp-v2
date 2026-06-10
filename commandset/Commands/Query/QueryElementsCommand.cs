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
    ///   ids_only        (bool, optional)   — return element IDs only (default page 5000, max 10000);
    ///                                        overrides summary_only
    ///   limit           (int, optional)    — page size for Tier 2 (default: 50, max: 200)
    ///   cursor          (string, optional) — pagination cursor for Tier 2
    ///   level_filter    (string, optional) — filter by level name
    ///   type_filter     (string, optional) — filter by type name (contains match)
    ///   parameter_name  (string, optional) — filter by parameter name existence
    ///   parameter_value (string, optional) — filter by parameter value (requires parameter_name)
    ///   match_mode      (string, optional) — "exact" (default) | "contains" | "empty".
    ///                                        "empty" matches elements whose parameter exists but has no value.
    ///   group_by_parameter (string, optional) — summary mode only: adds a value→count
    ///                                        distribution for the given parameter.
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
                var idsOnly = GetParam<bool>(parameters, "ids_only", false);
                var limit = GetParam<int>(parameters, "limit", 0);
                var cursor = GetParam<string>(parameters, "cursor", null);
                var levelFilter = GetParam<string>(parameters, "level_filter", null);
                var typeFilter = GetParam<string>(parameters, "type_filter", null);
                var parameterName = GetParam<string>(parameters, "parameter_name", null);
                var parameterValue = GetParam<string>(parameters, "parameter_value", null);
                var matchMode = (GetParam<string>(parameters, "match_mode", "exact") ?? "exact").ToLowerInvariant();
                var groupByParameter = GetParam<string>(parameters, "group_by_parameter", null);

                if (matchMode != "exact" && matchMode != "contains" && matchMode != "empty")
                    return Task.FromResult(CommandResult.Fail(
                        $"Invalid match_mode: '{matchMode}'",
                        "Use one of: 'exact' (default), 'contains', 'empty'."));

                // ids_only implies the caller wants the element list, not the summary.
                if (idsOnly)
                    summaryOnly = false;

                // Clamp limit. ids_only responses are tiny (one integer per element),
                // so they get a much larger default page and cap.
                if (idsOnly)
                    limit = limit <= 0 ? 5000 : Math.Max(1, Math.Min(limit, 10000));
                else
                    limit = limit <= 0 ? 50 : Math.Max(1, Math.Min(limit, 200));

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

                        if (matchMode == "empty")
                            return IsValueEmpty(param);

                        if (string.IsNullOrEmpty(parameterValue)) return true;
                        return MatchesValue(param, parameterValue, matchMode);
                    }).ToList();
                }

                cancellationToken.ThrowIfCancellationRequested();

                // Tier 1: Summary mode
                if (summaryOnly)
                {
                    var byType = new Dictionary<string, int>();
                    var byLevel = new Dictionary<string, int>();
                    var byParamValue = string.IsNullOrEmpty(groupByParameter)
                        ? null
                        : new Dictionary<string, int>();

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

                        // Group by parameter value (optional)
                        if (byParamValue != null)
                        {
                            var p = elem.LookupParameter(groupByParameter);
                            var key = p == null ? "(no parameter)"
                                : IsValueEmpty(p) ? "(empty)"
                                : (p.AsString() ?? p.AsValueString() ?? "(empty)");
                            byParamValue[key] = byParamValue.TryGetValue(key, out var pc) ? pc + 1 : 1;
                        }
                    }

                    var summary = new Dictionary<string, object>
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
                            ["parameter"] = parameterName ?? "",
                            ["match_mode"] = string.IsNullOrEmpty(parameterName) ? "" : matchMode
                        }
                    };

                    if (byParamValue != null)
                    {
                        summary["group_by_parameter"] = groupByParameter;
                        summary["by_parameter_value"] = byParamValue
                            .OrderByDescending(kv => kv.Value)
                            .ToDictionary(kv => kv.Key, kv => kv.Value);
                    }

                    return Task.FromResult(CommandResult.Ok(summary));
                }

                // Tier 2: Paginated detail (or lightweight ID list)
                var offset = ParseCursor(cursor);
                var paged = elements.Skip(offset).Take(limit).ToList();

                var hasMore = (offset + paged.Count) < elements.Count;
                var nextCursor = hasMore ? CreateCursor(offset + paged.Count) : null;

                var result = new Dictionary<string, object>
                {
                    ["mode"] = idsOnly ? "ids" : "paginated",
                    ["total_count"] = elements.Count,
                    ["returned_count"] = paged.Count,
                    ["offset"] = offset,
                    ["limit"] = limit,
                    ["has_more"] = hasMore,
                    ["next_cursor"] = nextCursor
                };

                if (idsOnly)
                    result["ids"] = paged.Select(e => e.Id.IntegerValue).ToList();
                else
                    result["items"] = paged.Select(e => SerializeElement(doc, e)).ToList();

                return Task.FromResult(CommandResult.Ok(result));
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
        /// Parse cursor string to offset integer.
        /// Accepts both base64 "offset:N" (issued via next_cursor) and plain integers ("200").
        /// </summary>
        private int ParseCursor(string cursor)
        {
            if (string.IsNullOrEmpty(cursor)) return 0;

            // Plain integer cursor — accepted as a direct offset.
            if (int.TryParse(cursor, out var plainOffset))
                return Math.Max(0, plainOffset);

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
        /// Create an opaque cursor for the given offset (base64 "offset:N").
        /// </summary>
        private string CreateCursor(int offset)
        {
            return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"offset:{offset}"));
        }

        /// <summary>
        /// True when the parameter exists but holds no usable value.
        /// </summary>
        private static bool IsValueEmpty(Parameter param)
        {
            if (!param.HasValue) return true;
            return string.IsNullOrEmpty(param.AsString()) && string.IsNullOrEmpty(param.AsValueString());
        }

        /// <summary>
        /// Compare a parameter's value (AsString or AsValueString) against an expected string.
        /// mode: "exact" → case-insensitive equality, "contains" → case-insensitive substring.
        /// </summary>
        private static bool MatchesValue(Parameter param, string expected, string mode)
        {
            var asString = param.AsString();
            var asValue = param.AsValueString();

            if (mode == "contains")
            {
                return (asValue?.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0)
                    || (asString?.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            return string.Equals(asValue, expected, StringComparison.OrdinalIgnoreCase)
                || string.Equals(asString, expected, StringComparison.OrdinalIgnoreCase);
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
