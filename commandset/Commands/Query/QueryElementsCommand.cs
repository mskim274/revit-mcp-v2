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
    /// Query host elements, and optionally loaded-link elements, by category.
    /// Summary mode is the safe default; detail and IDs use cursor pagination.
    /// workset_filter is an exact case-insensitive HOST workset match. Linked
    /// documents are not workset-filtered. Structural framing commonly exposes
    /// LevelId=-1, so its reference level should be queried through
    /// parameter_name/parameter_value rather than level_filter.
    /// </summary>
    public class QueryElementsCommand : IRevitCommand
    {
        private const int MaxLinkedSummaryEntries = 50;

        public string Name => "query_elements";
        public string Category => "Query";

        public Task<CommandResult> ExecuteAsync(
            Document doc,
            Dictionary<string, object> parameters,
            CancellationToken cancellationToken)
        {
            try
            {
                parameters = parameters ?? new Dictionary<string, object>();
                var categoryName = GetParam<string>(parameters, "category");
                if (string.IsNullOrWhiteSpace(categoryName))
                {
                    return Task.FromResult(CommandResult.Fail(
                        "Missing required parameter: category",
                        "Provide a category name like 'Walls', 'StructuralFraming', or 'Floors'. Use revit_get_all_categories to see available host categories."));
                }
                categoryName = categoryName.Trim();

                var summaryOnly = GetParam<bool>(parameters, "summary_only", true);
                var idsOnly = GetParam<bool>(parameters, "ids_only", false);
                if (!TryGetOptionalStrictBool(
                        parameters,
                        "include_links",
                        false,
                        out var includeLinks,
                        out var includeLinksError))
                {
                    return Task.FromResult(CommandResult.Fail(
                        includeLinksError,
                        "Use include_links=true or include_links=false, or omit it for host-only querying."));
                }

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
                        "Provide a non-empty level name, or omit level_filter. For StructuralFraming use its reference-level parameter instead."));
                }
                if (!TryGetOptionalNonBlankString(
                        parameters,
                        "workset_filter",
                        out var worksetFilter,
                        out var worksetFilterError))
                {
                    return Task.FromResult(CommandResult.Fail(
                        worksetFilterError,
                        "Provide a non-empty exact host workset name, or omit workset_filter."));
                }
                if (!string.IsNullOrEmpty(worksetFilter) && !doc.IsWorkshared)
                {
                    return Task.FromResult(CommandResult.Fail(
                        "workset_filter cannot be used because the host document is not workshared.",
                        "Omit workset_filter, or open a workshared host document and use an exact workset name from revit_get_project_info."));
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
                {
                    return Task.FromResult(CommandResult.Fail(
                        $"Invalid match_mode: '{matchMode}'",
                        "Use one of: 'exact' (default), 'contains', or 'empty'."));
                }
                if (hasExplicitMatchMode && !hasParameterName)
                {
                    return Task.FromResult(CommandResult.Fail(
                        "match_mode requires parameter_name.",
                        "Provide parameter_name when setting match_mode, or omit match_mode to use the default."));
                }
                if (hasParameterValue && !hasParameterName)
                {
                    return Task.FromResult(CommandResult.Fail(
                        "parameter_value requires parameter_name.",
                        "Provide both parameter_name and parameter_value, or omit both."));
                }
                if (matchMode == "empty" && hasParameterValue)
                {
                    return Task.FromResult(CommandResult.Fail(
                        "parameter_value cannot be combined with match_mode='empty'.",
                        "Omit parameter_value when using match_mode='empty'."));
                }
                if (hasGroupByParameter && (!summaryOnly || idsOnly))
                {
                    return Task.FromResult(CommandResult.Fail(
                        "group_by_parameter is available only when summary_only=true and ids_only=false.",
                        "Use summary_only=true and ids_only=false, or omit group_by_parameter."));
                }

                var isSummaryMode = summaryOnly && !idsOnly;
                if (isSummaryMode && (hasLimit || hasCursor))
                {
                    return Task.FromResult(CommandResult.Fail(
                        "limit and cursor are not available in summary mode.",
                        "Omit limit/cursor for summary_only=true, or set summary_only=false (or ids_only=true) to paginate results."));
                }
                if (idsOnly)
                    summaryOnly = false;

                var maxLimit = idsOnly ? 10000 : 200;
                if (hasLimit && (limit < 1 || limit > maxLimit))
                {
                    return Task.FromResult(CommandResult.Fail(
                        $"limit must be between 1 and {maxLimit} in {(idsOnly ? "ids_only" : "detail")} mode.",
                        $"Use an integer from 1 to {maxLimit}, or omit limit to use the default."));
                }
                if (!hasLimit)
                    limit = idsOnly ? 5000 : 50;

                if (!TryResolveCategory(categoryName, out BuiltInCategory builtInCat))
                {
                    return Task.FromResult(CommandResult.Fail(
                        $"Unknown category: '{categoryName}'",
                        "Use revit_get_all_categories to see valid category names."));
                }

                // Each document/category collector is enumerated once. The retained
                // records support summary aggregation or stable cursor pagination.
                var matches = new List<ElementMatch>();
                CollectMatches(
                    doc,
                    builtInCat,
                    levelFilter,
                    worksetFilter,
                    typeFilter,
                    parameterName,
                    parameterValue,
                    matchMode,
                    null,
                    matches,
                    cancellationToken);

                var linkedCounts = new List<LinkedCount>();
                var unloadedLinksSkipped = 0;
                if (includeLinks)
                {
                    foreach (var linkInstance in new FilteredElementCollector(doc)
                        .OfClass(typeof(RevitLinkInstance))
                        .WhereElementIsNotElementType()
                        .Cast<RevitLinkInstance>())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var linkedDocument = linkInstance.GetLinkDocument();
                        if (linkedDocument == null)
                        {
                            unloadedLinksSkipped++;
                            continue;
                        }

                        var linkTypeId = linkInstance.GetTypeId();
                        var linkContext = new LinkContext(
                            linkTypeId == null ? -1 : linkTypeId.GetValue(),
                            linkInstance.Id.GetValue(),
                            linkInstance.Name ?? linkedDocument.Title ?? "Linked model");
                        var before = matches.Count;

                        // workset_filter is intentionally host-only.
                        CollectMatches(
                            linkedDocument,
                            builtInCat,
                            levelFilter,
                            null,
                            typeFilter,
                            parameterName,
                            parameterValue,
                            matchMode,
                            linkContext,
                            matches,
                            cancellationToken);

                        linkedCounts.Add(new LinkedCount(linkContext, matches.Count - before));
                    }
                }

                var hostCount = matches.Count(m => m.Link == null);
                var linkedCount = matches.Count - hostCount;

                if (summaryOnly)
                {
                    var byType = new Dictionary<string, int>();
                    var byLevel = new Dictionary<string, int>();
                    var byParamValue = string.IsNullOrEmpty(groupByParameter)
                        ? null
                        : new Dictionary<string, int>();

                    foreach (var match in matches)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var elem = match.Element;
                        var sourceDoc = match.Document;
                        var typeId = elem.GetTypeId();
                        var typeName = typeId != null && typeId != ElementId.InvalidElementId
                            ? sourceDoc.GetElement(typeId)?.Name ?? "Unknown"
                            : "Unknown";
                        Increment(byType, typeName);

                        var levelId = elem.LevelId;
                        var levelName = levelId != null && levelId != ElementId.InvalidElementId
                            ? (sourceDoc.GetElement(levelId) as Level)?.Name ?? "No Level"
                            : "No Level";
                        Increment(byLevel, levelName);

                        if (byParamValue != null)
                        {
                            var p = elem.LookupParameter(groupByParameter);
                            var key = p == null ? "(no parameter)"
                                : IsValueEmpty(p) ? "(empty)"
                                : p.AsString() ?? p.AsValueString() ?? "(empty)";
                            Increment(byParamValue, key);
                        }
                    }

                    var orderedLinks = linkedCounts
                        .OrderByDescending(item => item.Count)
                        .ThenBy(item => item.Link.Name, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    var byLink = orderedLinks
                        .Take(MaxLinkedSummaryEntries)
                        .Select(item => new Dictionary<string, object>
                        {
                            ["link_id"] = item.Link.LinkId,
                            ["instance_id"] = item.Link.InstanceId,
                            ["link_name"] = item.Link.Name,
                            ["count"] = item.Count
                        })
                        .ToList();

                    var summary = new Dictionary<string, object>
                    {
                        ["mode"] = "summary",
                        ["total"] = matches.Count,
                        ["host_count"] = hostCount,
                        ["linked_count"] = linkedCount,
                        ["category"] = categoryName,
                        ["by_type"] = byType.OrderByDescending(kv => kv.Value)
                            .ToDictionary(kv => kv.Key, kv => kv.Value),
                        ["by_level"] = byLevel.OrderByDescending(kv => kv.Value)
                            .ToDictionary(kv => kv.Key, kv => kv.Value),
                        ["by_link"] = byLink,
                        ["linked_instances_queried"] = linkedCounts.Count,
                        ["unloaded_links_skipped"] = unloadedLinksSkipped,
                        ["links_truncated"] = orderedLinks.Count > MaxLinkedSummaryEntries,
                        ["filters_applied"] = new Dictionary<string, object>
                        {
                            ["level"] = levelFilter ?? "",
                            ["workset"] = worksetFilter ?? "",
                            ["type"] = typeFilter ?? "",
                            ["parameter"] = parameterName ?? "",
                            ["match_mode"] = string.IsNullOrEmpty(parameterName) ? "" : matchMode,
                            ["include_links"] = includeLinks
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

                var ordered = matches
                    .OrderBy(m => m.Link == null ? 0 : 1)
                    .ThenBy(m => m.Link?.InstanceId ?? -1)
                    .ThenBy(m => m.Element.Id.GetValue())
                    .ToList();
                var offset = ParseCursor(cursor);
                var paged = ordered.Skip(offset).Take(limit).ToList();
                var hasMore = offset + paged.Count < ordered.Count;
                var nextCursor = hasMore ? CreateCursor(offset + paged.Count) : null;

                var result = new Dictionary<string, object>
                {
                    ["mode"] = idsOnly ? "ids" : "paginated",
                    ["total_count"] = ordered.Count,
                    ["host_count"] = hostCount,
                    ["linked_count"] = linkedCount,
                    ["unloaded_links_skipped"] = unloadedLinksSkipped,
                    ["returned_count"] = paged.Count,
                    ["offset"] = offset,
                    ["limit"] = limit,
                    ["has_more"] = hasMore,
                    ["next_cursor"] = nextCursor
                };

                if (idsOnly)
                {
                    if (includeLinks)
                    {
                        result["ids"] = paged.Select(SerializeId).ToList();
                        result["host_ids"] = paged
                            .Where(m => m.Link == null)
                            .Select(m => m.Element.Id.GetValue())
                            .ToList();
                    }
                    else
                    {
                        // Preserve the directly batch-composable host-only contract.
                        result["ids"] = paged.Select(m => m.Element.Id.GetValue()).ToList();
                    }
                }
                else
                {
                    result["items"] = paged
                        .Select(m => SerializeElement(m.Document, m.Element, m.Link, includeLinks))
                        .ToList();
                }

                return Task.FromResult(CommandResult.Ok(result));
            }
            catch (OperationCanceledException)
            {
                return Task.FromResult(CommandResult.Fail(
                    "Query was cancelled due to timeout.",
                    "Try a more specific filter, set include_links=false, or use summary_only mode for large categories."));
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
                    "Check the category and filters. Use revit_get_all_categories, revit_get_project_info, or revit_get_linked_models to discover valid values."));
            }
        }

        private static void CollectMatches(
            Document sourceDoc,
            BuiltInCategory category,
            string levelFilter,
            string worksetFilter,
            string typeFilter,
            string parameterName,
            string parameterValue,
            string matchMode,
            LinkContext link,
            List<ElementMatch> matches,
            CancellationToken cancellationToken)
        {
            foreach (var element in new FilteredElementCollector(sourceDoc)
                .OfCategory(category)
                .WhereElementIsNotElementType())
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!string.IsNullOrEmpty(levelFilter))
                {
                    var levelId = element.LevelId;
                    var level = levelId != null && levelId != ElementId.InvalidElementId
                        ? sourceDoc.GetElement(levelId) as Level
                        : null;
                    if (level == null ||
                        !level.Name.Equals(levelFilter, StringComparison.OrdinalIgnoreCase))
                        continue;
                }

                if (!string.IsNullOrEmpty(worksetFilter))
                {
                    var worksetName = GetWorksetName(sourceDoc, element);
                    if (!string.Equals(worksetName, worksetFilter, StringComparison.OrdinalIgnoreCase))
                        continue;
                }

                if (!string.IsNullOrEmpty(typeFilter))
                {
                    var typeId = element.GetTypeId();
                    var type = typeId != null && typeId != ElementId.InvalidElementId
                        ? sourceDoc.GetElement(typeId)
                        : null;
                    if (type == null ||
                        type.Name.IndexOf(typeFilter, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                }

                if (!string.IsNullOrEmpty(parameterName))
                {
                    var param = element.LookupParameter(parameterName);
                    if (param == null)
                        continue;
                    if (matchMode == "empty")
                    {
                        if (!IsValueEmpty(param))
                            continue;
                    }
                    else if (!string.IsNullOrEmpty(parameterValue) &&
                             !MatchesValue(param, parameterValue, matchMode))
                    {
                        continue;
                    }
                }

                matches.Add(new ElementMatch(sourceDoc, element, link));
            }
        }

        private static Dictionary<string, object> SerializeId(ElementMatch match)
        {
            return new Dictionary<string, object>
            {
                ["id"] = match.Element.Id.GetValue(),
                ["link_id"] = match.Link?.LinkId,
                ["link_name"] = match.Link?.Name ?? ""
            };
        }

        private static Dictionary<string, object> SerializeElement(
            Document sourceDoc,
            Element elem,
            LinkContext link,
            bool includeLinkMetadata)
        {
            var result = new Dictionary<string, object>
            {
                ["id"] = elem.Id.GetValue(),
                ["name"] = elem.Name ?? "",
                ["category"] = elem.Category?.Name ?? "Unknown"
            };

            if (includeLinkMetadata)
            {
                result["link_id"] = link?.LinkId;
                result["link_name"] = link?.Name ?? "";
            }

            var typeId = elem.GetTypeId();
            if (typeId != null && typeId != ElementId.InvalidElementId)
            {
                var typeElem = sourceDoc.GetElement(typeId);
                result["type_name"] = typeElem?.Name ?? "Unknown";
                result["family_name"] = (typeElem as ElementType)?.FamilyName ?? "";
            }

            var levelId = elem.LevelId;
            if (levelId != null && levelId != ElementId.InvalidElementId)
            {
                var level = sourceDoc.GetElement(levelId) as Level;
                result["level"] = level?.Name ?? "Unknown";
                result["level_elevation"] = level?.Elevation ?? 0.0;
            }

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

        private static string GetWorksetName(Document sourceDoc, Element element)
        {
            try
            {
                var workset = sourceDoc.GetWorksetTable()?.GetWorkset(element.WorksetId);
                return workset?.Name;
            }
            catch
            {
                return null;
            }
        }

        private static void Increment(Dictionary<string, int> counts, string key)
        {
            counts[key] = counts.TryGetValue(key, out var count) ? count + 1 : 1;
        }

        private static int ParseCursor(string cursor)
        {
            if (cursor == null)
                return 0;
            if (string.IsNullOrWhiteSpace(cursor))
                throw new ArgumentException("cursor cannot be empty or whitespace.");

            if (int.TryParse(cursor, out var plainOffset))
            {
                if (plainOffset < 0)
                    throw new ArgumentException("cursor offset cannot be negative.");
                return plainOffset;
            }

            try
            {
                var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
                if (decoded.StartsWith("offset:") &&
                    int.TryParse(decoded.Substring(7), out var offset))
                {
                    if (offset < 0)
                        throw new ArgumentException("cursor offset cannot be negative.");
                    return offset;
                }
            }
            catch (ArgumentException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new ArgumentException(
                    "cursor is not a valid plain offset or issued base64 cursor.",
                    ex);
            }

            throw new ArgumentException("cursor is not a valid offset cursor.");
        }

        private static string CreateCursor(int offset)
        {
            return Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes($"offset:{offset}"));
        }

        private static bool IsValueEmpty(Parameter param)
        {
            if (!param.HasValue)
                return true;
            return string.IsNullOrEmpty(param.AsString()) &&
                   string.IsNullOrEmpty(param.AsValueString());
        }

        private static bool MatchesValue(Parameter param, string expected, string mode)
        {
            var asString = param.AsString();
            var asValue = param.AsValueString();
            if (mode == "contains")
            {
                return asValue?.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0 ||
                       asString?.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0;
            }

            return string.Equals(asValue, expected, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(asString, expected, StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryResolveCategory(string name, out BuiltInCategory category)
        {
            category = default;
            if (Enum.TryParse(name, true, out category))
                return true;
            if (Enum.TryParse("OST_" + name, true, out category))
                return true;

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

        private static T GetParam<T>(
            Dictionary<string, object> parameters,
            string key,
            T defaultValue = default)
        {
            if (parameters == null ||
                !parameters.TryGetValue(key, out var value) ||
                value == null)
                return defaultValue;

            try
            {
                if (value is T typed)
                    return typed;
                return (T)Convert.ChangeType(value, typeof(T));
            }
            catch
            {
                return defaultValue;
            }
        }

        private static bool HasNonNullParameter(
            Dictionary<string, object> parameters,
            string key)
        {
            return parameters != null &&
                   parameters.TryGetValue(key, out var value) &&
                   value != null;
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
            if (parameters == null || !parameters.TryGetValue(key, out var raw))
                return true;

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
                case long longValue when longValue >= int.MinValue && longValue <= int.MaxValue:
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

        private static bool TryGetOptionalStrictBool(
            Dictionary<string, object> parameters,
            string key,
            bool defaultValue,
            out bool value,
            out string error)
        {
            value = defaultValue;
            error = null;
            if (parameters == null || !parameters.TryGetValue(key, out var raw))
                return true;
            if (raw is bool boolValue)
            {
                value = boolValue;
                return true;
            }

            error = $"{key} must be true or false when supplied.";
            return false;
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
            if (parameters == null || !parameters.TryGetValue(key, out var raw))
                return true;

            supplied = true;
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
                return true;

            if (!(raw is string text) || string.IsNullOrWhiteSpace(text))
            {
                error = $"{key} must be a non-empty string when supplied.";
                return false;
            }

            value = text.Trim();
            return true;
        }

        private sealed class ElementMatch
        {
            public ElementMatch(Document document, Element element, LinkContext link)
            {
                Document = document;
                Element = element;
                Link = link;
            }

            public Document Document { get; }
            public Element Element { get; }
            public LinkContext Link { get; }
        }

        private sealed class LinkContext
        {
            public LinkContext(long linkId, long instanceId, string name)
            {
                LinkId = linkId;
                InstanceId = instanceId;
                Name = name;
            }

            public long LinkId { get; }
            public long InstanceId { get; }
            public string Name { get; }
        }

        private sealed class LinkedCount
        {
            public LinkedCount(LinkContext link, int count)
            {
                Link = link;
                Count = count;
            }

            public LinkContext Link { get; }
            public int Count { get; }
        }
    }
}
