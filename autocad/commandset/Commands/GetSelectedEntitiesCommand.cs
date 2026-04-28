using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using AutoCADMCP.CommandSet.Interfaces;

namespace AutoCADMCP.CommandSet.Commands
{
    /// <summary>
    /// Returns the user's current PICKFIRST selection (entities selected in
    /// AutoCAD before invoking this command). Output mirrors v1's
    /// get_selected_elements with v2 conventions: snake_case keys, position
    /// arrays of [x,y,z], handles as hex strings.
    ///
    /// Scope:
    ///   - "selection" (default) — read Editor.SelectImplied()
    ///   - When the implied set is empty, returns count=0 with a suggestion.
    ///
    /// Per-entity payload kept conservative (no GeometricExtents by default —
    /// large for 1000s of entities). Set include_geometry=true to add bounds.
    /// </summary>
    public class GetSelectedEntitiesCommand : ICadCommand
    {
        public string Name => "get_selected_entities";
        public string Category => "Query";

        public Task<CommandResult> ExecuteAsync(
            Database db,
            Transaction tr,
            Dictionary<string, object> parameters,
            CancellationToken cancellationToken)
        {
            try
            {
                var includeGeometry = GetBool(parameters, "include_geometry", false);
                var limit = (int)Math.Max(1, Math.Min(1000, GetLong(parameters, "limit", 500)));

                // Use the dispatcher-captured PICKFIRST snapshot.
                // SelectionContext.Current is populated BEFORE
                // ExecuteInCommandContextAsync, where Editor.SelectImplied()
                // would otherwise return empty.
                var ssObjects = SelectionContext.Current;
                if (ssObjects == null || ssObjects.Length == 0)
                {
                    return Task.FromResult(CommandResult.Ok(new Dictionary<string, object>
                    {
                        ["count"] = 0,
                        ["entities"] = new List<object>(),
                        ["note"] = "No PICKFIRST selection. Select entities in AutoCAD first, then re-run.",
                    }));
                }
                var entities = new List<Dictionary<string, object>>();
                int totalSelected = ssObjects.Length;
                int returned = 0;

                // Build typeCount summary even when truncating below limit
                var byType = new Dictionary<string, int>();
                var byLayer = new Dictionary<string, int>();

                foreach (var oid in ssObjects)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var ent = tr.GetObject(oid, OpenMode.ForRead) as Entity;
                    if (ent == null) continue;

                    var typeName = ent.GetType().Name;
                    Increment(byType, typeName);
                    Increment(byLayer, ent.Layer ?? "(none)");

                    if (returned >= limit) continue;

                    var d = new Dictionary<string, object>
                    {
                        ["handle"] = ent.Handle.Value.ToString("X"),
                        ["object_id"] = oid.Handle.Value.ToString("X"),
                        ["type"] = typeName,
                        ["layer"] = ent.Layer,
                        ["color_index"] = ent.ColorIndex,
                        ["linetype"] = ent.Linetype,
                    };

                    AddTypeSpecificFields(ent, d);

                    if (includeGeometry)
                    {
                        TryAddBounds(ent, d);
                    }

                    entities.Add(d);
                    returned++;
                }

                return Task.FromResult(CommandResult.Ok(new Dictionary<string, object>
                {
                    ["count"] = totalSelected,
                    ["returned"] = returned,
                    ["truncated"] = returned < totalSelected,
                    ["limit"] = limit,
                    ["by_type"] = byType,
                    ["by_layer"] = byLayer,
                    ["entities"] = entities,
                }));
            }
            catch (System.Exception ex)
            {
                return Task.FromResult(CommandResult.Fail(
                    $"get_selected_entities failed: {ex.Message}",
                    "Make sure the drawing has a selection (PICKFIRST set). Try selecting entities then re-run."));
            }
        }

        private static void AddTypeSpecificFields(Entity ent, Dictionary<string, object> d)
        {
            switch (ent)
            {
                case DBText t:
                    d["text"] = t.TextString;
                    d["position"] = new[] { t.Position.X, t.Position.Y, t.Position.Z };
                    d["height"] = t.Height;
                    d["rotation"] = t.Rotation;
                    break;
                case MText m:
                    d["text"] = m.Text;             // unformatted plain text
                    d["contents"] = m.Contents;     // raw with format codes
                    d["position"] = new[] { m.Location.X, m.Location.Y, m.Location.Z };
                    d["height"] = m.TextHeight;
                    d["rotation"] = m.Rotation;
                    d["width"] = m.Width;
                    break;
                case Line l:
                    d["start"] = new[] { l.StartPoint.X, l.StartPoint.Y, l.StartPoint.Z };
                    d["end"] = new[] { l.EndPoint.X, l.EndPoint.Y, l.EndPoint.Z };
                    d["length"] = l.Length;
                    break;
                case Circle c:
                    d["center"] = new[] { c.Center.X, c.Center.Y, c.Center.Z };
                    d["radius"] = c.Radius;
                    break;
                case Arc a:
                    d["center"] = new[] { a.Center.X, a.Center.Y, a.Center.Z };
                    d["radius"] = a.Radius;
                    d["start_angle"] = a.StartAngle;
                    d["end_angle"] = a.EndAngle;
                    break;
                case Polyline p:
                    d["vertex_count"] = p.NumberOfVertices;
                    d["closed"] = p.Closed;
                    d["length"] = p.Length;
                    break;
                case BlockReference br:
                    d["block_name"] = br.Name;
                    d["position"] = new[] { br.Position.X, br.Position.Y, br.Position.Z };
                    d["rotation"] = br.Rotation;
                    d["scale"] = new[] { br.ScaleFactors.X, br.ScaleFactors.Y, br.ScaleFactors.Z };
                    break;
            }
        }

        private static void TryAddBounds(Entity ent, Dictionary<string, object> d)
        {
            try
            {
                var ext = ent.GeometricExtents;
                d["bounds"] = new Dictionary<string, object>
                {
                    ["min"] = new[] { Round(ext.MinPoint.X), Round(ext.MinPoint.Y), Round(ext.MinPoint.Z) },
                    ["max"] = new[] { Round(ext.MaxPoint.X), Round(ext.MaxPoint.Y), Round(ext.MaxPoint.Z) },
                };
            }
            catch { /* some entities have no extents (text without TTF cache, etc.) */ }
        }

        private static double Round(double v) => Math.Round(v, 4);

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
