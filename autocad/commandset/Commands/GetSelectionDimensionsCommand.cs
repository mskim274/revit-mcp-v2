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
    /// Returns Dimension entities (RotatedDimension / AlignedDimension /
    /// Radial / Diametric / Arc / Angular / Ordinate) from the user's
    /// PICKFIRST selection — including the actual numeric Measurement value
    /// and text override. Useful when a schedule's section sizes (B×D) are
    /// drawn as dimension entities rather than text labels.
    ///
    /// Each row contains:
    ///   handle, type, measurement (numeric), dim_text (override or "" if
    ///   the measurement is shown verbatim), text_position [x,y,z],
    ///   layer, plus type-specific extension-line points and orientation
    ///   ("horizontal" / "vertical" / "angled"). For RotatedDimension /
    ///   AlignedDimension we report XLine1Point and XLine2Point so the
    ///   caller can spatially associate dimensions with the geometry they
    ///   measure.
    /// </summary>
    public class GetSelectionDimensionsCommand : ICadCommand
    {
        public string Name => "get_selection_dimensions";
        public string Category => "Query";

        public Task<CommandResult> ExecuteAsync(
            Database db,
            Transaction tr,
            Dictionary<string, object> parameters,
            CancellationToken cancellationToken)
        {
            try
            {
                var ids = SelectionContext.Current;
                if (ids == null || ids.Length == 0)
                {
                    return Task.FromResult(CommandResult.Ok(new Dictionary<string, object>
                    {
                        ["count"] = 0,
                        ["total_selected"] = 0,
                        ["skipped_non_dim"] = 0,
                        ["dimensions"] = new List<object>(),
                        ["note"] = "No PICKFIRST selection. Select dimensions in AutoCAD first, then re-run.",
                    }));
                }

                int totalSelected = ids.Length;
                int skipped = 0;
                var dims = new List<Dictionary<string, object>>();
                var byType = new Dictionary<string, int>();
                var byLayer = new Dictionary<string, int>();

                foreach (var oid in ids)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var ent = tr.GetObject(oid, OpenMode.ForRead) as Entity;
                    if (!(ent is Dimension dim)) { skipped++; continue; }

                    var typeName = dim.GetType().Name;
                    Increment(byType, typeName);
                    Increment(byLayer, dim.Layer ?? "(none)");

                    var d = new Dictionary<string, object>
                    {
                        ["handle"] = dim.Handle.Value.ToString("X"),
                        ["type"] = typeName,
                        ["measurement"] = Math.Round(dim.Measurement, 4),
                        ["dim_text"] = dim.DimensionText ?? "",
                        ["text_position"] = PointArr(dim.TextPosition),
                        ["layer"] = dim.Layer,
                    };

                    AddTypeSpecific(dim, d);

                    dims.Add(d);
                }

                return Task.FromResult(CommandResult.Ok(new Dictionary<string, object>
                {
                    ["count"] = dims.Count,
                    ["total_selected"] = totalSelected,
                    ["skipped_non_dim"] = skipped,
                    ["by_type"] = byType,
                    ["by_layer"] = byLayer,
                    ["dimensions"] = dims,
                }));
            }
            catch (System.Exception ex)
            {
                return Task.FromResult(CommandResult.Fail(
                    $"get_selection_dimensions failed: {ex.Message}",
                    "Make sure the selection contains Dimension entities (DIM, AlignedDimension, RotatedDimension, etc.)."));
            }
        }

        private static void AddTypeSpecific(Dimension dim, Dictionary<string, object> d)
        {
            switch (dim)
            {
                case RotatedDimension rd:
                    d["xline1"] = PointArr(rd.XLine1Point);
                    d["xline2"] = PointArr(rd.XLine2Point);
                    d["dim_line_point"] = PointArr(rd.DimLinePoint);
                    d["rotation_rad"] = rd.Rotation;
                    d["orientation"] = ClassifyRotation(rd.Rotation);
                    // Span vector between extension lines (used by callers
                    // to associate the dimension with what it measures).
                    var dx = rd.XLine2Point.X - rd.XLine1Point.X;
                    var dy = rd.XLine2Point.Y - rd.XLine1Point.Y;
                    d["span_length"] = Math.Sqrt(dx * dx + dy * dy);
                    break;
                case AlignedDimension ad:
                    d["xline1"] = PointArr(ad.XLine1Point);
                    d["xline2"] = PointArr(ad.XLine2Point);
                    d["dim_line_point"] = PointArr(ad.DimLinePoint);
                    var adx = ad.XLine2Point.X - ad.XLine1Point.X;
                    var ady = ad.XLine2Point.Y - ad.XLine1Point.Y;
                    d["span_length"] = Math.Sqrt(adx * adx + ady * ady);
                    d["orientation"] = ClassifySpan(adx, ady);
                    break;
                case RadialDimension radial:
                    d["center"] = PointArr(radial.Center);
                    d["chord_point"] = PointArr(radial.ChordPoint);
                    d["leader_length"] = radial.LeaderLength;
                    break;
                case DiametricDimension diam:
                    d["far_chord"] = PointArr(diam.FarChordPoint);
                    d["chord_point"] = PointArr(diam.ChordPoint);
                    d["leader_length"] = diam.LeaderLength;
                    break;
                case ArcDimension arc:
                    d["xline1"] = PointArr(arc.XLine1Point);
                    d["xline2"] = PointArr(arc.XLine2Point);
                    d["arc_point"] = PointArr(arc.ArcPoint);
                    break;
                case Point3AngularDimension p3a:
                    d["xline1"] = PointArr(p3a.XLine1Point);
                    d["xline2"] = PointArr(p3a.XLine2Point);
                    d["center"] = PointArr(p3a.CenterPoint);
                    break;
                case OrdinateDimension od:
                    d["definition_point"] = PointArr(od.DefiningPoint);
                    d["leader_endpoint"] = PointArr(od.LeaderEndPoint);
                    d["uses_x_axis"] = od.UsingXAxis;
                    break;
            }
        }

        private static string ClassifyRotation(double rotationRad)
        {
            // RotatedDimension.Rotation: 0 = horizontal dim line, π/2 = vertical dim line.
            // Within ±0.1 rad (~5.7°) we round to canonical.
            var r = NormalizeAngle(rotationRad);
            if (Math.Abs(r) < 0.1 || Math.Abs(r - Math.PI) < 0.1) return "horizontal";
            if (Math.Abs(r - Math.PI / 2) < 0.1 || Math.Abs(r + Math.PI / 2) < 0.1) return "vertical";
            return "angled";
        }

        private static string ClassifySpan(double dx, double dy)
        {
            var ax = Math.Abs(dx);
            var ay = Math.Abs(dy);
            if (ax < 1e-6 && ay < 1e-6) return "point";
            if (ay < ax * 0.1) return "horizontal";
            if (ax < ay * 0.1) return "vertical";
            return "angled";
        }

        private static double NormalizeAngle(double a)
        {
            while (a > Math.PI) a -= 2 * Math.PI;
            while (a < -Math.PI) a += 2 * Math.PI;
            return a;
        }

        private static double[] PointArr(Point3d p)
            => new[] { Math.Round(p.X, 4), Math.Round(p.Y, 4), Math.Round(p.Z, 4) };

        private static void Increment(Dictionary<string, int> dict, string key)
        {
            if (string.IsNullOrEmpty(key)) key = "(none)";
            dict.TryGetValue(key, out var v);
            dict[key] = v + 1;
        }
    }
}
