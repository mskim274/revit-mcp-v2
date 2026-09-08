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
    /// Search current/model/paper-space entities by type and/or layer.
    /// Supports summary counts and paginated detail (limit + offset).
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
                if (!TryGetSpace(parameters, out var requestedSpace, out var spaceError))
                {
                    return Task.FromResult(CommandResult.Fail(
                        spaceError,
                        "Use space='current', space='model', or space='paper'."));
                }
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

                var spaces = ResolveSpaces(
                    db,
                    tr,
                    requestedSpace,
                    cancellationToken);

                var byType = new Dictionary<string, int>();
                var byLayer = new Dictionary<string, int>();
                var bySpace = new Dictionary<string, int>();
                var matched = new List<EntityMatch>();
                int total = 0;

                foreach (var searchSpace in spaces)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    foreach (ObjectId id in searchSpace.Record)
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
                        Increment(bySpace, searchSpace.LayoutName);

                        if (!summaryOnly)
                        {
                            matched.Add(new EntityMatch
                            {
                                Id = id,
                                Space = searchSpace,
                            });
                        }
                    }
                }

                if (summaryOnly)
                {
                    return Task.FromResult(CommandResult.Ok(new Dictionary<string, object>
                    {
                        ["mode"] = "summary",
                        ["total"] = total,
                        ["by_type"] = byType,
                        ["by_layer"] = byLayer,
                        ["by_space"] = bySpace,
                        ["spaces_scanned"] = SpaceDescriptions(spaces),
                        ["filters_applied"] = new Dictionary<string, object>
                        {
                            ["entity_type"] = entityType ?? "",
                            ["layer"] = layer ?? "",
                            ["space"] = requestedSpace,
                        },
                    }));
                }

                // Paginated detail.
                var page = offset >= matched.Count
                    ? Enumerable.Empty<EntityMatch>()
                    : matched.Skip((int)offset).Take(limit);
                var items = new List<Dictionary<string, object>>();
                foreach (var match in page)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var ent = (Entity)tr.GetObject(
                        match.Id,
                        OpenMode.ForRead);
                    items.Add(EntityToDict(ent, match.Id, match.Space));
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
                    ["spaces_scanned"] = SpaceDescriptions(spaces),
                    ["filters_applied"] = new Dictionary<string, object>
                    {
                        ["entity_type"] = entityType ?? "",
                        ["layer"] = layer ?? "",
                        ["space"] = requestedSpace,
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

        private static Dictionary<string, object> EntityToDict(
            Entity ent,
            ObjectId id,
            SearchSpace searchSpace)
        {
            // Keep the existing decimal id wire format and expose the same
            // value explicitly as handle for cad_modify_entities.
            var handle = id.Handle.Value.ToString();
            var d = new Dictionary<string, object>
            {
                ["id"] = handle,
                ["handle"] = handle,
                ["type"] = ent.GetType().Name,
                ["layer"] = ent.Layer,
                ["space"] = searchSpace.Kind,
                ["layout_name"] = searchSpace.LayoutName,
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

        private static bool TryGetSpace(
            Dictionary<string, object> parameters,
            out string space,
            out string error)
        {
            space = "current";
            error = null;
            if (!parameters.TryGetValue("space", out var raw) || raw == null)
                return true;
            if (!(raw is string supplied) || string.IsNullOrWhiteSpace(supplied))
            {
                error = "space must be a non-empty string when supplied.";
                return false;
            }
            space = supplied.Trim().ToLowerInvariant();
            if (space == "current" || space == "model" || space == "paper")
                return true;
            error = $"Unsupported space '{supplied}'.";
            return false;
        }

        private static List<SearchSpace> ResolveSpaces(
            Database db,
            Transaction tr,
            string requestedSpace,
            CancellationToken cancellationToken)
        {
            if (requestedSpace == "current")
            {
                var record = tr.GetObject(
                    db.CurrentSpaceId,
                    OpenMode.ForRead) as BlockTableRecord;
                if (record == null || !record.IsLayout)
                    throw new InvalidOperationException(
                        "db.CurrentSpaceId is not a model/paper layout record.");
                return new List<SearchSpace>
                {
                    DescribeLayoutSpace(record, tr),
                };
            }

            if (requestedSpace == "model")
            {
                var blockTable = (BlockTable)tr.GetObject(
                    db.BlockTableId,
                    OpenMode.ForRead);
                var model = (BlockTableRecord)tr.GetObject(
                    blockTable[BlockTableRecord.ModelSpace],
                    OpenMode.ForRead);
                return new List<SearchSpace>
                {
                    DescribeLayoutSpace(model, tr),
                };
            }

            var result = new List<SearchSpace>();
            var layouts = (DBDictionary)tr.GetObject(
                db.LayoutDictionaryId,
                OpenMode.ForRead);
            foreach (DBDictionaryEntry entry in layouts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var layout = tr.GetObject(
                    entry.Value,
                    OpenMode.ForRead) as Layout;
                if (layout == null || layout.ModelType)
                    continue;
                var record = tr.GetObject(
                    layout.BlockTableRecordId,
                    OpenMode.ForRead) as BlockTableRecord;
                if (record == null)
                    continue;
                result.Add(new SearchSpace
                {
                    Record = record,
                    Kind = "paper",
                    LayoutName = layout.LayoutName,
                });
            }
            return result
                .OrderBy(item => item.LayoutName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static SearchSpace DescribeLayoutSpace(
            BlockTableRecord record,
            Transaction tr)
        {
            if (!record.IsLayout || record.LayoutId.IsNull)
                throw new InvalidOperationException(
                    $"Block table record '{record.Name}' has no layout.");
            var layout = (Layout)tr.GetObject(
                record.LayoutId,
                OpenMode.ForRead);
            return new SearchSpace
            {
                Record = record,
                Kind = layout.ModelType ? "model" : "paper",
                LayoutName = layout.LayoutName,
            };
        }

        private static List<Dictionary<string, object>> SpaceDescriptions(
            IEnumerable<SearchSpace> spaces)
            => spaces.Select(item => new Dictionary<string, object>
            {
                ["space"] = item.Kind,
                ["layout_name"] = item.LayoutName,
            }).ToList();

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

        private sealed class SearchSpace
        {
            public BlockTableRecord Record;
            public string Kind;
            public string LayoutName;
        }

        private sealed class EntityMatch
        {
            public ObjectId Id;
            public SearchSpace Space;
        }
    }
}
