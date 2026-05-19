using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using RevitMCP.CommandSet.Helpers;
using RevitMCP.CommandSet.Interfaces;

namespace RevitMCP.CommandSet.Commands.View
{
    /// <summary>
    /// Apply or clear a per-element graphic override (color) on a view.
    ///
    /// Use case: visualize "어떤 type이 미반영", "SK_MATE 타입 강조" 같은 검토 결과 —
    /// override line + surface fill so the reviewer can spot them at a glance.
    ///
    /// Parameters:
    ///   view_id (int, optional)            — Target view. Default = active view.
    ///   mode (string, optional)            — "apply" (default) or "clear".
    ///
    ///   // Selector (use any combination, OR pass element_ids directly):
    ///   element_ids (int[], optional)      — Explicit element ids (highest priority).
    ///   category (string, optional)        — "Walls", "StructuralFraming", ...
    ///   type_name_contains (string, optional)
    ///   type_name_starts_with (string, optional)
    ///   mark_contains (string, optional)
    ///   parameter_name (string, optional)  — pair with parameter_value_contains
    ///   parameter_value_contains (string, optional)
    ///   level_name (string, optional)
    ///   max_elements (int, optional)       — Default 5000.
    ///
    ///   // Color (apply mode only):
    ///   color (string, optional)           — "r,g,b" (0-255 each) OR preset:
    ///                                        "red" | "orange" | "yellow" | "green" |
    ///                                        "blue" | "magenta" | "cyan" | "gray".
    ///                                        Default "red".
    ///   surface_fill (bool, optional)      — Apply solid fill on surface/cut (default true).
    ///   transparency (int, optional)       — 0-100 (default 0 = opaque).
    ///   halftone (bool, optional)          — Apply halftone (default false).
    /// </summary>
    public class ApplyColorFilterCommand : IRevitCommand
    {
        public string Name => "apply_color_filter";
        public string Category => "View";

        public Task<CommandResult> ExecuteAsync(
            Document doc,
            Dictionary<string, object> parameters,
            CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                parameters = parameters ?? new Dictionary<string, object>();

                // ─── Resolve target view ───
                var view = ResolveView(doc, parameters);
                if (view == null)
                    return Task.FromResult(CommandResult.Fail(
                        "Could not resolve target view.",
                        "Pass view_id, or ensure a non-template view is active. " +
                        "Use revit_get_views to list views."));

                // Some view types reject SetElementOverrides
                if (view.ViewType == ViewType.SystemBrowser ||
                    view.ViewType == ViewType.ProjectBrowser ||
                    view.ViewType == ViewType.Schedule)
                {
                    return Task.FromResult(CommandResult.Fail(
                        $"View type {view.ViewType} does not support graphic overrides.",
                        "Use a graphical view (FloorPlan, Section, Elevation, ThreeD, etc.)."));
                }

                var mode = (parameters.TryGetValue("mode", out var modeObj) ? modeObj?.ToString() : null)?.ToLowerInvariant() ?? "apply";
                if (mode != "apply" && mode != "clear")
                    return Task.FromResult(CommandResult.Fail(
                        $"Invalid mode '{mode}'.",
                        "Use mode=\"apply\" (set override) or mode=\"clear\" (remove override)."));

                // ─── Resolve elements ───
                var selectorOpts = BuildSelector(parameters, view.Id);
                var sel = ElementSelector.Resolve(doc, selectorOpts);
                if (sel.Elements.Count == 0)
                {
                    return Task.FromResult(CommandResult.Fail(
                        "No elements matched the selector.",
                        $"Filters tried: [{string.Join(", ", sel.AppliedFilters)}]. " +
                        "Use revit_query_elements to verify the category/type exists, " +
                        "or pass element_ids directly."));
                }

                // ─── Build OverrideGraphicSettings ───
                OverrideGraphicSettings ogs;
                Color appliedColor = null;
                bool appliedHalftone = false;
                int appliedTransparency = 0;

                if (mode == "clear")
                {
                    // Empty OGS resets every override to "by category"
                    ogs = new OverrideGraphicSettings();
                }
                else
                {
                    var colorStr = (parameters.TryGetValue("color", out var cObj) ? cObj?.ToString() : null);
                    appliedColor = ParseColor(colorStr) ?? new Color(255, 0, 0); // default red

                    var surfaceFill = !parameters.TryGetValue("surface_fill", out var sfObj) || sfObj == null || Convert.ToBoolean(sfObj);
                    appliedHalftone = parameters.TryGetValue("halftone", out var htObj) && htObj != null && Convert.ToBoolean(htObj);
                    appliedTransparency = ClampInt(parameters.TryGetValue("transparency", out var trObj) ? trObj : 0, 0, 100);

                    ogs = new OverrideGraphicSettings();
                    ogs.SetProjectionLineColor(appliedColor);
                    ogs.SetCutLineColor(appliedColor);

                    if (surfaceFill)
                    {
                        var solidPatternId = GetSolidFillPatternId(doc);
                        if (solidPatternId != null && solidPatternId != ElementId.InvalidElementId)
                        {
                            ogs.SetSurfaceForegroundPatternId(solidPatternId);
                            ogs.SetSurfaceForegroundPatternColor(appliedColor);
                            ogs.SetSurfaceForegroundPatternVisible(true);
                            ogs.SetCutForegroundPatternId(solidPatternId);
                            ogs.SetCutForegroundPatternColor(appliedColor);
                            ogs.SetCutForegroundPatternVisible(true);
                        }
                    }

                    ogs.SetSurfaceTransparency(appliedTransparency);
                    ogs.SetHalftone(appliedHalftone);
                }

                // ─── Apply (transactional) ───
                int applied = 0;
                int skipped = 0;
                using (var tx = new Transaction(doc, $"MCP: {(mode == "apply" ? "Color filter" : "Clear color filter")}"))
                {
                    tx.Start();
                    foreach (var elem in sel.Elements)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            view.SetElementOverrides(elem.Id, ogs);
                            applied++;
                        }
                        catch
                        {
                            // Some elements (view-specific elements, model lines in 3D, etc.) reject overrides
                            skipped++;
                        }
                    }
                    tx.Commit();
                }

                // ─── Harness Tier 1: post-tx verification ───
                // Re-read the first applied element's override and confirm color matches.
                var verification = new Dictionary<string, object> { ["performed"] = true };
                if (mode == "apply" && applied > 0)
                {
                    var firstId = sel.Elements
                        .Select(e => e.Id)
                        .FirstOrDefault(id =>
                        {
                            try { return view.GetElementOverrides(id) != null; }
                            catch { return false; }
                        });
                    if (firstId != null && firstId != ElementId.InvalidElementId)
                    {
                        var actual = view.GetElementOverrides(firstId);
                        var actualColor = actual.ProjectionLineColor;
                        var match = actualColor != null && actualColor.IsValid
                                    && actualColor.Red == appliedColor.Red
                                    && actualColor.Green == appliedColor.Green
                                    && actualColor.Blue == appliedColor.Blue;
                        verification["color_match"] = match;
                        verification["sample_element_id"] = firstId.IntegerValue;
                        verification["sample_color_rgb"] = actualColor != null && actualColor.IsValid
                            ? $"{actualColor.Red},{actualColor.Green},{actualColor.Blue}"
                            : "(unset)";
                    }
                    else
                    {
                        verification["color_match"] = false;
                        verification["note"] = "Could not re-fetch any overridden element to verify.";
                    }
                }
                else if (mode == "clear" && applied > 0)
                {
                    // After clear, override should be the empty default — projection line color should be invalid/unset.
                    var firstId = sel.Elements[0].Id;
                    var actual = view.GetElementOverrides(firstId);
                    var col = actual.ProjectionLineColor;
                    verification["cleared"] = col == null || !col.IsValid;
                    verification["sample_element_id"] = firstId.IntegerValue;
                }

                // ─── Result ───
                var result = new Dictionary<string, object>
                {
                    ["view_id"] = view.Id.IntegerValue,
                    ["view_name"] = view.Name,
                    ["mode"] = mode,
                    ["matched_count"] = sel.Elements.Count,
                    ["applied_count"] = applied,
                    ["skipped_count"] = skipped,
                    ["truncated"] = sel.TruncatedToMaxCount,
                    ["filters"] = sel.AppliedFilters,
                    ["verification"] = verification,
                };
                if (mode == "apply")
                {
                    result["color_rgb"] = $"{appliedColor.Red},{appliedColor.Green},{appliedColor.Blue}";
                    result["transparency"] = appliedTransparency;
                    result["halftone"] = appliedHalftone;
                }
                return Task.FromResult(CommandResult.Ok(result));
            }
            catch (OperationCanceledException)
            {
                return Task.FromResult(CommandResult.Fail(
                    "Operation cancelled.",
                    "Reduce the matched set with a tighter selector, or split the work in smaller batches."));
            }
            catch (Exception ex)
            {
                return Task.FromResult(CommandResult.Fail(
                    $"Failed to apply color filter: {ex.Message}",
                    "Verify view supports overrides and selector is valid."));
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
                    : 5000,
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

        private static Color ParseColor(string spec)
        {
            if (string.IsNullOrWhiteSpace(spec)) return null;
            spec = spec.Trim().ToLowerInvariant();

            // Presets
            switch (spec)
            {
                case "red": return new Color(255, 0, 0);
                case "orange": return new Color(255, 128, 0);
                case "yellow": return new Color(255, 255, 0);
                case "green": return new Color(0, 200, 0);
                case "blue": return new Color(0, 100, 255);
                case "magenta": return new Color(255, 0, 200);
                case "cyan": return new Color(0, 200, 255);
                case "gray":
                case "grey": return new Color(160, 160, 160);
            }

            // "r,g,b"
            var parts = spec.Split(',');
            if (parts.Length == 3
                && byte.TryParse(parts[0].Trim(), out var r)
                && byte.TryParse(parts[1].Trim(), out var g)
                && byte.TryParse(parts[2].Trim(), out var b))
            {
                return new Color(r, g, b);
            }
            return null;
        }

        private static int ClampInt(object obj, int min, int max)
        {
            try
            {
                var v = Convert.ToInt32(obj);
                return Math.Max(min, Math.Min(max, v));
            }
            catch { return min; }
        }

        private static ElementId GetSolidFillPatternId(Document doc)
        {
            // Cache could go in Application but the lookup is cheap enough
            var solid = new FilteredElementCollector(doc)
                .OfClass(typeof(FillPatternElement))
                .Cast<FillPatternElement>()
                .FirstOrDefault(fp =>
                {
                    try
                    {
                        var pat = fp.GetFillPattern();
                        return pat != null && pat.IsSolidFill;
                    }
                    catch { return false; }
                });
            return solid?.Id;
        }
    }
}
