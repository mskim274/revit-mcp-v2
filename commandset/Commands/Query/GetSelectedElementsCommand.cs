using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using RevitMCP.CommandSet.Interfaces;

namespace RevitMCP.CommandSet.Commands.Query
{
    /// <summary>
    /// Returns the user's current Revit UI selection — the elements they
    /// have selected (highlighted) in Revit before invoking this tool.
    ///
    /// Mirrors the AutoCAD MCP's get_selected_entities. Same shape on both
    /// products so an LLM client can use a uniform "what is the user
    /// looking at" workflow.
    ///
    /// Output (per-element):
    ///   id, name, category, type_name, family_name, level (best-effort),
    ///   plus a small set of hot parameters when include_parameters=true
    ///   (default false to keep responses small).
    /// Plus aggregates: count, by_category, by_level.
    ///
    /// Selection capture: UIDocument.Selection.GetElementIds() runs on the
    /// Revit API thread and is captured by the WebSocket dispatcher into
    /// SelectionContext.Current before this command's ExecuteAsync runs.
    /// </summary>
    public class GetSelectedElementsCommand : IRevitCommand
    {
        public string Name => "get_selected_elements";
        public string Category => "Query";

        public Task<CommandResult> ExecuteAsync(
            Document doc,
            Dictionary<string, object> parameters,
            CancellationToken cancellationToken)
        {
            try
            {
                var includeParams = GetBool(parameters, "include_parameters", false);
                var limit = (int)Math.Max(1, Math.Min(1000, GetLong(parameters, "limit", 500)));

                var ids = SelectionContext.Current;
                if (ids == null || ids.Length == 0)
                {
                    return Task.FromResult(CommandResult.Ok(new Dictionary<string, object>
                    {
                        ["count"] = 0,
                        ["elements"] = new List<object>(),
                        ["note"] = "No active selection. Select elements in Revit first, then re-run.",
                    }));
                }

                int totalSelected = ids.Length;
                int returned = 0;
                var elements = new List<Dictionary<string, object>>();
                var byCategory = new Dictionary<string, int>();
                var byLevel = new Dictionary<string, int>();

                foreach (var id in ids)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var elem = doc.GetElement(id);
                    if (elem == null) continue;

                    var categoryName = elem.Category?.Name ?? "Unknown";
                    Increment(byCategory, categoryName);

                    string levelName = "(none)";
                    try
                    {
                        var levelId = elem.LevelId;
                        if (levelId != null && levelId != ElementId.InvalidElementId)
                        {
                            var lvl = doc.GetElement(levelId) as Level;
                            if (lvl != null) levelName = lvl.Name;
                        }
                    }
                    catch { /* element may not have LevelId */ }
                    Increment(byLevel, levelName);

                    if (returned >= limit) continue;

                    var d = new Dictionary<string, object>
                    {
                        ["id"] = elem.Id.IntegerValue,
                        ["name"] = elem.Name ?? "",
                        ["category"] = categoryName,
                        ["level"] = levelName,
                    };

                    // Type info
                    var typeId = elem.GetTypeId();
                    if (typeId != null && typeId != ElementId.InvalidElementId)
                    {
                        var typeElem = doc.GetElement(typeId);
                        if (typeElem != null)
                        {
                            d["type_name"] = typeElem.Name ?? "";
                            d["family_name"] = (typeElem as ElementType)?.FamilyName ?? "";
                        }
                    }

                    // Location summary (point or curve start/end)
                    AddLocationSummary(elem, d);

                    if (includeParams) AddHotParameters(elem, d);

                    elements.Add(d);
                    returned++;
                }

                return Task.FromResult(CommandResult.Ok(new Dictionary<string, object>
                {
                    ["count"] = totalSelected,
                    ["returned"] = returned,
                    ["truncated"] = returned < totalSelected,
                    ["limit"] = limit,
                    ["by_category"] = byCategory,
                    ["by_level"] = byLevel,
                    ["elements"] = elements,
                }));
            }
            catch (OperationCanceledException)
            {
                return Task.FromResult(CommandResult.Fail(
                    "get_selected_elements cancelled (timeout).",
                    "Reduce the selection size or set limit lower."));
            }
            catch (Exception ex)
            {
                return Task.FromResult(CommandResult.Fail(
                    $"get_selected_elements failed: {ex.Message}",
                    "Make sure Revit has elements selected (highlighted) in the UI."));
            }
        }

        private static void AddLocationSummary(Element elem, Dictionary<string, object> d)
        {
            try
            {
                var loc = elem.Location;
                if (loc is LocationPoint lp)
                {
                    var p = lp.Point;
                    d["location"] = new Dictionary<string, object>
                    {
                        ["type"] = "point",
                        ["point"] = new[] { Math.Round(p.X, 4), Math.Round(p.Y, 4), Math.Round(p.Z, 4) },
                    };
                }
                else if (loc is LocationCurve lc)
                {
                    var c = lc.Curve;
                    var s = c.GetEndPoint(0);
                    var e = c.GetEndPoint(1);
                    d["location"] = new Dictionary<string, object>
                    {
                        ["type"] = "curve",
                        ["start"] = new[] { Math.Round(s.X, 4), Math.Round(s.Y, 4), Math.Round(s.Z, 4) },
                        ["end"] = new[] { Math.Round(e.X, 4), Math.Round(e.Y, 4), Math.Round(e.Z, 4) },
                        ["length"] = Math.Round(c.Length, 4),
                    };
                }
            }
            catch { /* element has no location */ }
        }

        // A small set of frequently-needed parameters. Skips read failures
        // silently so a bad parameter doesn't break the whole response.
        private static readonly BuiltInParameter[] HotParams = new[]
        {
            BuiltInParameter.HOST_AREA_COMPUTED,
            BuiltInParameter.HOST_VOLUME_COMPUTED,
            BuiltInParameter.WALL_USER_HEIGHT_PARAM,
            BuiltInParameter.WALL_BASE_OFFSET,
            BuiltInParameter.STRUCTURAL_BEAM_END0_ELEVATION,
            BuiltInParameter.STRUCTURAL_BEAM_END1_ELEVATION,
            BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS,
            BuiltInParameter.ALL_MODEL_MARK,
        };

        private static void AddHotParameters(Element elem, Dictionary<string, object> d)
        {
            var paramDict = new Dictionary<string, object>();
            foreach (var bip in HotParams)
            {
                try
                {
                    var p = elem.get_Parameter(bip);
                    if (p == null || !p.HasValue) continue;
                    object v = p.StorageType switch
                    {
                        StorageType.Double => Math.Round(p.AsDouble(), 6),
                        StorageType.Integer => p.AsInteger(),
                        StorageType.String => p.AsString() ?? "",
                        StorageType.ElementId => p.AsElementId().IntegerValue,
                        _ => null,
                    };
                    if (v != null) paramDict[bip.ToString()] = v;
                }
                catch { /* ignore */ }
            }
            if (paramDict.Count > 0) d["parameters"] = paramDict;
        }

        private static void Increment(Dictionary<string, int> dict, string key)
        {
            if (string.IsNullOrEmpty(key)) key = "(none)";
            dict.TryGetValue(key, out var v);
            dict[key] = v + 1;
        }

        private static long GetLong(Dictionary<string, object> p, string key, long def)
        {
            if (!p.TryGetValue(key, out var v) || v == null) return def;
            return v switch
            {
                long l => l,
                int i => i,
                double dbl => (long)dbl,
                string s when long.TryParse(s, out var sl) => sl,
                _ => def,
            };
        }

        private static bool GetBool(Dictionary<string, object> p, string key, bool defaultValue)
        {
            if (!p.TryGetValue(key, out var v) || v == null) return defaultValue;
            return v switch
            {
                bool b => b,
                string s => s.Equals("true", StringComparison.OrdinalIgnoreCase),
                _ => defaultValue,
            };
        }
    }
}
