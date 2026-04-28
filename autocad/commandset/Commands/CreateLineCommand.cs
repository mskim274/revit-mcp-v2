using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AutoCADMCP.CommandSet.Interfaces;

namespace AutoCADMCP.CommandSet.Commands
{
    /// <summary>
    /// Add a Line entity to model space. First mutation command — proves out
    /// the write-transaction path, post-tx verification, and the Tier 1
    /// idempotency cache.
    ///
    /// Parameters:
    ///   start  — required, [x, y, z] (z optional, defaults to 0)
    ///   end    — required, [x, y, z]
    ///   layer  — optional layer name. If specified, must already exist
    ///             (this command does not create layers — use a future
    ///             create_layer for that).
    /// </summary>
    public class CreateLineCommand : ICadCommand
    {
        public string Name => "create_line";
        public string Category => "Create";

        public Task<CommandResult> ExecuteAsync(
            Database db,
            Transaction tr,
            Dictionary<string, object> parameters,
            CancellationToken cancellationToken)
        {
            try
            {
                var start = ParsePoint(parameters, "start");
                var end = ParsePoint(parameters, "end");
                if (start == null) return Fail("'start' is required as [x, y, z].");
                if (end == null) return Fail("'end' is required as [x, y, z].");
                if (start.Value.DistanceTo(end.Value) < 1e-9)
                    return Fail("Zero-length line — start and end are equal.");

                string layerName = null;
                if (parameters.TryGetValue("layer", out var lv) && lv is string ls && !string.IsNullOrEmpty(ls))
                    layerName = ls;

                // Validate layer exists if specified.
                var layerTable = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                if (layerName != null && !layerTable.Has(layerName))
                {
                    return Fail($"Layer '{layerName}' does not exist.",
                        "Use cad_get_layers to see available layers, or omit the 'layer' param to use the current layer.");
                }

                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                var line = new Line(start.Value, end.Value);
                if (layerName != null) line.Layer = layerName;

                ms.AppendEntity(line);
                tr.AddNewlyCreatedDBObject(line, true);

                // Post-transaction verification (Tier 1 harness pattern). The
                // outer dispatcher commits when this method returns. We can
                // already query line's properties here — they're set on the
                // managed object, regardless of commit timing.
                var startActual = line.StartPoint;
                var endActual = line.EndPoint;
                var verification = new Dictionary<string, object>
                {
                    ["performed"] = true,
                    ["start_match"] = NearlyEqual(startActual, start.Value, 1e-6),
                    ["end_match"] = NearlyEqual(endActual, end.Value, 1e-6),
                    ["actual_start"] = new[] { startActual.X, startActual.Y, startActual.Z },
                    ["actual_end"] = new[] { endActual.X, endActual.Y, endActual.Z },
                    ["actual_length"] = line.Length,
                };

                return Task.FromResult(CommandResult.Ok(new Dictionary<string, object>
                {
                    ["entity_id"] = line.Handle.Value.ToString(),
                    ["entity_type"] = "Line",
                    ["layer"] = line.Layer,
                    ["verification"] = verification,
                }));
            }
            catch (System.Exception ex)
            {
                return Fail($"create_line failed: {ex.Message}",
                    "Check that 'start' and 'end' are valid [x,y,z] arrays of numbers.");
            }
        }

        private static Task<CommandResult> Fail(string msg, string suggestion = null)
            => Task.FromResult(CommandResult.Fail(msg, suggestion));

        private static Point3d? ParsePoint(Dictionary<string, object> p, string key)
        {
            if (!p.TryGetValue(key, out var v) || v == null) return null;
            if (v is not List<object> list || list.Count < 2) return null;
            try
            {
                double x = ToDouble(list[0]);
                double y = ToDouble(list[1]);
                double z = list.Count >= 3 ? ToDouble(list[2]) : 0.0;
                return new Point3d(x, y, z);
            }
            catch { return null; }
        }

        private static double ToDouble(object o) => o switch
        {
            double d => d,
            long l => l,
            int i => i,
            string s => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture),
            _ => throw new InvalidCastException($"Cannot convert {o?.GetType().Name} to double"),
        };

        private static bool NearlyEqual(Point3d a, Point3d b, double tol)
            => Math.Abs(a.X - b.X) <= tol && Math.Abs(a.Y - b.Y) <= tol && Math.Abs(a.Z - b.Z) <= tol;
    }
}
