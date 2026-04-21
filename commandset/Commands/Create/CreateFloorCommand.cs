using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using RevitMCP.CommandSet.Interfaces;

namespace RevitMCP.CommandSet.Commands.Create
{
    /// <summary>
    /// Create a floor from a rectangular boundary or a list of points.
    ///
    /// Parameters (rectangular mode):
    ///   min_x       (double, required) — Minimum X coordinate (feet)
    ///   min_y       (double, required) — Minimum Y coordinate (feet)
    ///   max_x       (double, required) — Maximum X coordinate (feet)
    ///   max_y       (double, required) — Maximum Y coordinate (feet)
    ///
    /// Parameters (polygon mode):
    ///   points      (array, required)  — Array of {x, y} coordinate objects (feet)
    ///
    /// Common parameters:
    ///   level_name  (string, optional) — Level name (default: lowest level)
    ///   floor_type  (string, optional) — Floor type name (default: first available)
    ///   structural  (bool, optional)   — Is structural floor (default: false)
    /// </summary>
    public class CreateFloorCommand : IRevitCommand
    {
        public string Name => "create_floor";
        public string Category => "Create";

        public Task<CommandResult> ExecuteAsync(
            Document doc,
            Dictionary<string, object> parameters,
            CancellationToken cancellationToken)
        {
            try
            {
                if (parameters == null)
                    return Task.FromResult(CommandResult.Fail(
                        "No parameters provided.",
                        "Provide either min_x/min_y/max_x/max_y for a rectangle, or points for a polygon."));

                // Resolve level
                var levelName = parameters.TryGetValue("level_name", out var lnObj) ? lnObj?.ToString() : null;
                Level level = ResolveLevel(doc, levelName);
                if (level == null)
                    return Task.FromResult(CommandResult.Fail(
                        $"Level '{levelName}' not found.",
                        "Use revit_get_levels to see available levels."));

                // Resolve floor type
                var floorTypeName = parameters.TryGetValue("floor_type", out var ftObj) ? ftObj?.ToString() : null;
                FloorType floorType = ResolveFloorType(doc, floorTypeName);
                if (floorType == null)
                    return Task.FromResult(CommandResult.Fail(
                        $"Floor type '{floorTypeName}' not found.",
                        "Use revit_get_types_by_category(category='Floors') to see available floor types."));

                var structural = parameters.TryGetValue("structural", out var sObj) && Convert.ToBoolean(sObj);

                // Build boundary curve loop
                CurveLoop boundary;
                string mode;
                bool autoFallback = false;

                if (parameters.ContainsKey("points"))
                {
                    // Polygon mode
                    boundary = BuildPolygonBoundary(parameters["points"]);
                    mode = "polygon";
                }
                else if (TryGetDouble(parameters, "min_x", out var minX) &&
                         TryGetDouble(parameters, "min_y", out var minY) &&
                         TryGetDouble(parameters, "max_x", out var maxX) &&
                         TryGetDouble(parameters, "max_y", out var maxY))
                {
                    // Rectangle mode
                    boundary = BuildRectBoundary(minX, minY, maxX, maxY);
                    mode = "rectangle";
                }
                else
                {
                    return Task.FromResult(CommandResult.Fail(
                        "Missing boundary definition.",
                        "Provide either min_x/min_y/max_x/max_y for a rectangle, or points array for a polygon."));
                }

                if (boundary == null)
                    return Task.FromResult(CommandResult.Fail(
                        "Invalid boundary definition — could not create valid curve loop.",
                        "Ensure points form a valid closed polygon with no self-intersections."));

                // Harness Engineering — Tier 1: Create with auto-fallback.
                // Known issue: rectangle mode occasionally fails with "Invalid boundary"
                // even when geometry is valid. Try rectangle first, fall back to polygon
                // representation (same 4 corners as explicit polygon) if rectangle fails.
                Floor floor = null;
                Exception firstError = null;

                try
                {
                    floor = CreateFloorInTransaction(doc, boundary, floorType, level, structural);
                }
                catch (Exception ex) when (mode == "rectangle")
                {
                    firstError = ex;
                    // Rebuild boundary as polygon (4 corner points)
                    var fallbackBoundary = BuildPolygonFromRectBoundary(boundary);
                    if (fallbackBoundary != null)
                    {
                        try
                        {
                            floor = CreateFloorInTransaction(doc, fallbackBoundary, floorType, level, structural);
                            boundary = fallbackBoundary;
                            autoFallback = true;
                            mode = "rectangle→polygon(auto)";
                        }
                        catch
                        {
                            throw firstError; // Report original error if fallback also fails
                        }
                    }
                    else
                    {
                        throw;
                    }
                }

                if (floor == null)
                    return Task.FromResult(CommandResult.Fail(
                        "Floor creation returned null after transaction.",
                        "Check boundary validity and floor type compatibility."));

                // Calculate area approximation
                var area = CalculateLoopArea(boundary);

                // Harness Engineering — Tier 1: Post-transaction verification.
                var verification = VerifyCreatedFloor(doc, floor, boundary, level, floorType, area);

                return Task.FromResult(CommandResult.Ok(new Dictionary<string, object>
                {
                    ["element_id"] = floor.Id.IntegerValue,
                    ["floor_type"] = floorType.Name,
                    ["level"] = level.Name,
                    ["mode"] = mode,
                    ["approximate_area_sqft"] = Math.Round(area, 2),
                    ["approximate_area_sqm"] = Math.Round(area * 0.0929, 2),
                    ["structural"] = structural,
                    ["auto_fallback_applied"] = autoFallback,
                    ["verification"] = verification
                }));
            }
            catch (OperationCanceledException)
            {
                return Task.FromResult(CommandResult.Fail(
                    "Operation cancelled.", "Try again."));
            }
            catch (Exception ex)
            {
                return Task.FromResult(CommandResult.Fail(
                    $"Failed to create floor: {ex.Message}",
                    "Check boundary coordinates and floor type. Ensure points form a valid closed loop."));
            }
        }

        private CurveLoop BuildRectBoundary(double minX, double minY, double maxX, double maxY)
        {
            if (Math.Abs(maxX - minX) < 0.01 || Math.Abs(maxY - minY) < 0.01)
                return null;

            var p0 = new XYZ(minX, minY, 0);
            var p1 = new XYZ(maxX, minY, 0);
            var p2 = new XYZ(maxX, maxY, 0);
            var p3 = new XYZ(minX, maxY, 0);

            var loop = new CurveLoop();
            loop.Append(Line.CreateBound(p0, p1));
            loop.Append(Line.CreateBound(p1, p2));
            loop.Append(Line.CreateBound(p2, p3));
            loop.Append(Line.CreateBound(p3, p0));
            return loop;
        }

        private CurveLoop BuildPolygonBoundary(object pointsObj)
        {
            var points = new List<XYZ>();

            if (pointsObj is IEnumerable<object> enumerable)
            {
                foreach (var item in enumerable)
                {
                    if (item is Dictionary<string, object> dict)
                    {
                        if (dict.TryGetValue("x", out var xObj) && dict.TryGetValue("y", out var yObj))
                        {
                            var x = Convert.ToDouble(xObj);
                            var y = Convert.ToDouble(yObj);
                            points.Add(new XYZ(x, y, 0));
                        }
                    }
                }
            }

            if (points.Count < 3) return null;

            var loop = new CurveLoop();
            for (int i = 0; i < points.Count; i++)
            {
                var next = (i + 1) % points.Count;
                if (points[i].DistanceTo(points[next]) < 0.001) continue;
                loop.Append(Line.CreateBound(points[i], points[next]));
            }

            return loop;
        }

        private double CalculateLoopArea(CurveLoop loop)
        {
            // Shoelace formula approximation
            double area = 0;
            var points = new List<XYZ>();
            foreach (var curve in loop)
            {
                points.Add(curve.GetEndPoint(0));
            }
            if (points.Count < 3) return 0;

            for (int i = 0; i < points.Count; i++)
            {
                int j = (i + 1) % points.Count;
                area += points[i].X * points[j].Y;
                area -= points[j].X * points[i].Y;
            }
            return Math.Abs(area) / 2.0;
        }

        private Level ResolveLevel(Document doc, string levelName)
        {
            var levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .ToList();

            if (levels.Count == 0) return null;

            if (string.IsNullOrEmpty(levelName))
                return levels.First();

            return levels.FirstOrDefault(l =>
                l.Name.Equals(levelName, StringComparison.OrdinalIgnoreCase));
        }

        private FloorType ResolveFloorType(Document doc, string typeName)
        {
            var floorTypes = new FilteredElementCollector(doc)
                .OfClass(typeof(FloorType))
                .Cast<FloorType>()
                .ToList();

            if (floorTypes.Count == 0) return null;

            if (string.IsNullOrEmpty(typeName))
                return floorTypes.First();

            return floorTypes.FirstOrDefault(ft =>
                ft.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase))
                ?? floorTypes.FirstOrDefault(ft =>
                    ft.Name.IndexOf(typeName, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private bool TryGetDouble(Dictionary<string, object> parameters, string key, out double value)
        {
            value = 0;
            if (parameters == null || !parameters.TryGetValue(key, out var obj) || obj == null)
                return false;
            try
            {
                value = Convert.ToDouble(obj);
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Encapsulates the floor creation transaction so it can be retried on fallback.
        /// </summary>
        private static Floor CreateFloorInTransaction(
            Document doc,
            CurveLoop boundary,
            FloorType floorType,
            Level level,
            bool structural)
        {
            Floor floor;
            using (var tx = new Transaction(doc, "MCP: Create Floor"))
            {
                tx.Start();

                var loops = new List<CurveLoop> { boundary };
                floor = Floor.Create(doc, loops, floorType.Id, level.Id);

                if (structural)
                {
                    var param = floor.get_Parameter(BuiltInParameter.FLOOR_PARAM_IS_STRUCTURAL);
                    if (param != null && !param.IsReadOnly)
                        param.Set(1);
                }

                tx.Commit();
            }
            return floor;
        }

        /// <summary>
        /// Build a polygon CurveLoop from a rectangle CurveLoop by extracting
        /// the 4 corner points. Used as fallback when rectangle mode fails.
        /// </summary>
        private static CurveLoop BuildPolygonFromRectBoundary(CurveLoop rectLoop)
        {
            var points = new List<XYZ>();
            foreach (var curve in rectLoop)
                points.Add(curve.GetEndPoint(0));

            if (points.Count < 3) return null;

            var loop = new CurveLoop();
            for (int i = 0; i < points.Count; i++)
            {
                var next = (i + 1) % points.Count;
                if (points[i].DistanceTo(points[next]) < 0.001) continue;
                loop.Append(Line.CreateBound(points[i], points[next]));
            }
            return loop;
        }

        /// <summary>
        /// Post-transaction verification for created floor.
        /// Confirms the floor exists, is on the expected level/type, and area is close to expected.
        /// </summary>
        private static Dictionary<string, object> VerifyCreatedFloor(
            Document doc,
            Floor floor,
            CurveLoop expectedBoundary,
            Level expectedLevel,
            FloorType expectedType,
            double expectedAreaSqft)
        {
            const double AREA_TOLERANCE_RATIO = 0.05; // 5% tolerance on computed area

            var issues = new List<string>();
            var result = new Dictionary<string, object>
            {
                ["performed"] = true,
                ["geometry_match"] = true
            };

            try
            {
                var refetched = doc.GetElement(floor.Id) as Floor;
                if (refetched == null)
                {
                    result["geometry_match"] = false;
                    result["issues"] = new List<string> { "Floor not found after commit — creation may have failed." };
                    return result;
                }

                // Check level
                if (refetched.LevelId != expectedLevel.Id)
                    issues.Add("Floor attached to a different level than requested.");

                // Check type
                if (refetched.FloorType?.Id != expectedType.Id)
                    issues.Add($"Floor type mismatch: expected '{expectedType.Name}'.");

                // Check area against the Revit-computed parameter
                var areaParam = refetched.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED);
                if (areaParam != null)
                {
                    var actualAreaSqft = areaParam.AsDouble();
                    result["actual_area_sqft"] = Math.Round(actualAreaSqft, 2);

                    if (expectedAreaSqft > 0.01)
                    {
                        var ratio = Math.Abs(actualAreaSqft - expectedAreaSqft) / expectedAreaSqft;
                        result["area_deviation_ratio"] = Math.Round(ratio, 4);
                        if (ratio > AREA_TOLERANCE_RATIO)
                            issues.Add($"Area {Math.Round(actualAreaSqft, 2)}sqft differs from expected {Math.Round(expectedAreaSqft, 2)}sqft by {Math.Round(ratio * 100, 1)}%.");
                    }
                }

                if (issues.Count > 0)
                {
                    result["geometry_match"] = false;
                    result["issues"] = issues;
                }
            }
            catch (Exception ex)
            {
                result["performed"] = false;
                result["verification_error"] = ex.Message;
            }

            return result;
        }
    }
}
