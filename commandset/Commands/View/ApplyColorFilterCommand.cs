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
    /// Use case: visualize unmatched types or highlight a review group by
    /// overriding line and surface fill so reviewers can spot them at a glance.
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
                if (!TryGetBoundedInt(
                        parameters,
                        "max_elements",
                        defaultValue: 5000,
                        minValue: 1,
                        maxValue: 50_000,
                        out var maxElements,
                        out var maxElementsError))
                {
                    return Task.FromResult(CommandResult.Fail(
                        maxElementsError,
                        "Pass max_elements as an integer from 1 through 50000."));
                }
                if (!TryGetBoundedInt(
                        parameters,
                        "transparency",
                        defaultValue: 0,
                        minValue: 0,
                        maxValue: 100,
                        out var requestedTransparency,
                        out var transparencyError))
                {
                    return Task.FromResult(CommandResult.Fail(
                        transparencyError,
                        "Pass transparency as an integer from 0 through 100."));
                }
                if (!RawParameterValidation.TryGetOptionalStrictBool(
                        parameters,
                        "surface_fill",
                        defaultValue: true,
                        out var requestedSurfaceFill,
                        out var surfaceFillError))
                {
                    return Task.FromResult(CommandResult.Fail(
                        surfaceFillError,
                        "Pass surface_fill as true or false, or omit it to use true."));
                }
                if (!RawParameterValidation.TryGetOptionalStrictBool(
                        parameters,
                        "halftone",
                        defaultValue: false,
                        out var requestedHalftone,
                        out var halftoneError))
                {
                    return Task.FromResult(CommandResult.Fail(
                        halftoneError,
                        "Pass halftone as true or false, or omit it to use false."));
                }

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
                var selectorOpts = BuildSelector(parameters, view.Id, maxElements);
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
                bool appliedSurfaceFill = false;
                bool appliedHalftone = false;
                int appliedTransparency = 0;

                if (mode == "clear")
                {
                    // Empty OGS resets every override to "by category"
                    ogs = new OverrideGraphicSettings();
                }
                else
                {
                    var colorStr =
                        parameters.TryGetValue("color", out var cObj)
                            ? cObj?.ToString()
                            : null;
                    if (string.IsNullOrWhiteSpace(colorStr))
                        colorStr = "red";
                    appliedColor = ParseColor(colorStr);
                    if (appliedColor == null)
                    {
                        return Task.FromResult(CommandResult.Fail(
                            $"Unsupported color '{colorStr}'.",
                            "Use red, orange, yellow, green, blue, magenta, " +
                            "cyan, gray, or an r,g,b triple with values 0-255."));
                    }

                    appliedSurfaceFill = requestedSurfaceFill;
                    appliedHalftone = requestedHalftone;
                    appliedTransparency = requestedTransparency;

                    ogs = new OverrideGraphicSettings();
                    ogs.SetProjectionLineColor(appliedColor);
                    ogs.SetCutLineColor(appliedColor);

                    if (appliedSurfaceFill)
                    {
                        var solidPatternId = GetSolidFillPatternId(doc);
                        if (solidPatternId == null ||
                            solidPatternId == ElementId.InvalidElementId)
                        {
                            return Task.FromResult(CommandResult.Fail(
                                "No solid fill pattern is available for surface_fill=true.",
                                "Load or restore Revit's solid drafting fill pattern, " +
                                "or retry with surface_fill=false."));
                        }

                        ogs.SetSurfaceForegroundPatternId(solidPatternId);
                        ogs.SetSurfaceForegroundPatternColor(appliedColor);
                        ogs.SetSurfaceForegroundPatternVisible(true);
                        ogs.SetCutForegroundPatternId(solidPatternId);
                        ogs.SetCutForegroundPatternColor(appliedColor);
                        ogs.SetCutForegroundPatternVisible(true);
                    }

                    ogs.SetSurfaceTransparency(appliedTransparency);
                    ogs.SetHalftone(appliedHalftone);
                }

                // ─── Apply (transactional) ───
                int applied = 0;
                var appliedIds = new List<ElementId>();
                var skipped = new List<Dictionary<string, object>>();
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
                            appliedIds.Add(elem.Id);
                        }
                        catch (Exception ex)
                        {
                            skipped.Add(new Dictionary<string, object>
                            {
                                ["element_id"] = elem.Id.GetValue(),
                                ["reason"] = ex.Message
                            });
                        }
                    }
                    if (appliedIds.Count == 0)
                    {
                        tx.RollBack();
                        var firstReason = skipped.Count > 0 &&
                                          skipped[0].TryGetValue("reason", out var reason)
                            ? reason?.ToString()
                            : "Revit did not accept any override.";
                        var verb = mode == "apply" ? "applied" : "cleared";
                        return Task.FromResult(CommandResult.Fail(
                            $"No graphic overrides were {verb} for " +
                            $"{sel.Elements.Count} matched elements. " +
                            $"First failure: {firstReason}",
                            "Use a graphical view and verify that the selected elements " +
                            "can receive view-specific overrides."));
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    tx.CommitOrThrow();
                }

                // ─── Harness Tier 1: post-tx verification ───
                // Re-read the first applied element's override and confirm color matches.
                var verification = new Dictionary<string, object>();
                try
                {
                    var firstId = appliedIds[0];
                    var actual = view.GetElementOverrides(firstId);
                    bool match;
                    if (mode == "apply")
                    {
                        var actualColor = actual.ProjectionLineColor;
                        var projectionColorMatch =
                            ColorsEqual(actualColor, appliedColor);
                        var cutColorMatch =
                            ColorsEqual(actual.CutLineColor, appliedColor);
                        var transparencyMatch =
                            actual.Transparency == appliedTransparency;
                        var halftoneMatch =
                            actual.Halftone == appliedHalftone;
                        var surfaceFillMatch =
                            !appliedSurfaceFill ||
                            (actual.SurfaceForegroundPatternId != null &&
                             actual.SurfaceForegroundPatternId !=
                             ElementId.InvalidElementId &&
                             actual.CutForegroundPatternId != null &&
                             actual.CutForegroundPatternId !=
                             ElementId.InvalidElementId &&
                             ColorsEqual(
                                 actual.SurfaceForegroundPatternColor,
                                 appliedColor) &&
                             ColorsEqual(
                                 actual.CutForegroundPatternColor,
                                 appliedColor));
                        match = projectionColorMatch &&
                                cutColorMatch &&
                                transparencyMatch &&
                                halftoneMatch &&
                                surfaceFillMatch;

                        verification["projection_color_match"] =
                            projectionColorMatch;
                        verification["cut_color_match"] = cutColorMatch;
                        verification["transparency_match"] =
                            transparencyMatch;
                        verification["halftone_match"] = halftoneMatch;
                        verification["surface_fill_match"] =
                            surfaceFillMatch;
                        verification["color_match"] =
                            projectionColorMatch && cutColorMatch;
                        verification["sample_element_id"] = firstId.GetValue();
                        verification["sample_color_rgb"] =
                            actualColor != null && actualColor.IsValid
                                ? $"{actualColor.Red},{actualColor.Green},{actualColor.Blue}"
                                : "(unset)";
                        verification["sample_transparency"] =
                            actual.Transparency;
                        verification["sample_halftone"] = actual.Halftone;
                    }
                    else
                    {
                        var projectionCleared =
                            IsUnsetColor(actual.ProjectionLineColor);
                        var cutCleared = IsUnsetColor(actual.CutLineColor);
                        var surfacePatternCleared =
                            actual.SurfaceForegroundPatternId == null ||
                            actual.SurfaceForegroundPatternId ==
                            ElementId.InvalidElementId;
                        var cutPatternCleared =
                            actual.CutForegroundPatternId == null ||
                            actual.CutForegroundPatternId ==
                            ElementId.InvalidElementId;
                        var transparencyCleared =
                            actual.Transparency == 0;
                        var halftoneCleared = !actual.Halftone;
                        match = projectionCleared &&
                                cutCleared &&
                                surfacePatternCleared &&
                                cutPatternCleared &&
                                transparencyCleared &&
                                halftoneCleared;

                        verification["projection_color_cleared"] =
                            projectionCleared;
                        verification["cut_color_cleared"] = cutCleared;
                        verification["surface_pattern_cleared"] =
                            surfacePatternCleared;
                        verification["cut_pattern_cleared"] =
                            cutPatternCleared;
                        verification["transparency_cleared"] =
                            transparencyCleared;
                        verification["halftone_cleared"] = halftoneCleared;
                        verification["cleared"] = match;
                        verification["sample_element_id"] = firstId.GetValue();
                    }

                    verification["performed"] = true;
                    verification["match"] = match;
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
                    ["mode"] = mode,
                    ["matched_count"] = sel.Elements.Count,
                    ["applied_count"] = applied,
                    ["skipped_count"] = skipped.Count,
                    ["applied_element_ids"] = appliedIds
                        .Take(50)
                        .Select(id => id.GetValue())
                        .ToList(),
                    ["applied_element_ids_truncated"] =
                        appliedIds.Count > 50,
                    ["skipped_sample"] = skipped.Take(25).ToList(),
                    ["truncated"] = sel.TruncatedToMaxCount,
                    ["filters"] = sel.AppliedFilters,
                    ["verification"] = verification,
                    ["mutation_committed"] = appliedIds.Count > 0,
                };
                if (mode == "apply")
                {
                    result["color_rgb"] = $"{appliedColor.Red},{appliedColor.Green},{appliedColor.Blue}";
                    result["transparency"] = appliedTransparency;
                    result["halftone"] = appliedHalftone;
                    result["surface_fill"] = appliedSurfaceFill;
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

        private static bool ColorsEqual(Color actual, Color expected)
        {
            return actual != null &&
                   expected != null &&
                   actual.IsValid &&
                   expected.IsValid &&
                   actual.Red == expected.Red &&
                   actual.Green == expected.Green &&
                   actual.Blue == expected.Blue;
        }

        private static bool IsUnsetColor(Color color)
        {
            return color == null || !color.IsValid;
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
