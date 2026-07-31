using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using AutoCADMCP.CommandSet.Interfaces;

namespace AutoCADMCP.CommandSet.Commands
{
    /// <summary>
    /// Search model-space entities by type and/or layer. Supports a summary
    /// mode (counts only) and a paginated detail mode (limit + offset).
    /// Mirrors Revit MCP's query_elements.
    ///
    /// Parameters:
    ///   entity_type  — DXF class name to filter (e.g. "Line", "Circle",
    ///                   "BlockReference", "MText"). Optional.
    ///   layer        — exact layer name. Optional.
    ///   summary_only — bool, default true. Returns counts grouped by type
    ///                   and by layer, no per-entity rows.
    ///   limit        — page size, default 50, max 200.
    ///   offset       — pagination cursor (raw integer).
    /// </summary>
    public class QueryEntitiesCommand : ICadCommand
    {
        public string Name => "query_entities";
        public string Category => "Query";

        public Task<CommandResult> ExecuteAsync(
            Database db,
            Transaction tr,
            Dictionary<string, object> parameters,
            CancellationToken cancellationToken)
        {
            try
            {
                var entityType = GetString(parameters, "entity_type");
                var layer = GetString(parameters, "layer");
                var summaryOnly = GetBool(parameters, "summary_only", defaultValue: true);
                if (!TryGetBoundedInt(
                        parameters,
                        "limit",
                        defaultValue: 50,
                        minValue: 1,
                        maxValue: 200,
                        out var limit,
                        out var limitError))
                {
                    return Task.FromResult(CommandResult.Fail(
                        limitError,
                        "Pass limit as an integer from 1 through 200."));
                }
                if (!TryGetNonNegativeLong(
                        parameters,
                        "offset",
                        defaultValue: 0,
                        out var offset,
                        out var offsetError))
                {
                    return Task.FromResult(CommandResult.Fail(
                        offsetError,
                        "Pass offset as a non-negative integer."));
                }

                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                var byType = new Dictionary<string, int>();
                var byLayer = new Dictionary<string, int>();
                var matched = new List<ObjectId>();
                int total = 0;

                foreach (ObjectId id in ms)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null) continue;

                    var typeName = ent.GetType().Name;
                    if (!string.IsNullOrEmpty(entityType) &&
                        !string.Equals(typeName, entityType, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!string.IsNullOrEmpty(layer) &&
                        !string.Equals(ent.Layer, layer, StringComparison.OrdinalIgnoreCase))
                        continue;

                    total++;
                    Increment(byType, typeName);
                    Increment(byLayer, ent.Layer);

                    if (!summaryOnly) matched.Add(id);
                }

                if (summaryOnly)
                {
                    return Task.FromResult(CommandResult.Ok(new Dictionary<string, object>
                    {
                        ["mode"] = "summary",
                        ["total"] = total,
                        ["by_type"] = byType,
                        ["by_layer"] = byLayer,
                        ["filters_applied"] = new Dictionary<string, object>
                        {
                            ["entity_type"] = entityType ?? "",
                            ["layer"] = layer ?? "",
                        },
                    }));
                }

                // Paginated detail.
                var page = offset >= matched.Count
                    ? Enumerable.Empty<ObjectId>()
                    : matched.Skip((int)offset).Take(limit);
                var items = new List<Dictionary<string, object>>();
                foreach (var id in page)
                {
                    var ent = (Entity)tr.GetObject(id, OpenMode.ForRead);
                    items.Add(EntityToDict(ent, id));
                }

                return Task.FromResult(CommandResult.Ok(new Dictionary<string, object>
                {
                    ["mode"] = "paginated",
                    ["total_count"] = total,
                    ["returned_count"] = items.Count,
                    ["offset"] = offset,
                    ["has_more"] =
                        offset < total && items.Count < (total - offset),
                    ["items"] = items,
                    ["filters_applied"] = new Dictionary<string, object>
                    {
                        ["entity_type"] = entityType ?? "",
                        ["layer"] = layer ?? "",
                    },
                }));
            }
            catch (System.Exception ex)
            {
                return Task.FromResult(CommandResult.Fail(
                    $"query_entities failed: {ex.Message}",
                    "Try summary_only:true first, or narrow entity_type/layer."));
            }
        }

        private static Dictionary<string, object> EntityToDict(Entity ent, ObjectId id)
        {
            var d = new Dictionary<string, object>
            {
                ["id"] = id.Handle.Value.ToString(),
                ["type"] = ent.GetType().Name,
                ["layer"] = ent.Layer,
                ["color_index"] = ent.ColorIndex,
                ["linetype"] = ent.Linetype,
            };

            // Light type-specific extras — keep small to avoid response bloat.
            switch (ent)
            {
                case Line line:
                    d["start"] = new[] { line.StartPoint.X, line.StartPoint.Y, line.StartPoint.Z };
                    d["end"] = new[] { line.EndPoint.X, line.EndPoint.Y, line.EndPoint.Z };
                    d["length"] = line.Length;
                    break;
                case Circle circle:
                    d["center"] = new[] { circle.Center.X, circle.Center.Y, circle.Center.Z };
                    d["radius"] = circle.Radius;
                    break;
                case DBText text:
                    d["text"] = text.TextString;
                    d["position"] = new[] { text.Position.X, text.Position.Y, text.Position.Z };
                    break;
                case MText mtext:
                    d["text"] = mtext.Contents;
                    d["position"] = new[] { mtext.Location.X, mtext.Location.Y, mtext.Location.Z };
                    break;
                case BlockReference block:
                    d["block_name"] = block.Name;
                    d["position"] = new[] { block.Position.X, block.Position.Y, block.Position.Z };
                    d["rotation"] = block.Rotation;
                    break;
            }
            return d;
        }

        private static void Increment(Dictionary<string, int> dict, string key)
        {
            if (string.IsNullOrEmpty(key)) key = "(none)";
            dict.TryGetValue(key, out var v);
            dict[key] = v + 1;
        }

        private static string GetString(Dictionary<string, object> p, string key)
            => p.TryGetValue(key, out var v) && v is string s ? s : null;
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
            if (!TryGetIntegralValue(p, key, out var supplied, out var parsed))
            {
                error = $"{key} must be an integer from {minValue} through {maxValue}.";
                return false;
            }
            if (!supplied)
                return true;
            if (parsed < minValue || parsed > maxValue)
            {
                error =
                    $"{key} must be from {minValue} through {maxValue}; received {parsed}.";
                return false;
            }

            value = (int)parsed;
            return true;
        }

        private static bool TryGetNonNegativeLong(
            Dictionary<string, object> p,
            string key,
            long defaultValue,
            out long value,
            out string error)
        {
            value = defaultValue;
            error = null;
            if (!TryGetIntegralValue(p, key, out var supplied, out var parsed))
            {
                error = $"{key} must be a non-negative integer.";
                return false;
            }
            if (!supplied)
                return true;
            if (parsed < 0)
            {
                error = $"{key} must be non-negative; received {parsed}.";
                return false;
            }

            value = parsed;
            return true;
        }

        private static bool TryGetIntegralValue(
            Dictionary<string, object> p,
            string key,
            out bool supplied,
            out long value)
        {
            supplied = p.TryGetValue(key, out var raw);
            value = 0;
            if (!supplied)
                return true;

            switch (raw)
            {
                case int i:
                    value = i;
                    return true;
                case long l:
                    value = l;
                    return true;
                case double d when !double.IsNaN(d) &&
                                   !double.IsInfinity(d) &&
                                   d == Math.Truncate(d) &&
                                   d >= long.MinValue &&
                                   d <= long.MaxValue:
                    value = (long)d;
                    return true;
                default:
                    return false;
            }
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
