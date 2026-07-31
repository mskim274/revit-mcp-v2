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
                if (!TryGetOptionalInteger(
                        parameters,
                        "limit",
                        out var hasLimit,
                        out var limit,
                        out var limitError))
                {
                    return Task.FromResult(CommandResult.Fail(
                        limitError,
                        "Use an integer page size in the documented range, or omit limit to use the mode default."));
                }
                if (!TryGetOptionalTrimmedString(
                        parameters,
                        "cursor",
                        out var hasCursor,
                        out var cursor,
                        out var cursorError))
                {
                    return Task.FromResult(CommandResult.Fail(
                        cursorError,
                        "Pass the exact next_cursor from the previous response, a non-negative integer string, or omit cursor."));
                }
                if (!TryGetOptionalNonBlankString(
                        parameters,
                        "level_filter",
                        out var levelFilter,
                        out var levelFilterError))
                {
                    return Task.FromResult(CommandResult.Fail(
                        levelFilterError,
                        "Provide a non-empty level name, or omit level_filter."));
                }
                if (!TryGetOptionalNonBlankString(
                        parameters,
                        "type_filter",
                        out var typeFilter,
                        out var typeFilterError))
                {
                    return Task.FromResult(CommandResult.Fail(
                        typeFilterError,
                        "Provide a non-empty type-name substring, or omit type_filter."));
                }
                var parameterName = GetParam<string>(parameters, "parameter_name", null);
                if (!TryGetOptionalNonBlankString(
                        parameters,
                        "parameter_value",
                        out var parameterValue,
                        out var parameterValueError))
                {
                    return Task.FromResult(CommandResult.Fail(
                        parameterValueError,
                        "Provide a non-empty parameter_value, or use match_mode='empty' to find unfilled values."));
                }
                var matchMode = (GetParam<string>(parameters, "match_mode", "exact") ?? "exact").ToLowerInvariant();
                var groupByParameter = GetParam<string>(parameters, "group_by_parameter", null);
                var hasParameterName = !string.IsNullOrWhiteSpace(parameterName);
                var hasParameterValue = parameterValue != null;
                var hasExplicitMatchMode = HasNonNullParameter(parameters, "match_mode");
                var hasGroupByParameter = !string.IsNullOrWhiteSpace(groupByParameter);

                if (matchMode != "exact" && matchMode != "contains" && matchMode != "empty")
                    return Task.FromResult(CommandResult.Fail(
                        $"Invalid match_mode: '{matchMode}'",
                        "Use one of: 'exact' (default), 'contains', 'empty'."));
                if (hasExplicitMatchMode && !hasParameterName)
                    return Task.FromResult(CommandResult.Fail(
                        "match_mode requires parameter_name.",
                        "Provide parameter_name when setting match_mode, or omit match_mode to use the default."));
                if (hasParameterValue && !hasParameterName)
                    return Task.FromResult(CommandResult.Fail(
                        "parameter_value requires parameter_name.",
                        "Provide both parameter_name and parameter_value, or omit both."));
                if (matchMode == "empty" && hasParameterValue)
                    return Task.FromResult(CommandResult.Fail(
                        "parameter_value cannot be combined with match_mode='empty'.",
                        "Omit parameter_value when using match_mode='empty'."));
                if (hasGroupByParameter && (!summaryOnly || idsOnly))
                    return Task.FromResult(CommandResult.Fail(
                        "group_by_parameter is available only when summary_only=true and ids_only=false.",
                        "Use summary_only=true and ids_only=false, or omit group_by_parameter."));

                var isSummaryMode = summaryOnly && !idsOnly;
                if (isSummaryMode && (hasLimit || hasCursor))
                {
                    return Task.FromResult(CommandResult.Fail(
                        "limit and cursor are not available in summary mode.",
                        "Omit limit/cursor for summary_only=true, or set summary_only=false (or ids_only=true) to paginate results."));
                }

                // ids_only implies the caller wants the element list, not the summary.
                if (idsOnly)
                    summaryOnly = false;

                // An omitted limit receives the mode default. Supplied values
                // (including null) fail closed rather than being silently clamped.
                var maxLimit = idsOnly ? 10000 : 200;
                if (hasLimit && (limit < 1 || limit > maxLimit))
                {
                    return Task.FromResult(CommandResult.Fail(
                        $"limit must be between 1 and {maxLimit} in {(idsOnly ? "ids_only" : "detail")} mode.",
                        $"Use an integer from 1 to {maxLimit}, or omit limit to use the default."));
                }
                if (!hasLimit)
                    limit = idsOnly ? 5000 : 50;

                // Resolve BuiltInCategory
                if (!TryResolveCategory(categoryName, out BuiltInCategory builtInCat))
                    return Task.FromResult(CommandResult.Fail(
                        $"Unknown category: '{categoryName}'",
                        "Use revit_get_all_categories to see valid category names."));

                // Single-pass filtering avoids materializing and repeatedly
                // scanning very large category collections.
                var elements = new List<Element>();
                foreach (var element in new FilteredElementCollector(doc)
                    .OfCategory(builtInCat)
                    .WhereElementIsNotElementType())
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!string.IsNullOrEmpty(levelFilter))
                    {
                        var levelId = element.LevelId;
                        var level = levelId != null && levelId != ElementId.InvalidElementId
                            ? doc.GetElement(levelId) as Level
                            : null;
                        if (level == null
                            || !level.Name.Equals(levelFilter, StringComparison.OrdinalIgnoreCase))
                            continue;
                    }

                    if (!string.IsNullOrEmpty(typeFilter))
                    {
                        var typeId = element.GetTypeId();
                        var type = typeId != null && typeId != ElementId.InvalidElementId
                            ? doc.GetElement(typeId)
                            : null;
                        if (type == null
                            || type.Name.IndexOf(typeFilter, StringComparison.OrdinalIgnoreCase) < 0)
                            continue;
                    }

                    if (!string.IsNullOrEmpty(parameterName))
                    {
                        var param = element.LookupParameter(parameterName);
                        if (param == null) continue;
                        if (matchMode == "empty")
                        {
                            if (!IsValueEmpty(param)) continue;
                        }
                        else if (!string.IsNullOrEmpty(parameterValue)
                            && !MatchesValue(param, parameterValue, matchMode))
                        {
                            continue;
                        }
                    }

                    elements.Add(element);
                }

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
                        cancellationToken.ThrowIfCancellationRequested();
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
                elements = elements.OrderBy(e => e.Id.GetValue()).ToList();
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
                    result["ids"] = paged.Select(e => e.Id.GetValue()).ToList();
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
            catch (ArgumentException ex)
            {
                return Task.FromResult(CommandResult.Fail(
                    $"Invalid query input: {ex.Message}",
                    "Use the exact next_cursor returned by the previous page, or omit cursor to start from the first page."));
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
                ["id"] = elem.Id.GetValue(),
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
            if (cursor == null) return 0;
            if (string.IsNullOrWhiteSpace(cursor))
                throw new ArgumentException("cursor cannot be empty or whitespace.");

            // Plain integer cursor — accepted as a direct offset.
            if (int.TryParse(cursor, out var plainOffset))
            {
                if (plainOffset < 0)
                    throw new ArgumentException("cursor offset cannot be negative.");
                return plainOffset;
            }

            try
            {
                var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
                if (decoded.StartsWith("offset:") && int.TryParse(decoded.Substring(7), out var offset))
                {
                    if (offset < 0)
                        throw new ArgumentException("cursor offset cannot be negative.");
                    return offset;
                }
            }
            catch (ArgumentException) { throw; }
            catch (Exception ex)
            {
                throw new ArgumentException("cursor is not a valid plain offset or issued base64 cursor.", ex);
            }
            throw new ArgumentException("cursor is not a valid offset cursor.");
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

        /// <summary>
        /// True when a raw WebSocket caller supplied a non-null value for a key.
        /// Some legacy optional string fields are forwarded as null by the MCP
        /// server, so null does not count as explicitly setting those options.
        /// </summary>
        private static bool HasNonNullParameter(
            Dictionary<string, object> parameters,
            string key)
        {
            return parameters != null
                && parameters.TryGetValue(key, out var value)
                && value != null;
        }

        private static bool TryGetOptionalInteger(
            Dictionary<string, object> parameters,
            string key,
            out bool supplied,
            out int value,
            out string error)
        {
            supplied = false;
            value = 0;
            error = null;
            if (parameters == null ||
                !parameters.TryGetValue(key, out var raw))
            {
                return true;
            }

            supplied = true;
            if (raw == null)
            {
                error = $"{key} must be a 32-bit integer when supplied.";
                return false;
            }
            switch (raw)
            {
                case int intValue:
                    value = intValue;
                    return true;
                case long longValue
                    when longValue >= int.MinValue &&
                         longValue <= int.MaxValue:
                    value = (int)longValue;
                    return true;
                case double doubleValue
                    when !double.IsNaN(doubleValue) &&
                         !double.IsInfinity(doubleValue) &&
                         doubleValue == Math.Truncate(doubleValue) &&
                         doubleValue >= int.MinValue &&
                         doubleValue <= int.MaxValue:
                    value = (int)doubleValue;
                    return true;
                default:
                    error = $"{key} must be a 32-bit integer when supplied.";
                    return false;
            }
        }

        private static bool TryGetOptionalTrimmedString(
            Dictionary<string, object> parameters,
            string key,
            out bool supplied,
            out string value,
            out string error)
        {
            supplied = false;
            value = null;
            error = null;
            if (parameters == null ||
                !parameters.TryGetValue(key, out var raw))
            {
                return true;
            }

            supplied = true;
            if (raw == null)
            {
                error = $"{key} must be a string when supplied.";
                return false;
            }
            if (!(raw is string text))
            {
                error = $"{key} must be a string when supplied.";
                return false;
            }

            value = text.Trim();
            if (value.Length == 0)
            {
                error = $"{key} cannot be empty or whitespace.";
                return false;
            }

            return true;
        }

        private static bool TryGetOptionalNonBlankString(
            Dictionary<string, object> parameters,
            string key,
            out string value,
            out string error)
        {
            value = null;
            error = null;
            if (parameters == null ||
                !parameters.TryGetValue(key, out var raw) ||
                raw == null)
            {
                return true;
            }

            if (!(raw is string text) || string.IsNullOrWhiteSpace(text))
            {
                error = $"{key} must be a non-empty string when supplied.";
                return false;
            }

            value = text.Trim();
            return true;
        }
    }
}
