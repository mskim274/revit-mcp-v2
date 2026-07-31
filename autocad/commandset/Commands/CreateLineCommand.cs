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
    /// Add a Line entity to model space. The command reports only a
    /// provisional in-transaction check; the plugin dispatcher owns the
    /// transaction and replaces it with post-commit verification.
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
                if (start == null)
                    return Fail(
                        "'start' must contain exactly 2 or 3 finite numbers.");
                if (end == null)
                    return Fail(
                        "'end' must contain exactly 2 or 3 finite numbers.");
                if (start.Value.DistanceTo(end.Value) < 1e-9)
                    return Fail("Zero-length line — start and end are equal.");

                string layerName = null;
                if (parameters.TryGetValue("layer", out var layerValue))
                {
                    if (!(layerValue is string suppliedLayer) ||
                        string.IsNullOrWhiteSpace(suppliedLayer))
                    {
                        return Fail(
                            "'layer' must be a non-empty string when supplied.",
                            "Omit 'layer' to use the current layer, or pass " +
                            "an exact name from cad_get_layers.");
                    }

                    layerName = suppliedLayer.Trim();
                }

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

                // Provisional object check only. The dispatcher owns commit
                // and replaces this block after reopening the ObjectId.
                var startActual = line.StartPoint;
                var endActual = line.EndPoint;
                var preCommitGeometryMatch =
                    NearlyEqual(startActual, start.Value, 1e-6) &&
                    NearlyEqual(endActual, end.Value, 1e-6);
                var verification = new Dictionary<string, object>
                {
                    ["performed"] = false,
                    ["phase"] = "pre_commit",
                    ["provisional"] = true,
                    ["commit_verified"] = false,
                    ["pre_commit_geometry_match"] = preCommitGeometryMatch,
                    ["actual_start"] = new[] { startActual.X, startActual.Y, startActual.Z },
                    ["actual_end"] = new[] { endActual.X, endActual.Y, endActual.Z },
                    ["actual_length"] = line.Length,
                    ["issues"] = new[]
                    {
                        "Final verification is pending transaction commit."
                    },
                };

                return Task.FromResult(CommandResult.Ok(new Dictionary<string, object>
                {
                    ["entity_id"] = line.Handle.Value.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                    ["entity_type"] = "Line",
                    ["layer"] = line.Layer,
                    ["verification"] = verification,
                }));
            }
            catch (System.Exception ex)
            {
                return Fail($"create_line failed: {ex.Message}",
                    "Use exactly 2 or 3 finite numbers for each point.");
            }
        }

        private static Task<CommandResult> Fail(string msg, string suggestion = null)
            => Task.FromResult(CommandResult.Fail(msg, suggestion));

        private static Point3d? ParsePoint(Dictionary<string, object> p, string key)
        {
            if (!p.TryGetValue(key, out var v) || v == null) return null;
            if (v is not List<object> list ||
                list.Count < 2 ||
                list.Count > 3)
            {
                return null;
            }

            try
            {
                double x = ToFiniteDouble(list[0]);
                double y = ToFiniteDouble(list[1]);
                double z = list.Count == 3
                    ? ToFiniteDouble(list[2])
                    : 0.0;
                return new Point3d(x, y, z);
            }
            catch { return null; }
        }

        private static double ToFiniteDouble(object value)
        {
            var number = value switch
            {
                double doubleValue => doubleValue,
                float floatValue => floatValue,
                long longValue => longValue,
                int intValue => intValue,
                decimal decimalValue => (double)decimalValue,
                _ => throw new InvalidCastException(
                    $"Cannot convert {value?.GetType().Name} to double"),
            };

            if (double.IsNaN(number) || double.IsInfinity(number))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    "Coordinate must be finite.");
            }

            return number;
        }

        private static bool NearlyEqual(Point3d a, Point3d b, double tol)
            => Math.Abs(a.X - b.X) <= tol && Math.Abs(a.Y - b.Y) <= tol && Math.Abs(a.Z - b.Z) <= tol;
    }
}
