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
    /// Returns geometry primitives (bounding box, faces, edges, solids
    /// summary) for one or more elements. Default mode is "summary":
    /// per-element bounding box + face_count + edge_count + solid_count.
    /// detail=true adds face metadata (area, normal, planar/cylindrical),
    /// edge endpoints, and per-solid volume/surface_area.
    ///
    /// Designed for LLM-driven spatial reasoning: "find walls intersecting
    /// this slab", "which beams overlap in plan", etc. Returns geometry
    /// in Revit internal feet (consistent with the rest of the API).
    ///
    /// Inputs:
    ///   - element_ids (int[], optional): if absent, uses
    ///     SelectionContext.Current (the user's UI selection).
    ///   - detail (bool, default false): include face/edge/solid detail.
    ///   - include_geometry_view (string, optional): "Model" | "Cut".
    ///       View detail mode for Element.get_Geometry. Default "Model"
    ///       (Coarse / no view-specific cut).
    ///   - max_faces / max_edges (int, default 50 / 100): per-element
    ///     caps to avoid response bloat on complex families.
    /// </summary>
    public class GetElementGeometryCommand : IRevitCommand
    {
        public string Name => "get_element_geometry";
        public string Category => "Query";

        public Task<CommandResult> ExecuteAsync(
            Document doc,
            Dictionary<string, object> parameters,
            CancellationToken cancellationToken)
        {
            try
            {
                var detail = GetBool(parameters, "detail", false);
                var maxFaces = (int)Math.Max(1, Math.Min(500, GetLong(parameters, "max_faces", 50)));
                var maxEdges = (int)Math.Max(1, Math.Min(2000, GetLong(parameters, "max_edges", 100)));
                var viewMode = GetString(parameters, "include_geometry_view") ?? "Model";

                // Resolve element IDs: explicit list OR PICKFIRST.
                var ids = ResolveIds(parameters);
                if (ids.Count == 0)
                {
                    return Task.FromResult(CommandResult.Fail(
                        "No element IDs provided and no UI selection.",
                        "Pass element_ids:[...] or select elements in Revit first."));
                }

                var options = new Options
                {
                    ComputeReferences = false,
                    IncludeNonVisibleObjects = false,
                    DetailLevel = ParseDetail(viewMode),
                };

                var perElement = new List<Dictionary<string, object>>();
                int withGeom = 0, withoutGeom = 0;

                foreach (var id in ids)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var elem = doc.GetElement(id);
                    if (elem == null)
                    {
                        perElement.Add(new Dictionary<string, object>
                        {
                            ["id"] = id.IntegerValue,
                            ["error"] = "element not found",
                        });
                        continue;
                    }

                    var entry = new Dictionary<string, object>
                    {
                        ["id"] = elem.Id.IntegerValue,
                        ["name"] = elem.Name ?? "",
                        ["category"] = elem.Category?.Name ?? "Unknown",
                    };

                    // Bounding box (cheap, even for non-geometric elements that have
                    // a location).
                    try
                    {
                        var bb = elem.get_BoundingBox(null);
                        if (bb != null)
                        {
                            entry["bounding_box"] = new Dictionary<string, object>
                            {
                                ["min"] = Pt(bb.Min),
                                ["max"] = Pt(bb.Max),
                                ["size"] = new[]
                                {
                                    Math.Round(bb.Max.X - bb.Min.X, 4),
                                    Math.Round(bb.Max.Y - bb.Min.Y, 4),
                                    Math.Round(bb.Max.Z - bb.Min.Z, 4),
                                },
                            };
                        }
                    }
                    catch { /* ignore */ }

                    // Geometry traversal — count + optional detail.
                    try
                    {
                        var geom = elem.get_Geometry(options);
                        if (geom == null)
                        {
                            withoutGeom++;
                            entry["geometry"] = "no_geometry";
                            perElement.Add(entry);
                            continue;
                        }

                        var solids = new List<Solid>();
                        FlattenSolids(geom, solids);

                        int faceCount = 0, edgeCount = 0;
                        double totalVolume = 0, totalSurfaceArea = 0;
                        var facesOut = new List<Dictionary<string, object>>();
                        var edgesOut = new List<Dictionary<string, object>>();

                        foreach (var solid in solids)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (solid == null || solid.Volume <= 1e-9) continue;
                            totalVolume += solid.Volume;
                            totalSurfaceArea += solid.SurfaceArea;
                            foreach (Face f in solid.Faces)
                            {
                                faceCount++;
                                if (detail && facesOut.Count < maxFaces) facesOut.Add(SummarizeFace(f));
                            }
                            foreach (Edge e in solid.Edges)
                            {
                                edgeCount++;
                                if (detail && edgesOut.Count < maxEdges) edgesOut.Add(SummarizeEdge(e));
                            }
                        }

                        var geomDict = new Dictionary<string, object>
                        {
                            ["solid_count"] = solids.Count,
                            ["face_count"] = faceCount,
                            ["edge_count"] = edgeCount,
                            ["total_volume"] = Math.Round(totalVolume, 6),
                            ["total_surface_area"] = Math.Round(totalSurfaceArea, 6),
                        };
                        if (detail)
                        {
                            geomDict["faces"] = facesOut;
                            geomDict["edges"] = edgesOut;
                            geomDict["faces_truncated"] = faceCount > facesOut.Count;
                            geomDict["edges_truncated"] = edgeCount > edgesOut.Count;
                        }
                        entry["geometry"] = geomDict;
                        withGeom++;
                    }
                    catch (Exception ex)
                    {
                        entry["geometry_error"] = ex.Message;
                    }

                    perElement.Add(entry);
                }

                return Task.FromResult(CommandResult.Ok(new Dictionary<string, object>
                {
                    ["count"] = perElement.Count,
                    ["with_geometry"] = withGeom,
                    ["without_geometry"] = withoutGeom,
                    ["detail_mode"] = detail,
                    ["units_note"] = "All distances/areas/volumes in Revit internal feet (1 ft = 304.8 mm).",
                    ["elements"] = perElement,
                }));
            }
            catch (OperationCanceledException)
            {
                return Task.FromResult(CommandResult.Fail(
                    "get_element_geometry cancelled (timeout).",
                    "Reduce element count or set detail=false."));
            }
            catch (Exception ex)
            {
                return Task.FromResult(CommandResult.Fail(
                    $"get_element_geometry failed: {ex.Message}",
                    "Verify element_ids exist in the document, or that the UI selection is non-empty."));
            }
        }

        // ─────────────────────────────────────────────────────────────────

        private static List<ElementId> ResolveIds(Dictionary<string, object> parameters)
        {
            var result = new List<ElementId>();
            if (parameters.TryGetValue("element_ids", out var raw) && raw is List<object> list)
            {
                foreach (var v in list)
                {
                    if (v == null) continue;
                    int n;
                    switch (v)
                    {
                        case int i: n = i; break;
                        case long l: n = (int)l; break;
                        case double d: n = (int)d; break;
                        case string s when int.TryParse(s, out var parsed): n = parsed; break;
                        default: continue;
                    }
                    result.Add(new ElementId(n));
                }
            }
            if (result.Count == 0)
            {
                foreach (var id in SelectionContext.Current ?? Array.Empty<ElementId>())
                    result.Add(id);
            }
            return result;
        }

        private static void FlattenSolids(GeometryElement geom, List<Solid> output)
        {
            foreach (GeometryObject obj in geom)
            {
                if (obj is Solid s) { output.Add(s); }
                else if (obj is GeometryInstance gi)
                {
                    var inst = gi.GetInstanceGeometry();
                    if (inst != null) FlattenSolids(inst, output);
                }
            }
        }

        private static Dictionary<string, object> SummarizeFace(Face f)
        {
            var d = new Dictionary<string, object>
            {
                ["type"] = f.GetType().Name,
                ["area"] = Math.Round(f.Area, 6),
            };
            try
            {
                if (f is PlanarFace pf)
                {
                    d["normal"] = Vec(pf.FaceNormal);
                    d["origin"] = Pt(pf.Origin);
                }
                else if (f is CylindricalFace cf)
                {
                    d["axis"] = Vec(cf.Axis);
                    d["origin"] = Pt(cf.Origin);
                    var radii = cf.get_Radius(0);
                    d["radius"] = Math.Round(radii.GetLength(), 6);
                }
            }
            catch { /* ignore — face introspection can throw on degenerate */ }
            return d;
        }

        private static Dictionary<string, object> SummarizeEdge(Edge e)
        {
            var d = new Dictionary<string, object>();
            try
            {
                var c = e.AsCurve();
                d["start"] = Pt(c.GetEndPoint(0));
                d["end"] = Pt(c.GetEndPoint(1));
                d["length"] = Math.Round(c.Length, 6);
                d["curve_type"] = c.GetType().Name;
            }
            catch { /* ignore */ }
            return d;
        }

        private static double[] Pt(XYZ p)
            => new[] { Math.Round(p.X, 4), Math.Round(p.Y, 4), Math.Round(p.Z, 4) };

        private static double[] Vec(XYZ v)
            => new[] { Math.Round(v.X, 6), Math.Round(v.Y, 6), Math.Round(v.Z, 6) };

        private static ViewDetailLevel ParseDetail(string s)
        {
            if (string.Equals(s, "Fine", StringComparison.OrdinalIgnoreCase)) return ViewDetailLevel.Fine;
            if (string.Equals(s, "Medium", StringComparison.OrdinalIgnoreCase)) return ViewDetailLevel.Medium;
            return ViewDetailLevel.Coarse;
        }

        private static string GetString(Dictionary<string, object> p, string key)
            => p.TryGetValue(key, out var v) && v is string s ? s : null;

        private static long GetLong(Dictionary<string, object> p, string key, long def)
        {
            if (!p.TryGetValue(key, out var v) || v == null) return def;
            return v switch
            {
                long l => l,
                int i => i,
                double d => (long)d,
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
