using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
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
    ///   pipe_type       (string|int)      — PipeType name (contains) or ElementId
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
                        "Provide points as [{\"e\":228231.2,\"n\":506653.9,\"z\":130.29}, ...] (survey) " +
                        "or [{\"x\":..,\"y\":..,\"z\":..}, ...] (internal)."));

                if (rawPts.Count > MaxPoints)
                    return Task.FromResult(CommandResult.Fail(
                        $"Too many points: {rawPts.Count} (max {MaxPoints}).",
                        "Split into multiple runs."));

                var mode = (GetStr(parameters, "coordinate_mode", "survey") ?? "survey").ToLowerInvariant();
                var unit = (GetStr(parameters, "input_unit", "m") ?? "m").ToLowerInvariant();
                double unitToFt = unit == "mm" ? 1.0 / 304.8 : 1.0 / 0.3048;  // m default

                // ─── resolve pipe type ───
                var pipeTypes = new FilteredElementCollector(doc).OfClass(typeof(PipeType))
                    .Cast<PipeType>().ToList();
                if (pipeTypes.Count == 0)
                    return Task.FromResult(CommandResult.Fail(
                        "No PipeType found in this project.",
                        "Load a pipe family/type first."));

                PipeType pipeType = null;
                if (parameters.TryGetValue("pipe_type", out var ptRaw) && ptRaw != null)
                {
                    if (int.TryParse(ptRaw.ToString(), out var ptId))
                        pipeType = pipeTypes.FirstOrDefault(t => t.Id.IntegerValue == ptId);
                    if (pipeType == null)
                        pipeType = pipeTypes.FirstOrDefault(t =>
                            t.Name.IndexOf(ptRaw.ToString(), StringComparison.OrdinalIgnoreCase) >= 0);
                    if (pipeType == null)
                        return Task.FromResult(CommandResult.Fail(
                            $"PipeType '{ptRaw}' not found.",
                            "Available: " + string.Join(", ", pipeTypes.Select(t => t.Name))));
                }
                else pipeType = pipeTypes.First();

                // ─── resolve system type ───
                ElementId sysTypeId;
                if (parameters.TryGetValue("system_type_id", out var stRaw) && stRaw != null
                    && int.TryParse(stRaw.ToString(), out var stId))
                    sysTypeId = new ElementId(stId);
                else
                {
                    var st = new FilteredElementCollector(doc).OfClass(typeof(PipingSystemType)).FirstElement();
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
                    toInternal = (x, y, z) => new XYZ(x * unitToFt, y * unitToFt, z * unitToFt);
                    coordNote = "internal coordinates (unit-scaled only)";
                }
                else // survey
                {
                    var pl = doc.ActiveProjectLocation;
                    var pp = pl.GetProjectPosition(XYZ.Zero);
                    double EW0 = pp.EastWest, NS0 = pp.NorthSouth, ang = pp.Angle, EL0 = pp.Elevation;

                    // Guard: shared coordinates not configured → conversion is meaningless.
                    if (Math.Abs(EW0) < 1e-6 && Math.Abs(NS0) < 1e-6 && Math.Abs(ang) < 1e-9)
                        return Task.FromResult(CommandResult.Fail(
                            "This project has no Shared Coordinates configured (survey origin is 0,0,0).",
                            "Set up Shared Coordinates in Revit, or call again with coordinate_mode=\"internal\"."));

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
                foreach (var o in rawPts)
                {
                    if (!(o is Dictionary<string, object> d))
                        return Task.FromResult(CommandResult.Fail(
                            "Each point must be an object.",
                            "Use {e,n,z} for survey or {x,y,z} for internal."));
                    double a = GetNum(d, mode == "internal" ? "x" : "e");
                    double b = GetNum(d, mode == "internal" ? "y" : "n");
                    double zz = GetNum(d, "z");
                    pts.Add(toInternal(a, b, zz));
                    surveyForVerify.Add(new[] { a, b, zz });
                }

                // ─── reference level: nearest by elevation, or named ───
                Level level;
                var namedLevel = GetStr(parameters, "level_name", null);
                if (!string.IsNullOrEmpty(namedLevel))
                {
                    level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                        .FirstOrDefault(l => l.Name.Equals(namedLevel, StringComparison.OrdinalIgnoreCase));
                    if (level == null)
                        return Task.FromResult(CommandResult.Fail(
                            $"Level '{namedLevel}' not found.",
                            "Omit level_name to auto-pick the nearest level."));
                }
                else
                {
                    double avgZ = pts.Average(p => p.Z);
                    level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                        .OrderBy(l => Math.Abs(l.Elevation - avgZ)).First();
                }

                double? diameterFt = null;
                if (parameters.TryGetValue("diameter_mm", out var diaRaw) && diaRaw != null
                    && double.TryParse(diaRaw.ToString(), out var diaMm) && diaMm > 0)
                    diameterFt = diaMm / 304.8;

                bool connectElbows = GetBool(parameters, "connect_elbows", true);

                cancellationToken.ThrowIfCancellationRequested();

                // ─── create in one transaction ───
                var pipeIds = new List<int>();
                var pipes = new List<Pipe>();
                int elbows = 0;
                var elbowFailures = new List<string>();

                using (var tx = new Transaction(doc, $"MCP: Pipe run ({pts.Count} pts)"))
                {
                    tx.Start();

                    for (int i = 0; i < pts.Count - 1; i++)
                    {
                        // skip zero-length segments
                        if (pts[i].DistanceTo(pts[i + 1]) < 1e-6) continue;
                        var pipe = Pipe.Create(doc, sysTypeId, pipeType.Id, level.Id, pts[i], pts[i + 1]);
                        if (diameterFt.HasValue)
                        {
                            var dp = pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
                            if (dp != null && !dp.IsReadOnly) dp.Set(diameterFt.Value);
                        }
                        pipes.Add(pipe);
                        pipeIds.Add(pipe.Id.IntegerValue);
                    }

                    doc.Regenerate();

                    if (connectElbows)
                    {
                        for (int i = 0; i < pipes.Count - 1; i++)
                        {
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

                    tx.Commit();
                }

                // ─── post-tx verification: first point survey round-trip ───
                Dictionary<string, object> verification = null;
                if (pipes.Count > 0 && mode == "survey")
                {
                    var pl = doc.ActiveProjectLocation;
                    var firstInternal = ((pipes[0].Location as LocationCurve).Curve).GetEndPoint(0);
                    var sp = pl.GetProjectPosition(firstInternal);
                    double mPerFt = 0.3048;
                    double gotE = sp.EastWest * mPerFt, gotN = sp.NorthSouth * mPerFt, gotZ = sp.Elevation * mPerFt;
                    double expE = surveyForVerify[0][0] * (unit == "mm" ? 0.001 : 1.0);
                    double expN = surveyForVerify[0][1] * (unit == "mm" ? 0.001 : 1.0);
                    double expZ = surveyForVerify[0][2] * (unit == "mm" ? 0.001 : 1.0);
                    double err = Math.Sqrt((gotE - expE) * (gotE - expE) + (gotN - expN) * (gotN - expN));
                    verification = new Dictionary<string, object>
                    {
                        ["performed"] = true,
                        ["first_point_expected_survey_m"] = new[] { Math.Round(expE, 4), Math.Round(expN, 4), Math.Round(expZ, 4) },
                        ["first_point_actual_survey_m"] = new[] { Math.Round(gotE, 4), Math.Round(gotN, 4), Math.Round(gotZ, 4) },
                        ["horizontal_error_m"] = Math.Round(err, 4),
                        ["match"] = err < 0.01
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
                    ["coord_note"] = coordNote
                };
                if (elbowFailures.Count > 0) data["elbow_failures"] = elbowFailures;
                if (verification != null) data["verification"] = verification;

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

        private static double GetNum(Dictionary<string, object> d, string k)
            => d.TryGetValue(k, out var v) && v != null && double.TryParse(v.ToString(), out var n) ? n : 0.0;

        private static bool GetBool(Dictionary<string, object> p, string k, bool def)
        {
            if (p == null || !p.TryGetValue(k, out var v) || v == null) return def;
            try { return Convert.ToBoolean(v); } catch { return def; }
        }
    }
}
