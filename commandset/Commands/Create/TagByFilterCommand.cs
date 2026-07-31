using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using RevitMCP.CommandSet.Helpers;
using RevitMCP.CommandSet.Interfaces;

namespace RevitMCP.CommandSet.Commands.Create
{
    /// <summary>
    /// Place IndependentTag instances on every element matched by the selector.
    ///
    /// Use case: bulk-tag every wall whose mark starts with "S1-", every column
    /// on "Level 4", or every type whose name contains "Fire Rated".
    ///
    /// Parameters:
    ///   view_id (int, optional)            — Target view. Default = active view
    ///                                        (must be a graphical view that supports tags).
    ///
    ///   // Selector (same shape as apply_color_filter):
    ///   element_ids (int[], optional)
    ///   category (string, optional)
    ///   type_name_contains (string, optional)
    ///   type_name_starts_with (string, optional)
    ///   mark_contains (string, optional)
    ///   parameter_name + parameter_value_contains (optional pair)
    ///   level_name (string, optional)
    ///   max_elements (int, optional)       — Default 500. Tag bulk is expensive.
    ///
    ///   // Tag options:
    ///   tag_type_id (int, optional)        — Specific tag family-type id. Compatible
    ///                                        only with tag_mode="ByCategory". If omitted,
    ///                                        Revit resolves the default type for tag_mode.
    ///   has_leader (bool, optional)        — Default false.
    ///   orientation (string, optional)     — "Horizontal" (default) | "Vertical".
    ///   offset_x_feet (double, optional)   — Tag location offset from the element
    ///                                        anchor point. Default 0.
    ///   offset_y_feet (double, optional)   — Default 0.
    ///   tag_mode (string, optional)        — "ByCategory" (default) | "Multicategory" |
    ///                                        "Material".
    ///
    /// Notes:
    ///   - For element_ids that resolve to elements whose category has no loaded
    ///     tag family, the tag creation will fail per-element and the count of
    ///     skipped tags is reported. Use revit_get_family_types(category="...Tags")
    ///     to discover loaded tag families first.
    ///   - Tag location heuristics:
    ///       LocationPoint   → tag at the point
    ///       LocationCurve   → tag at the curve mid-point
    ///       Otherwise       → tag at the element's bounding-box mid-point in view
    ///
    /// Harness Tier 1:
    ///   - Single transaction wraps the whole batch — failure mid-flight rolls back.
    ///   - Idempotency cache: side-effect command, cached on idempotency_key (15min).
    ///   - Post-creation verification: re-query the created tag ids and confirm
    ///     count matches.
    /// </summary>
    public class TagByFilterCommand : IRevitCommand
    {
        public string Name => "tag_by_filter";
        public string Category => "Create";

        public Task<CommandResult> ExecuteAsync(
            Document doc,
            Dictionary<string, object> parameters,
            CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                parameters = parameters ?? new Dictionary<string, object>();
                if (!TryGetBoundedInt(
                        parameters,
                        "max_elements",
                        defaultValue: 500,
                        minValue: 1,
                        maxValue: 500,
                        out var maxElements,
                        out var maxElementsError))
                {
                    return Task.FromResult(CommandResult.Fail(
                        maxElementsError,
                        "Pass max_elements as an integer from 1 through 500."));
                }
                if (!RawParameterValidation.TryGetOptionalStrictBool(
                        parameters,
                        "has_leader",
                        defaultValue: false,
                        out var hasLeader,
                        out var hasLeaderError))
                {
                    return Task.FromResult(CommandResult.Fail(
                        hasLeaderError,
                        "Pass has_leader as true or false, or omit it to use false."));
                }
                if (!RawParameterValidation.TryGetOptionalFiniteDouble(
                        parameters,
                        "offset_x_feet",
                        defaultValue: 0,
                        out var offsetX,
                        out var offsetXError))
                {
                    return Task.FromResult(CommandResult.Fail(
                        offsetXError,
                        "Pass offset_x_feet as a finite number in feet, or omit it to use 0."));
                }
                if (!RawParameterValidation.TryGetOptionalFiniteDouble(
                        parameters,
                        "offset_y_feet",
                        defaultValue: 0,
                        out var offsetY,
                        out var offsetYError))
                {
                    return Task.FromResult(CommandResult.Fail(
                        offsetYError,
                        "Pass offset_y_feet as a finite number in feet, or omit it to use 0."));
                }

                // ─── View ───
                var view = ResolveView(doc, parameters);
                if (view == null)
                    return Task.FromResult(CommandResult.Fail(
                        "Could not resolve target view.",
                        "Pass view_id, or ensure a graphical view is active. Tags can only be placed in graphical views (plan/section/elevation/3D)."));
                if (view.IsTemplate || !view.CanBePrinted && view.ViewType == ViewType.Schedule)
                    return Task.FromResult(CommandResult.Fail(
                        $"View '{view.Name}' (type {view.ViewType}) does not support tag placement.",
                        "Switch to a plan/section/elevation/3D view."));

                // ─── Tag options ───
                var orientationStr = (parameters.TryGetValue("orientation", out var orObj) ? orObj?.ToString() : null)?.ToLowerInvariant();
                var orientation = orientationStr == "vertical" ? TagOrientation.Vertical : TagOrientation.Horizontal;

                var modeStr = (parameters.TryGetValue("tag_mode", out var tmObj) ? tmObj?.ToString() : null)?.ToLowerInvariant();
                TagMode tagMode;
                string tagModeName;
                switch (modeStr)
                {
                    case "multicategory":
                        tagMode = TagMode.TM_ADDBY_MULTICATEGORY;
                        tagModeName = "Multicategory";
                        break;
                    case "material":
                        tagMode = TagMode.TM_ADDBY_MATERIAL;
                        tagModeName = "Material";
                        break;
                    case null:
                    case "":
                    case "bycategory":
                        tagMode = TagMode.TM_ADDBY_CATEGORY;
                        tagModeName = "ByCategory";
                        break;
                    default:
                        return Task.FromResult(CommandResult.Fail(
                            $"Unsupported tag_mode '{tmObj}'.",
                            "Use ByCategory, Multicategory, or Material."));
                }

                ElementId tagTypeId = ElementId.InvalidElementId;
                var hasExplicitTagType =
                    parameters.TryGetValue("tag_type_id", out var ttObj) && ttObj != null;
                if (hasExplicitTagType && tagMode != TagMode.TM_ADDBY_CATEGORY)
                {
                    return Task.FromResult(CommandResult.Fail(
                        $"tag_type_id cannot be combined with tag_mode=\"{tagModeName}\".",
                        "Omit tag_type_id so Revit can resolve the requested tag mode, " +
                        "or use tag_mode=\"ByCategory\" with the explicit tag type."));
                }

                if (hasExplicitTagType)
                {
                    try
                    {
                        var requestedId = ElementIdCompatibility.Create(Convert.ToInt64(ttObj));
                        if (!(doc.GetElement(requestedId) is FamilySymbol fs))
                        {
                            return Task.FromResult(CommandResult.Fail(
                                $"tag_type_id {ttObj} is not a valid tag FamilySymbol.",
                                "Use revit_get_family_types for the matching tag category, " +
                                "or omit tag_type_id to use Revit's default tag type."));
                        }
                        tagTypeId = fs.Id;
                    }
                    catch (Exception ex)
                    {
                        return Task.FromResult(CommandResult.Fail(
                            $"Invalid tag_type_id '{ttObj}': {ex.Message}",
                            "Provide a positive tag FamilySymbol ElementId, or omit " +
                            "tag_type_id to use Revit's default tag type."));
                    }
                }

                // ─── Resolve elements ───
                var selectorOpts = BuildSelector(parameters, view.Id, maxElements);
                var sel = ElementSelector.Resolve(doc, selectorOpts);
                if (sel.Elements.Count == 0)
                {
                    return Task.FromResult(CommandResult.Fail(
                        "No elements matched the selector.",
                        $"Filters tried: [{string.Join(", ", sel.AppliedFilters)}]. " +
                        "Use revit_query_elements to verify the selector, or pass element_ids directly."));
                }

                // ─── Place tags ───
                var createdTagIds = new List<long>();
                var skipped = new List<Dictionary<string, object>>();

                using (var tx = new Transaction(doc, $"MCP: Tag by filter ({sel.Elements.Count})"))
                {
                    tx.Start();
                    foreach (var elem in sel.Elements)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var anchor = ResolveAnchorPoint(elem, view);
                        if (anchor == null)
                        {
                            skipped.Add(new Dictionary<string, object>
                            {
                                ["element_id"] = elem.Id.GetValue(),
                                ["reason"] = "no_anchor_point"
                            });
                            continue;
                        }

                        var tagPoint = new XYZ(anchor.X + offsetX, anchor.Y + offsetY, anchor.Z);

                        IndependentTag tag = null;
                        try
                        {
                            var reference = new Reference(elem);
                            tag = tagTypeId != ElementId.InvalidElementId
                                ? IndependentTag.Create(
                                    doc,
                                    tagTypeId,
                                    view.Id,
                                    reference,
                                    hasLeader,
                                    orientation,
                                    tagPoint)
                                : IndependentTag.Create(
                                    doc,
                                    view.Id,
                                    reference,
                                    hasLeader,
                                    tagMode,
                                    orientation,
                                    tagPoint);
                        }
                        catch (Exception ex)
                        {
                            skipped.Add(new Dictionary<string, object>
                            {
                                ["element_id"] = elem.Id.GetValue(),
                                ["reason"] = "create_failed: " + ex.Message
                            });
                            continue;
                        }
                        if (tag != null) createdTagIds.Add(tag.Id.GetValue());
                    }

                    if (createdTagIds.Count == 0)
                    {
                        tx.RollBack();
                        var firstReason = skipped.Count > 0 &&
                                          skipped[0].TryGetValue("reason", out var reason)
                            ? reason?.ToString()
                            : "No tag was returned by Revit.";
                        return Task.FromResult(CommandResult.Fail(
                            $"No tags were created for {sel.Elements.Count} matched elements. " +
                            $"First failure: {firstReason}",
                            "Load a compatible tag family, verify tag_mode/tag_type_id and " +
                            "the target view, then retry with the same selector."));
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    tx.CommitOrThrow();
                }

                // ─── Harness Tier 1: post-creation verification ───
                int verifiedCount = 0;
                var verification = new Dictionary<string, object>();
                try
                {
                foreach (var id in createdTagIds)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (doc.GetElement(ElementIdCompatibility.Create(id)) is IndependentTag) verifiedCount++;
                }
                verification = new Dictionary<string, object>
                {
                    ["performed"] = true,
                    ["expected_count"] = createdTagIds.Count,
                    ["actual_count"] = verifiedCount,
                    ["count_match"] = verifiedCount == createdTagIds.Count,
                    ["match"] = verifiedCount == createdTagIds.Count,
                };

                }
                catch (Exception verificationError)
                {
                    verification.Clear();
                    verification["performed"] = false;
                    verification["match"] = false;
                    verification["error"] = verificationError.Message;
                }

                var result = new Dictionary<string, object>
                {
                    ["view_id"] = view.Id.GetValue(),
                    ["view_name"] = view.Name,
                    ["matched_count"] = sel.Elements.Count,
                    ["created_count"] = createdTagIds.Count,
                    ["skipped_count"] = skipped.Count,
                    ["truncated"] = sel.TruncatedToMaxCount,
                    ["filters"] = sel.AppliedFilters,
                    ["tag_mode"] = tagModeName,
                    ["tag_type_source"] = hasExplicitTagType
                        ? "explicit_tag_type_id"
                        : "revit_default_for_tag_mode",
                    ["orientation"] = orientation.ToString(),
                    ["has_leader"] = hasLeader,
                    ["created_tag_ids"] = createdTagIds.Take(50).ToList(),
                    ["created_tag_ids_truncated"] = createdTagIds.Count > 50,
                    ["skipped_sample"] = skipped.Take(10).ToList(),
                    ["verification"] = verification,
                    ["mutation_committed"] = createdTagIds.Count > 0,
                };
                return Task.FromResult(CommandResult.Ok(result));
            }
            catch (OperationCanceledException)
            {
                return Task.FromResult(CommandResult.Fail(
                    "Operation cancelled — transaction rolled back.",
                    "Reduce the matched set with a tighter selector."));
            }
            catch (Exception ex)
            {
                return Task.FromResult(CommandResult.Fail(
                    $"Failed to tag elements: {ex.Message}",
                    "Verify the view supports tags and a tag family is loaded for the category."));
            }
        }

        // ─── Helpers ───

        private static Autodesk.Revit.DB.View ResolveView(Document doc, Dictionary<string, object> parameters)
        {
            if (parameters.TryGetValue("view_id", out var vidObj) && vidObj != null)
            {
                try
                {
                    var view = doc.GetElement(ElementIdCompatibility.Create(Convert.ToInt64(vidObj))) as Autodesk.Revit.DB.View;
                    if (view != null && !view.IsTemplate) return view;
                }
                catch { }
                return null;
            }
            return doc.ActiveView;
        }

        private static ElementSelectorOptions BuildSelector(
            Dictionary<string, object> p,
            ElementId viewId,
            int maxElements)
        {
            var opts = new ElementSelectorOptions
            {
                ViewId = viewId,
                MaxCount = maxElements,
            };

            if (p.TryGetValue("element_ids", out var eidsObj) && eidsObj is System.Collections.IEnumerable eids && !(eidsObj is string))
            {
                opts.ElementIds = new List<long>();
                foreach (var item in eids)
                {
                    try { opts.ElementIds.Add(Convert.ToInt64(item)); }
                    catch
                    {
                        throw new ArgumentException(
                            $"element_ids contains a non-integer value: '{item}'.");
                    }
                }
            }

            opts.Category = p.TryGetValue("category", out var c) ? c?.ToString() : null;
            opts.TypeNameContains = p.TryGetValue("type_name_contains", out var tn) ? tn?.ToString() : null;
            opts.TypeNameStartsWith = p.TryGetValue("type_name_starts_with", out var ts) ? ts?.ToString() : null;
            opts.MarkContains = p.TryGetValue("mark_contains", out var mc) ? mc?.ToString() : null;
            opts.ParameterName = p.TryGetValue("parameter_name", out var pn) ? pn?.ToString() : null;
            opts.ParameterValueContains = p.TryGetValue("parameter_value_contains", out var pv) ? pv?.ToString() : null;
            opts.LevelName = p.TryGetValue("level_name", out var ln) ? ln?.ToString() : null;
            return opts;
        }

        private static XYZ ResolveAnchorPoint(Element elem, Autodesk.Revit.DB.View view)
        {
            try
            {
                if (elem.Location is LocationPoint lp) return lp.Point;
                if (elem.Location is LocationCurve lc)
                {
                    // Mid-point of the curve
                    return lc.Curve.Evaluate(0.5, true);
                }
                // Fallback: view-specific bounding box centre
                var bb = elem.get_BoundingBox(view) ?? elem.get_BoundingBox(null);
                if (bb != null)
                    return (bb.Min + bb.Max) * 0.5;
            }
            catch { }
            return null;
        }

        private static bool TryGetBoundedInt(
            Dictionary<string, object> p,
            string key,
            int defaultValue,
            int minValue,
            int maxValue,
            out int value,
            out string error)
        {
            value = defaultValue;
            error = null;
            if (!p.TryGetValue(key, out var raw))
                return true;

            long parsed;
            switch (raw)
            {
                case int i:
                    parsed = i;
                    break;
                case long l:
                    parsed = l;
                    break;
                case double d when !double.IsNaN(d) &&
                                   !double.IsInfinity(d) &&
                                   d == Math.Truncate(d) &&
                                   d >= long.MinValue &&
                                   d <= long.MaxValue:
                    parsed = (long)d;
                    break;
                default:
                    error = $"{key} must be an integer from {minValue} through {maxValue}.";
                    return false;
            }

            if (parsed < minValue || parsed > maxValue)
            {
                error =
                    $"{key} must be from {minValue} through {maxValue}; received {parsed}.";
                return false;
            }

            value = (int)parsed;
            return true;
        }
    }
}
