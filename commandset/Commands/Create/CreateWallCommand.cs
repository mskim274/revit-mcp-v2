using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using RevitMCP.CommandSet.Helpers;
using RevitMCP.CommandSet.Interfaces;

namespace RevitMCP.CommandSet.Commands.Create
{
    /// <summary>
    /// Create a straight wall between two points.
    ///
    /// Parameters:
    ///   start_x     (double, required) — Start point X (feet)
    ///   start_y     (double, required) — Start point Y (feet)
    ///   end_x       (double, required) — End point X (feet)
    ///   end_y       (double, required) — End point Y (feet)
    ///   level_name  (string, optional) — Level name (default: lowest level)
    ///   wall_type   (string, optional) — Wall type name (default: first available)
    ///   height      (double, optional) — Wall height in feet (default: level-to-level or 10ft)
    ///   structural  (bool, optional)   — Is structural wall (default: false)
    /// </summary>
    public class CreateWallCommand : IRevitCommand
    {
        public string Name => "create_wall";
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
                        "Provide start_x, start_y, end_x, end_y at minimum."));

                // Validate every raw value before resolving Revit elements or
                // opening a transaction. The TypeScript layer performs the
                // same finite-number/strict-boolean checks, but raw WebSocket
                // callers must not bypass them.
                if (!RawParameterValidation.TryGetRequiredFiniteDouble(
                        parameters,
                        "start_x",
                        out var startX,
                        out var validationError) ||
                    !RawParameterValidation.TryGetRequiredFiniteDouble(
                        parameters,
                        "start_y",
                        out var startY,
                        out validationError) ||
                    !RawParameterValidation.TryGetRequiredFiniteDouble(
                        parameters,
                        "end_x",
                        out var endX,
                        out validationError) ||
                    !RawParameterValidation.TryGetRequiredFiniteDouble(
                        parameters,
                        "end_y",
                        out var endY,
                        out validationError))
                {
                    return Task.FromResult(CommandResult.Fail(
                        validationError,
                        "Provide finite numeric start_x, start_y, end_x, and end_y values in feet."));
                }

                if (!RawParameterValidation.TryGetOptionalFiniteDouble(
                        parameters,
                        "height",
                        defaultValue: 10.0,
                        out var height,
                        out validationError))
                {
                    return Task.FromResult(CommandResult.Fail(
                        validationError,
                        "Pass height as a finite number greater than zero in feet, or omit it to use 10 feet."));
                }
                if (height <= 0)
                {
                    return Task.FromResult(CommandResult.Fail(
                        $"height must be greater than zero; received {height}.",
                        "Pass a positive wall height in feet."));
                }

                if (!RawParameterValidation.TryGetOptionalStrictBool(
                        parameters,
                        "structural",
                        defaultValue: false,
                        out var structural,
                        out validationError))
                {
                    return Task.FromResult(CommandResult.Fail(
                        validationError,
                        "Pass structural as true or false, or omit it to use false."));
                }

                var startPoint = new XYZ(startX, startY, 0);
                var endPoint = new XYZ(endX, endY, 0);

                if (startPoint.DistanceTo(endPoint) < 0.01)
                    return Task.FromResult(CommandResult.Fail(
                        "Start and end points are too close (< 0.01 ft).",
                        "Provide points that are at least 0.01 feet apart."));

                var line = Line.CreateBound(startPoint, endPoint);

                // Resolve level
                var levelName = parameters.TryGetValue("level_name", out var lnObj) ? lnObj?.ToString() : null;
                Level level = ResolveLevel(doc, levelName);
                if (level == null)
                    return Task.FromResult(CommandResult.Fail(
                        $"Level '{levelName}' not found.",
                        "Use revit_get_levels to see available levels."));

                // Resolve wall type
                var wallTypeName = parameters.TryGetValue("wall_type", out var wtObj) ? wtObj?.ToString() : null;
                WallType wallType = ResolveWallType(doc, wallTypeName);
                if (wallType == null)
                    return Task.FromResult(CommandResult.Fail(
                        $"Wall type '{wallTypeName}' not found.",
                        "Use revit_get_types_by_category(category='Walls') to see available wall types."));

                // Create wall
                Wall wall;
                using (var tx = new Transaction(doc, "MCP: Create Wall"))
                {
                    tx.Start();

                    wall = Wall.Create(doc, line, wallType.Id, level.Id, height, 0, false, structural);

                    cancellationToken.ThrowIfCancellationRequested();
                    tx.CommitOrThrow();
                }

                // Harness Engineering — Tier 1: Post-transaction verification.
                // After commit, re-query the created wall and compare actual geometry
                // against requested geometry. Claude can use this to self-correct
                // if the creation produced unexpected results.
                //
                // NOTE: Revit places walls at level.Elevation (not Z=0), so we compare
                // expected points at (X, Y, level.Elevation), not the Z=0 line we created.
                var expectedStartWithLevel = new XYZ(startX, startY, level.Elevation);
                var expectedEndWithLevel = new XYZ(endX, endY, level.Elevation);
                var verification = VerifyCreatedWall(doc, wall, expectedStartWithLevel, expectedEndWithLevel, height, level, wallType);

                // Return info about the created wall
                return Task.FromResult(CommandResult.Ok(new Dictionary<string, object>
                {
                    ["element_id"] = wall.Id.GetValue(),
                    ["wall_type"] = wallType.Name,
                    ["level"] = level.Name,
                    ["height_feet"] = height,
                    ["height_mm"] = Math.Round(height * 304.8, 1),
                    ["length_feet"] = Math.Round(line.Length, 4),
                    ["length_mm"] = Math.Round(line.Length * 304.8, 1),
                    ["structural"] = structural,
                    ["start"] = new Dictionary<string, double>
                    {
                        ["x"] = startX, ["y"] = startY
                    },
                    ["end"] = new Dictionary<string, double>
                    {
                        ["x"] = endX, ["y"] = endY
                    },
                    ["mutation_committed"] = true,
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
                    $"Failed to create wall: {ex.Message}",
                    "Check coordinates and wall type. Ensure a valid level exists."));
            }
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

        private WallType ResolveWallType(Document doc, string typeName)
        {
            var wallTypes = new FilteredElementCollector(doc)
                .OfClass(typeof(WallType))
                .Cast<WallType>()
                .ToList();

            if (wallTypes.Count == 0) return null;

            if (string.IsNullOrEmpty(typeName))
                return wallTypes.First();

            return wallTypes.FirstOrDefault(wt =>
                wt.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase))
                ?? wallTypes.FirstOrDefault(wt =>
                    wt.Name.IndexOf(typeName, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        /// <summary>
        /// Post-transaction verification. Re-queries the created wall and compares
        /// actual geometry/parameters against the request. Tolerances are in feet.
        ///
        /// Returns a structured dictionary with pass/fail per check and a
        /// geometry_match summary flag. Claude can use this to self-correct.
        /// </summary>
        private static Dictionary<string, object> VerifyCreatedWall(
            Document doc,
            Wall wall,
            XYZ expectedStart,
            XYZ expectedEnd,
            double expectedHeight,
            Level expectedLevel,
            WallType expectedType)
        {
            const double POSITION_TOLERANCE = 0.01; // ~3mm in feet
            const double HEIGHT_TOLERANCE = 0.01;

            var issues = new List<string>();
            var result = new Dictionary<string, object>
            {
                ["performed"] = true,
                ["geometry_match"] = true
            };

            try
            {
                // Re-fetch the wall to ensure we're reading committed state
                var refetched = doc.GetElement(wall.Id) as Wall;
                if (refetched == null)
                {
                    result["geometry_match"] = false;
                    result["issues"] = new List<string> { "Wall not found after commit — creation may have failed." };
                    return result;
                }

                // Check location curve
                var locCurve = refetched.Location as LocationCurve;
                if (locCurve?.Curve is Line actualLine)
                {
                    var actualStart = actualLine.GetEndPoint(0);
                    var actualEnd = actualLine.GetEndPoint(1);

                    var startDiff = actualStart.DistanceTo(expectedStart);
                    var endDiff = actualEnd.DistanceTo(expectedEnd);

                    result["actual_start"] = new Dictionary<string, double>
                    {
                        ["x"] = Math.Round(actualStart.X, 4),
                        ["y"] = Math.Round(actualStart.Y, 4),
                        ["z"] = Math.Round(actualStart.Z, 4)
                    };
                    result["actual_end"] = new Dictionary<string, double>
                    {
                        ["x"] = Math.Round(actualEnd.X, 4),
                        ["y"] = Math.Round(actualEnd.Y, 4),
                        ["z"] = Math.Round(actualEnd.Z, 4)
                    };
                    result["start_offset_feet"] = Math.Round(startDiff, 6);
                    result["end_offset_feet"] = Math.Round(endDiff, 6);

                    if (startDiff > POSITION_TOLERANCE)
                        issues.Add($"Start point offset by {Math.Round(startDiff * 304.8, 1)}mm from request.");
                    if (endDiff > POSITION_TOLERANCE)
                        issues.Add($"End point offset by {Math.Round(endDiff * 304.8, 1)}mm from request.");
                }
                else
                {
                    issues.Add("Could not read wall location curve.");
                }

                // Check height
                var heightParam = refetched.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM);
                if (heightParam != null)
                {
                    var actualHeight = heightParam.AsDouble();
                    result["actual_height_feet"] = Math.Round(actualHeight, 4);
                    if (Math.Abs(actualHeight - expectedHeight) > HEIGHT_TOLERANCE)
                        issues.Add($"Height {Math.Round(actualHeight * 304.8, 1)}mm differs from requested {Math.Round(expectedHeight * 304.8, 1)}mm.");
                }

                // Check level
                var levelIdParam = refetched.LevelId;
                if (levelIdParam != expectedLevel.Id)
                    issues.Add($"Wall attached to a different level than requested.");

                // Check type
                if (refetched.WallType?.Id != expectedType.Id)
                    issues.Add($"Wall type mismatch: expected '{expectedType.Name}'.");

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
