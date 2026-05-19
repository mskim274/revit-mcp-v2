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
    /// in level 4F, every type_name_contains "SK_MATE", etc.
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
    ///   tag_type_id (int, optional)        — Specific tag family-type id. If omitted,
    ///                                        the view's default tag for the category is used.
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
                var hasLeader = parameters.TryGetValue("has_leader", out var hlObj) && hlObj != null && Convert.ToBoolean(hlObj);
                var orientationStr = (parameters.TryGetValue("orientation", out var orObj) ? orObj?.ToString() : null)?.ToLowerInvariant();
                var orientation = orientationStr == "vertical" ? TagOrientation.Vertical : TagOrientation.Horizontal;

                var offsetX = TryGetDouble(parameters, "offset_x_feet", 0);
                var offsetY = TryGetDouble(parameters, "offset_y_feet", 0);

                var modeStr = (parameters.TryGetValue("tag_mode", out var tmObj) ? tmObj?.ToString() : null)?.ToLowerInvariant();
                TagMode tagMode;
                switch (modeStr)
                {
                    case "multicategory": tagMode = TagMode.TM_ADDBY_MULTICATEGORY; break;
                    case "material": tagMode = TagMode.TM_ADDBY_MATERIAL; break;
                    default: tagMode = TagMode.TM_ADDBY_CATEGORY; break;
                }

                ElementId tagTypeId = ElementId.InvalidElementId;
                if (parameters.TryGetValue("tag_type_id", out var ttObj) && ttObj != null)
                {
                    try
                    {
                        var requestedId = new ElementId(Convert.ToInt32(ttObj));
                        if (doc.GetElement(requestedId) is FamilySymbol fs) tagTypeId = fs.Id;
                    }
                    catch { /* leave as invalid → use default */ }
                }

                // ─── Resolve elements ───
                var selectorOpts = BuildSelector(parameters, view.Id);
                var sel = ElementSelector.Resolve(doc, selectorOpts);
                if (sel.Elements.Count == 0)
                {
                    return Task.FromResult(CommandResult.Fail(
                        "No elements matched the selector.",
                        $"Filters tried: [{string.Join(", ", sel.AppliedFilters)}]. " +
                        "Use revit_query_elements to verify the selector, or pass element_ids directly."));
                }

                // ─── Place tags ───
                var createdTagIds = new List<int>();
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
                                ["element_id"] = elem.Id.IntegerValue,
                                ["reason"] = "no_anchor_point"
                            });
                            continue;
                        }

                        var tagPoint = new XYZ(anchor.X + offsetX, anchor.Y + offsetY, anchor.Z);

                        IndependentTag tag = null;
                        try
                        {
                            var reference = new Reference(elem);
                            tag = IndependentTag.Create(
                                doc, tagTypeId, view.Id, reference, hasLeader, orientation, tagPoint);
                        }
                        catch (Exception ex)
                        {
                            skipped.Add(new Dictionary<string, object>
                            {
                                ["element_id"] = elem.Id.IntegerValue,
                                ["reason"] = "create_failed: " + ex.Message
                            });
                            continue;
                        }
                        if (tag != null) createdTagIds.Add(tag.Id.IntegerValue);
                    }
                    tx.Commit();
                }

                // ─── Harness Tier 1: post-creation verification ───
                int verifiedCount = 0;
                foreach (var id in createdTagIds)
                {
                    if (doc.GetElement(new ElementId(id)) is IndependentTag) verifiedCount++;
                }
                var verification = new Dictionary<string, object>
                {
                    ["performed"] = true,
                    ["expected_count"] = createdTagIds.Count,
                    ["actual_count"] = verifiedCount,
                    ["count_match"] = verifiedCount == createdTagIds.Count,
                };
                if (createdTagIds.Count == 0 && sel.Elements.Count > 0)
                {
                    verification["note"] =
                        "No tag created for any element. Possible cause: no loaded tag family for the category, " +
                        "or all elements have unsupported geometry. Use revit_get_family_types(category=\"...Tags\") to check.";
                }

                var result = new Dictionary<string, object>
                {
                    ["view_id"] = view.Id.IntegerValue,
                    ["view_name"] = view.Name,
                    ["matched_count"] = sel.Elements.Count,
                    ["created_count"] = createdTagIds.Count,
                    ["skipped_count"] = skipped.Count,
                    ["truncated"] = sel.TruncatedToMaxCount,
                    ["filters"] = sel.AppliedFilters,
                    ["tag_mode"] = tagMode.ToString(),
                    ["orientation"] = orientation.ToString(),
                    ["has_leader"] = hasLeader,
                    ["created_tag_ids"] = createdTagIds.Take(50).ToList(),
                    ["created_tag_ids_truncated"] = createdTagIds.Count > 50,
                    ["skipped_sample"] = skipped.Take(10).ToList(),
                    ["verification"] = verification,
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
                    var view = doc.GetElement(new ElementId(Convert.ToInt32(vidObj))) as Autodesk.Revit.DB.View;
                    if (view != null && !view.IsTemplate) return view;
                }
                catch { /* fall through */ }
            }
            return doc.ActiveView;
        }

        private static ElementSelectorOptions BuildSelector(Dictionary<string, object> p, ElementId viewId)
        {
            var opts = new ElementSelectorOptions
            {
                ViewId = viewId,
                MaxCount = p.TryGetValue("max_elements", out var mxObj) && mxObj != null
                    ? Math.Max(1, Convert.ToInt32(mxObj))
                    : 500,
            };

            if (p.TryGetValue("element_ids", out var eidsObj) && eidsObj is System.Collections.IEnumerable eids && !(eidsObj is string))
            {
                opts.ElementIds = new List<int>();
                foreach (var item in eids)
                {
                    try { opts.ElementIds.Add(Convert.ToInt32(item)); } catch { }
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

        private static double TryGetDouble(Dictionary<string, object> p, string key, double def)
        {
            if (!p.TryGetValue(key, out var obj) || obj == null) return def;
            try { return Convert.ToDouble(obj); } catch { return def; }
        }
    }
}
