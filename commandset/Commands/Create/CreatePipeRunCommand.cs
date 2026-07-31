using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using RevitMCP.CommandSet.Helpers;
using RevitMCP.CommandSet.Interfaces;

namespace RevitMCP.CommandSet.Commands.Create
{
    /// <summary>
    /// Create a connected run of pipes through a list of points, optionally
    /// joined with elbow fittings at each interior vertex.
    ///
    /// Promoted from the execute_script CAD→Revit pipe workflow (CLAUDE.md §7).
    ///
    /// Coordinate handling is PROJECT-PORTABLE:
    ///   coordinate_mode="survey" (default) — points are shared/survey
    ///   coordinates. The command reads THIS document's ActiveProjectLocation
    ///   at runtime and converts them to internal coordinates, so the same
    ///   survey points land correctly in any project that has Shared
    ///   Coordinates configured. The rotation sign is auto-detected per
    ///   project via a round-trip test point (no hard-coded transform).
    ///   coordinate_mode="internal" — points are raw Revit internal feet.
    ///
    /// Parameters:
    ///   points          (array, required) — [{e,n,z}] survey or [{x,y,z}] internal. Min 2.
    ///   coordinate_mode (string)          — "survey" (default) | "internal"
    ///   input_unit      (string)          — survey/elevation unit: "m" (default) | "mm"
    ///   pipe_type       (string|int)      — PipeType ElementId or name (exact first,
    ///                                       then an unambiguous contains match)
    ///   system_type_id  (int, optional)   — PipingSystemType id (default: first found)
    ///   diameter_mm     (number, optional)— pipe diameter in mm (default: type default)
    ///   level_name      (string, optional)— reference level (default: nearest by elevation)
    ///   connect_elbows  (bool)            — insert elbow fittings at vertices (default true)
    ///   idempotency_key (string, optional)
    /// </summary>
    public class CreatePipeRunCommand : IRevitCommand
    {
        public string Name => "create_pipe_run";
        public string Category => "Create";

        private const int MaxPoints = 500;

        public Task<CommandResult> ExecuteAsync(
            Document doc,
            Dictionary<string, object> parameters,
            CancellationToken cancellationToken)
        {
            try
            {
                // ─── points ───
                if (parameters == null || !parameters.TryGetValue("points", out var ptsObj)
                    || !(ptsObj is List<object> rawPts) || rawPts.Count < 2)
                    return Task.FromResult(CommandResult.Fail(
                        "Missing or invalid 'points' (need at least 2).",
                        "Provide points as [{\"e\":500000,\"n\":200000,\"z\":100}, ...] (survey) " +
                        "or [{\"x\":..,\"y\":..,\"z\":..}, ...] (internal)."));

                if (rawPts.Count > MaxPoints)
                    return Task.FromResult(CommandResult.Fail(
                        $"Too many points: {rawPts.Count} (max {MaxPoints}).",
                        "Split into multiple runs."));

                var mode = (GetStr(parameters, "coordinate_mode", "survey") ?? "survey").ToLowerInvariant();
                if (mode != "survey" && mode != "internal")
                    return Task.FromResult(CommandResult.Fail(
                        $"Invalid coordinate_mode '{mode}'.",
                        "Use coordinate_mode=\"survey\" or coordinate_mode=\"internal\"."));

                var unit = (GetStr(parameters, "input_unit", mode == "internal" ? "ft" : "m")
                    ?? (mode == "internal" ? "ft" : "m")).ToLowerInvariant();
                if (mode == "internal" && unit != "ft")
                    return Task.FromResult(CommandResult.Fail(
                        $"Internal coordinates use raw Revit feet; input_unit '{unit}' is not valid.",
                        "Set input_unit=\"ft\" or omit input_unit when coordinate_mode=\"internal\"."));
                if (mode == "survey" && unit != "m" && unit != "mm")
                    return Task.FromResult(CommandResult.Fail(
                        $"Survey coordinates support input_unit \"m\" or \"mm\", not '{unit}'.",
                        "Use input_unit=\"m\" or input_unit=\"mm\"."));

                if (!RawParameterValidation.TryGetOptionalStrictBool(
                        parameters,
                        "connect_elbows",
                        defaultValue: true,
                        out var connectElbows,
                        out var connectElbowsError))
                {
                    return Task.FromResult(CommandResult.Fail(
                        connectElbowsError,
                        "Pass connect_elbows as true or false, or omit it to use true."));
                }
                if (!RawParameterValidation.TryGetOptionalStrictBool(
                        parameters,
                        "allow_identity_transform",
                        defaultValue: false,
                        out var allowIdentityTransform,
                        out var allowIdentityError))
                {
                    return Task.FromResult(CommandResult.Fail(
                        allowIdentityError,
                        "Pass allow_identity_transform as true or false, or omit it to use false."));
                }

                double unitToFt = unit == "mm" ? 1.0 / 304.8 : 1.0 / 0.3048;

                // ─── resolve pipe type ───
                var pipeTypes = new FilteredElementCollector(doc).OfClass(typeof(PipeType))
                    .Cast<PipeType>()
                    .OrderBy(type => type.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(type => type.Id.GetValue())
                    .ToList();
                if (pipeTypes.Count == 0)
                    return Task.FromResult(CommandResult.Fail(
                        "No PipeType found in this project.",
                        "Load a pipe family/type first."));

                PipeType pipeType = null;
                if (parameters.TryGetValue("pipe_type", out var ptRaw) && ptRaw != null)
                {
                    var requestedType = ptRaw.ToString()?.Trim();
                    if (string.IsNullOrWhiteSpace(requestedType))
                        return Task.FromResult(CommandResult.Fail(
                            "pipe_type must not be empty.",
                            "Pass an exact PipeType name, an unambiguous name fragment, or an ElementId."));

                    // A string can legitimately be a numeric-looking type
                    // name, so exact name matching must precede ID parsing.
                    var exactMatches = pipeTypes.Where(t =>
                        t.Name.Equals(
                            requestedType,
                            StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    if (exactMatches.Count == 1)
                    {
                        pipeType = exactMatches[0];
                    }
                    else if (exactMatches.Count > 1)
                    {
                        return Task.FromResult(CommandResult.Fail(
                            $"PipeType name '{requestedType}' is ambiguous ({exactMatches.Count} exact matches).",
                            "Use the ElementId of the intended type. Matches: " +
                            string.Join(", ", exactMatches.Take(20).Select(t =>
                                $"{t.Name} (id {t.Id.GetValue()})"))));
                    }

                    var parsedElementId = long.TryParse(
                        requestedType,
                        out var ptId);
                    if (pipeType == null && parsedElementId)
                    {
                        pipeType = pipeTypes.FirstOrDefault(
                            t => t.Id.GetValue() == ptId);
                        if (pipeType == null && !(ptRaw is string))
                            return Task.FromResult(CommandResult.Fail(
                                $"PipeType id '{ptId}' not found.",
                                "Query the available pipe types and retry with a valid ElementId."));
                    }

                    if (pipeType == null)
                    {
                        var containsMatches = pipeTypes.Where(t =>
                            t.Name.IndexOf(
                                requestedType,
                                StringComparison.OrdinalIgnoreCase) >= 0)
                            .ToList();
                        if (containsMatches.Count == 1)
                        {
                            pipeType = containsMatches[0];
                        }
                        else if (containsMatches.Count > 1)
                        {
                            return Task.FromResult(CommandResult.Fail(
                                $"PipeType name '{requestedType}' is ambiguous ({containsMatches.Count} matches).",
                                "Use the exact name or ElementId. Matches: " +
                                string.Join(", ", containsMatches.Take(20).Select(t =>
                                    $"{t.Name} (id {t.Id.GetValue()})"))));
                        }
                    }

                    if (pipeType == null)
                        return Task.FromResult(CommandResult.Fail(
                            parsedElementId
                                ? $"No PipeType name or id matched '{requestedType}'."
                                : $"PipeType '{requestedType}' not found.",
                            "Available: " + string.Join(", ", pipeTypes.Take(20).Select(t =>
                                $"{t.Name} (id {t.Id.GetValue()})"))));
                }
                else pipeType = pipeTypes.First();

                // ─── resolve system type ───
                ElementId sysTypeId;
                if (parameters.TryGetValue("system_type_id", out var stRaw) &&
                    stRaw != null)
                {
                    if (!long.TryParse(stRaw.ToString(), out var stId))
                        return Task.FromResult(CommandResult.Fail(
                            $"Invalid system_type_id '{stRaw}'.",
                            "Pass the ElementId of a PipingSystemType, or omit it to use the first available type."));

                    var requestedSystemType =
                        doc.GetElement(ElementIdCompatibility.Create(stId))
                        as PipingSystemType;
                    if (requestedSystemType == null)
                        return Task.FromResult(CommandResult.Fail(
                            $"PipingSystemType id '{stId}' was not found.",
                            "Query the available piping system types and retry with a valid ElementId."));
                    sysTypeId = requestedSystemType.Id;
                }
                else
                {
                    var st = new FilteredElementCollector(doc)
                        .OfClass(typeof(PipingSystemType))
                        .Cast<PipingSystemType>()
                        .OrderBy(type => type.Name, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(type => type.Id.GetValue())
                        .FirstOrDefault();
                    if (st == null)
                        return Task.FromResult(CommandResult.Fail(
                            "No PipingSystemType found.",
                            "Create a piping system type first."));
                    sysTypeId = st.Id;
                }

                // ─── coordinate conversion (project-portable) ───
                Func<double, double, double, XYZ> toInternal;
                string coordNote;

                if (mode == "internal")
                {
                    toInternal = (x, y, z) => new XYZ(x, y, z);
                    coordNote = "raw Revit internal coordinates (feet)";
                }
                else // survey
                {
                    var pl = doc.ActiveProjectLocation;
                    var pp = pl.GetProjectPosition(XYZ.Zero);
                    double EW0 = pp.EastWest, NS0 = pp.NorthSouth, ang = pp.Angle, EL0 = pp.Elevation;

                    // An all-zero project transform is indistinguishable from an
                    // intentionally configured identity transform. Fail closed by
                    // default and allow only an explicit, informed override.
                    if (!allowIdentityTransform &&
                        Math.Abs(EW0) < 1e-6 &&
                        Math.Abs(NS0) < 1e-6 &&
                        Math.Abs(EL0) < 1e-6 &&
                        Math.Abs(ang) < 1e-9)
                        return Task.FromResult(CommandResult.Fail(
                            "The project location uses an identity survey transform (east/west, north/south, elevation, and angle are all zero).",
                            "Verify Shared Coordinates, use coordinate_mode=\"internal\", or set allow_identity_transform=true only when survey and internal coordinates intentionally match."));

                    double c = Math.Cos(ang), s = Math.Sin(ang);

                    // Auto-detect rotation sign via a round-trip test point.
                    var testInternal = new XYZ(100, 50, 0);
                    var ts = pl.GetProjectPosition(testInternal);
                    double tdE = ts.EastWest - EW0, tdN = ts.NorthSouth - NS0;
                    var c1 = new XYZ(tdE * c + tdN * s, -tdE * s + tdN * c, 0);
                    var c2 = new XYZ(tdE * c - tdN * s, tdE * s + tdN * c, 0);
                    bool useC1 = c1.DistanceTo(testInternal) <= c2.DistanceTo(testInternal);

                    toInternal = (e, n, z) =>
                    {
                        double dE = e * unitToFt - EW0, dN = n * unitToFt - NS0;
                        double ix = useC1 ? (dE * c + dN * s) : (dE * c - dN * s);
                        double iy = useC1 ? (-dE * s + dN * c) : (dE * s + dN * c);
                        double iz = z * unitToFt - EL0;
                        return new XYZ(ix, iy, iz);
                    };
                    coordNote = $"survey→internal (auto sign {(useC1 ? "1" : "2")}, EL0={EL0:F2}ft)";
                }

                // ─── build internal points ───
                var pts = new List<XYZ>();
                var surveyForVerify = new List<double[]>();
                for (int pointIndex = 0; pointIndex < rawPts.Count; pointIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var o = rawPts[pointIndex];
                    if (!(o is Dictionary<string, object> d))
                        return Task.FromResult(CommandResult.Fail(
                            $"Point at index {pointIndex} must be an object.",
                            "Use {e,n,z} for survey or {x,y,z} for internal."));
                    var firstKey = mode == "internal" ? "x" : "e";
                    var secondKey = mode == "internal" ? "y" : "n";
                    if (!TryGetFiniteNumber(d, firstKey, out var a)
                        || !TryGetFiniteNumber(d, secondKey, out var b)
                        || !TryGetFiniteNumber(d, "z", out var zz))
                        return Task.FromResult(CommandResult.Fail(
                            $"Point at index {pointIndex} is missing a finite numeric {firstKey}, {secondKey}, or z value.",
                            mode == "internal"
                                ? "Provide every point as {\"x\":number,\"y\":number,\"z\":number} in raw Revit feet."
                                : "Provide every point as {\"e\":number,\"n\":number,\"z\":number} in the selected survey unit."));
                    pts.Add(toInternal(a, b, zz));
                    surveyForVerify.Add(new[] { a, b, zz });
                }

                var firstNonZeroSegment = -1;
                for (int i = 0; i < pts.Count - 1; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (pts[i].DistanceTo(pts[i + 1]) >= 1e-6)
                    {
                        firstNonZeroSegment = i;
                        break;
                    }
                }
                if (firstNonZeroSegment < 0)
                    return Task.FromResult(CommandResult.Fail(
                        "All requested pipe segments have zero length.",
                        "Provide at least two distinct points."));

                // ─── reference level: nearest by elevation, or named ───
                var levels = new FilteredElementCollector(doc)
                    .OfClass(typeof(Level))
                    .Cast<Level>()
                    .ToList();
                if (levels.Count == 0)
                    return Task.FromResult(CommandResult.Fail(
                        "No Level found in this project.",
                        "Create a level before creating a pipe run."));

                Level level;
                var namedLevel = GetStr(parameters, "level_name", null);
                if (!string.IsNullOrEmpty(namedLevel))
                {
                    level = levels
                        .FirstOrDefault(l => l.Name.Equals(namedLevel, StringComparison.OrdinalIgnoreCase));
                    if (level == null)
                        return Task.FromResult(CommandResult.Fail(
                            $"Level '{namedLevel}' not found.",
                            "Omit level_name to auto-pick the nearest level."));
                }
                else
                {
                    double avgZ = pts.Average(p => p.Z);
                    level = levels
                        .OrderBy(l => Math.Abs(l.Elevation - avgZ)).First();
                }

                double? diameterFt = null;
                if (parameters.TryGetValue("diameter_mm", out var diaRaw) && diaRaw != null)
                {
                    if (!double.TryParse(diaRaw.ToString(), out var diaMm)
                        || double.IsNaN(diaMm)
                        || double.IsInfinity(diaMm)
                        || diaMm <= 0)
                    {
                        return Task.FromResult(CommandResult.Fail(
                            $"Invalid diameter_mm '{diaRaw}'.",
                            "Provide a finite number greater than zero in millimetres."));
                    }
                    diameterFt = diaMm / 304.8;
                }

                cancellationToken.ThrowIfCancellationRequested();

                // ─── create in one transaction ───
                var pipeIds = new List<long>();
                var pipes = new List<Pipe>();
                int elbows = 0;
                var elbowFailures = new List<string>();

                using (var tx = new Transaction(doc, $"MCP: Pipe run ({pts.Count} pts)"))
                {
                    tx.Start();

                    for (int i = 0; i < pts.Count - 1; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        // skip zero-length segments
                        if (pts[i].DistanceTo(pts[i + 1]) < 1e-6) continue;
                        var pipe = Pipe.Create(doc, sysTypeId, pipeType.Id, level.Id, pts[i], pts[i + 1]);
                        if (diameterFt.HasValue)
                        {
                            var dp = pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
                            if (dp == null || dp.IsReadOnly || !dp.Set(diameterFt.Value))
                                throw new InvalidOperationException(
                                    $"Could not set diameter on pipe {pipe.Id.GetValue()}; the transaction was rolled back.");
                        }
                        pipes.Add(pipe);
                        pipeIds.Add(pipe.Id.GetValue());
                    }

                    doc.Regenerate();

                    if (connectElbows)
                    {
                        for (int i = 0; i < pipes.Count - 1; i++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            // Shared vertex = the end of pipe[i] closest to pipe[i+1].
                            var a0 = GetEnd(pipes[i], 0);
                            var a1 = GetEnd(pipes[i], 1);
                            var b0 = GetEnd(pipes[i + 1], 0);
                            var b1 = GetEnd(pipes[i + 1], 1);
                            var join = Math.Min(a0.DistanceTo(b0), a0.DistanceTo(b1))
                                       <= Math.Min(a1.DistanceTo(b0), a1.DistanceTo(b1)) ? a0 : a1;
                            var ca = NearestConn(pipes[i], join);
                            var cb = NearestConn(pipes[i + 1], join);
                            if (ca == null || cb == null) { elbowFailures.Add($"vertex {i + 1}: connector not found"); continue; }
                            try
                            {
                                doc.Create.NewElbowFitting(ca, cb);
                                elbows++;
                            }
                            catch (Exception ex)
                            {
                                elbowFailures.Add($"vertex {i + 1}: {ex.Message}");
                            }
                        }
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    tx.CommitOrThrow();
                }

                // ─── post-tx verification: first point survey round-trip ───
                Dictionary<string, object> verification;
                try
                {
                    var firstInternal = ((pipes[0].Location as LocationCurve).Curve).GetEndPoint(0);
                    if (mode == "survey")
                    {
                        var pl = doc.ActiveProjectLocation;
                        var sp = pl.GetProjectPosition(firstInternal);
                        double mPerFt = 0.3048;
                        double gotE = sp.EastWest * mPerFt;
                        double gotN = sp.NorthSouth * mPerFt;
                        double gotZ = sp.Elevation * mPerFt;
                        double expE = surveyForVerify[firstNonZeroSegment][0] * (unit == "mm" ? 0.001 : 1.0);
                        double expN = surveyForVerify[firstNonZeroSegment][1] * (unit == "mm" ? 0.001 : 1.0);
                        double expZ = surveyForVerify[firstNonZeroSegment][2] * (unit == "mm" ? 0.001 : 1.0);
                        double err = Math.Sqrt(
                            (gotE - expE) * (gotE - expE)
                            + (gotN - expN) * (gotN - expN)
                            + (gotZ - expZ) * (gotZ - expZ));
                        verification = new Dictionary<string, object>
                        {
                            ["performed"] = true,
                            ["first_point_expected_survey_m"] = new[] { Math.Round(expE, 4), Math.Round(expN, 4), Math.Round(expZ, 4) },
                            ["first_point_actual_survey_m"] = new[] { Math.Round(gotE, 4), Math.Round(gotN, 4), Math.Round(gotZ, 4) },
                            ["three_dimensional_error_m"] = Math.Round(err, 4),
                            ["match"] = err < 0.01
                        };
                    }
                    else
                    {
                        var expected = pts[firstNonZeroSegment];
                        var err = firstInternal.DistanceTo(expected);
                        verification = new Dictionary<string, object>
                        {
                            ["performed"] = true,
                            ["first_point_expected_internal_ft"] = new[]
                            {
                                Math.Round(expected.X, 6), Math.Round(expected.Y, 6), Math.Round(expected.Z, 6)
                            },
                            ["first_point_actual_internal_ft"] = new[]
                            {
                                Math.Round(firstInternal.X, 6), Math.Round(firstInternal.Y, 6), Math.Round(firstInternal.Z, 6)
                            },
                            ["error_feet"] = Math.Round(err, 6),
                            ["match"] = err < 0.01
                        };
                    }

                    if (diameterFt.HasValue)
                    {
                        var diameterParameter = pipes[0].get_Parameter(
                            BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
                        if (diameterParameter == null)
                            throw new InvalidOperationException(
                                "Created pipe has no diameter parameter for verification.");
                        var actualDiameterFt = diameterParameter.AsDouble();
                        var diameterErrorFt = Math.Abs(actualDiameterFt - diameterFt.Value);
                        var diameterMatch = diameterErrorFt < (0.1 / 304.8);
                        verification["diameter_expected_mm"] =
                            Math.Round(diameterFt.Value * 304.8, 3);
                        verification["diameter_actual_mm"] =
                            Math.Round(actualDiameterFt * 304.8, 3);
                        verification["diameter_match"] = diameterMatch;
                        verification["match"] =
                            Convert.ToBoolean(verification["match"]) && diameterMatch;
                    }

                    if (connectElbows && elbowFailures.Count > 0)
                    {
                        verification["elbow_match"] = false;
                        verification["match"] = false;
                    }
                    else if (connectElbows)
                    {
                        verification["elbow_match"] = true;
                    }
                }
                catch (Exception verificationError)
                {
                    verification = new Dictionary<string, object>
                    {
                        ["performed"] = false,
                        ["match"] = false,
                        ["error"] = verificationError.Message
                    };
                }

                var data = new Dictionary<string, object>
                {
                    ["pipe_ids"] = pipeIds,
                    ["pipe_count"] = pipeIds.Count,
                    ["elbow_count"] = elbows,
                    ["pipe_type"] = pipeType.Name,
                    ["reference_level"] = level.Name,
                    ["diameter_mm"] = diameterFt.HasValue ? (object)Math.Round(diameterFt.Value * 304.8) : "type default",
                    ["coordinate_mode"] = mode,
                    ["input_unit"] = unit,
                    ["coord_note"] = coordNote,
                    ["mutation_committed"] = true,
                    ["verification"] = verification
                };
                if (elbowFailures.Count > 0) data["elbow_failures"] = elbowFailures;

                return Task.FromResult(CommandResult.Ok(data));
            }
            catch (OperationCanceledException)
            {
                return Task.FromResult(CommandResult.Fail(
                    "Pipe run creation was cancelled.",
                    "Retry with fewer points."));
            }
            catch (Exception ex)
            {
                return Task.FromResult(CommandResult.Fail(
                    $"create_pipe_run failed: {ex.Message}",
                    "Verify pipe_type exists and points are valid. If the transaction failed, nothing was committed."));
            }
        }

        private static XYZ GetEnd(Pipe p, int i) =>
            ((p.Location as LocationCurve).Curve).GetEndPoint(i);

        private static Connector NearestConn(Pipe p, XYZ pt)
        {
            Connector best = null; double bd = double.MaxValue;
            foreach (Connector con in p.ConnectorManager.Connectors)
            {
                double d = con.Origin.DistanceTo(pt);
                if (d < bd) { bd = d; best = con; }
            }
            return best;
        }

        private static string GetStr(Dictionary<string, object> p, string k, string def)
            => p != null && p.TryGetValue(k, out var v) && v != null ? v.ToString() : def;

        private static bool TryGetFiniteNumber(
            Dictionary<string, object> values,
            string key,
            out double number)
        {
            number = 0;
            if (values == null || !values.TryGetValue(key, out var raw) || raw == null
                || !double.TryParse(raw.ToString(), out number))
                return false;
            return !double.IsNaN(number) && !double.IsInfinity(number);
        }

    }
}
