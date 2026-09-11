using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using RevitMCP.CommandSet.Helpers;
using RevitMCP.CommandSet.Interfaces;

namespace RevitMCP.CommandSet.Commands.Modify
{
    /// <summary>
    /// Set selected walls' unconnected height to the underside of the
    /// nearest linked structural floor above each wall.
    ///
    /// Linked floors are not attachable in Revit; this command reads
    /// soffit Z from ST link geometry and writes WALL_USER_HEIGHT_PARAM.
    /// One wall still has one height — a plan-step across the wall is
    /// reported and the lowest soffit is used so the wall does not
    /// penetrate the lower slab.
    ///
    /// Parameters:
    ///   element_ids          (int[], optional) — wall IDs; defaults to UI selection
    ///   link_name_contains   (string, optional) — link title filter (default "ST_")
    ///   step_tolerance_mm    (number, optional) — soffit delta that counts as a step (default 20)
    ///   dry_run              (bool, optional) — compute only, no write (default true)
    /// </summary>
    public class ModifyWallHeightToLinkedSoffitCommand : IRevitCommand
    {
        public string Name => "modify_wall_height_to_linked_soffit";
        public string Category => "Modify";

        private const int MaxWalls = 50;
        private const double RayLengthFeet = 80.0;
        private const double VerifyTolFeet = 0.01; // ~3 mm

        public Task<CommandResult> ExecuteAsync(
            Document doc,
            Dictionary<string, object> parameters,
            CancellationToken cancellationToken)
        {
            try
            {
                parameters ??= new Dictionary<string, object>();

                if (!RawParameterValidation.TryGetOptionalStrictBool(
                        parameters,
                        "dry_run",
                        defaultValue: true,
                        out var dryRun,
                        out var validationError))
                {
                    return Task.FromResult(CommandResult.Fail(
                        validationError,
                        "Pass dry_run as true or false; omit it to measure without writing."));
                }

                string linkFilter;
                try
                {
                    linkFilter = GetString(parameters, "link_name_contains") ?? "ST_";
                }
                catch (ArgumentException ex)
                {
                    return Task.FromResult(CommandResult.Fail(
                        ex.Message,
                        "Pass a non-blank case-insensitive link-name substring, or omit it to use 'ST_'."));
                }

                if (!RawParameterValidation.TryGetOptionalFiniteDouble(
                        parameters,
                        "step_tolerance_mm",
                        defaultValue: 20.0,
                        out var stepTolMm,
                        out validationError) ||
                    stepTolMm < 0)
                {
                    return Task.FromResult(CommandResult.Fail(
                        "step_tolerance_mm must be a finite number >= 0.",
                        "Pass 20 for a 20 mm step threshold."));
                }
                var stepTolFeet = MmToFt(stepTolMm);

                List<long> requestedIds;
                try
                {
                    requestedIds = ResolveWallIds(parameters);
                }
                catch (ArgumentException ex)
                {
                    return Task.FromResult(CommandResult.Fail(
                        ex.Message,
                        "Pass element_ids as positive integers, or select walls in Revit first."));
                }

                if (requestedIds.Count == 0)
                    return Task.FromResult(CommandResult.Fail(
                        "No walls provided.",
                        "Select walls in Revit or pass element_ids."));

                if (requestedIds.Count > MaxWalls)
                    return Task.FromResult(CommandResult.Fail(
                        $"Too many walls ({requestedIds.Count}). Maximum is {MaxWalls} per call.",
                        "Run a smaller selection. This command is for targeted tests and batches."));

                var links = CollectStructuralLinks(doc, linkFilter);
                if (links.Count == 0)
                    return Task.FromResult(CommandResult.Fail(
                        $"No loaded Revit links matching '{linkFilter}'.",
                        "Reload the structural link, or pass link_name_contains matching the ST model title."));

                var floorCache = BuildFloorCache(links, cancellationToken);
                if (floorCache.Count == 0)
                    return Task.FromResult(CommandResult.Fail(
                        "No floors found in the matching linked models.",
                        "Confirm the ST link is loaded and contains Floor elements."));

                var plans = new List<WallPlan>();
                var skipped = new List<Dictionary<string, object>>();

                foreach (var id in requestedIds)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var element = doc.GetElement(ElementIdCompatibility.Create(id));
                    if (element is not Wall wall)
                    {
                        skipped.Add(Skip(id, "not_a_wall", "Element is not a wall."));
                        continue;
                    }

                    var plan = MeasureWall(doc, wall, floorCache, stepTolFeet, cancellationToken);
                    if (plan.Status == "ok" || plan.Status == "stepped")
                        plans.Add(plan);
                    else
                        skipped.Add(Skip(id, plan.Status, plan.Reason, plan));
                }

                var applied = new List<Dictionary<string, object>>();
                if (!dryRun && plans.Count > 0)
                {
                    using (var tx = new Transaction(doc, $"MCP: Wall height to linked soffit ({plans.Count})"))
                    {
                        tx.Start();
                        foreach (var plan in plans)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var wall = doc.GetElement(ElementIdCompatibility.Create(plan.WallId)) as Wall;
                            if (wall == null)
                            {
                                skipped.Add(Skip(plan.WallId, "missing_after_start", "Wall disappeared before write."));
                                continue;
                            }

                            using (var subTransaction = new SubTransaction(doc))
                            {
                                try
                                {
                                    subTransaction.Start();
                                    var writeError = ApplyHeight(wall, plan.NewHeightFeet);
                                    if (writeError != null)
                                    {
                                        subTransaction.RollBack();
                                        skipped.Add(Skip(plan.WallId, "write_failed", writeError, plan));
                                        continue;
                                    }

                                    cancellationToken.ThrowIfCancellationRequested();
                                    var subStatus = subTransaction.Commit();
                                    if (subStatus != TransactionStatus.Committed)
                                        throw new InvalidOperationException(
                                            $"Wall-height subtransaction did not commit ({subStatus}).");

                                    applied.Add(ToResult(plan, applied: true));
                                }
                                catch (OperationCanceledException)
                                {
                                    if (subTransaction.GetStatus() == TransactionStatus.Started)
                                        subTransaction.RollBack();
                                    throw;
                                }
                                catch (Exception itemError)
                                {
                                    if (subTransaction.GetStatus() == TransactionStatus.Started)
                                        subTransaction.RollBack();
                                    skipped.Add(Skip(plan.WallId, "write_failed", itemError.Message, plan));
                                }
                            }
                        }
                        tx.CommitOrThrow();
                    }

                    foreach (var row in applied)
                    {
                        var wall = doc.GetElement(ElementIdCompatibility.Create(Convert.ToInt64(row["id"]))) as Wall;
                        if (wall == null) continue;
                        var bbox = wall.get_BoundingBox(null);
                        var expected = Convert.ToDouble(row["soffit_z_ft"]);
                        var actualTop = bbox?.Max.Z ?? double.NaN;
                        var match = !double.IsNaN(actualTop) && Math.Abs(actualTop - expected) <= VerifyTolFeet;
                        row["verification"] = new Dictionary<string, object>
                        {
                            ["performed"] = true,
                            ["geometry_match"] = match,
                            ["actual_top_z_mm"] = double.IsNaN(actualTop) ? null : RoundMm(FtToMm(actualTop)),
                            ["expected_soffit_z_mm"] = RoundMm(FtToMm(expected)),
                            ["delta_mm"] = double.IsNaN(actualTop) ? null : RoundMm(FtToMm(actualTop - expected)),
                        };
                    }
                }
                else
                {
                    foreach (var plan in plans)
                        applied.Add(ToResult(plan, applied: false));
                }

                return Task.FromResult(CommandResult.Ok(new Dictionary<string, object>
                {
                    ["dry_run"] = dryRun,
                    ["requested"] = requestedIds.Count,
                    ["measured"] = plans.Count,
                    ["applied"] = dryRun ? 0 : applied.Count(r => true && Convert.ToBoolean(r["applied"])),
                    ["skipped"] = skipped.Count,
                    ["link_filter"] = linkFilter,
                    ["links_used"] = links.Select(l => l.Name).Distinct().ToList(),
                    ["step_tolerance_mm"] = stepTolMm,
                    ["walls"] = dryRun ? applied : applied,
                    ["skipped_items"] = skipped,
                    ["note"] = dryRun
                        ? "dry_run=true — no model changes. Re-run with dry_run=false to write heights."
                        : "Unconnected height was set to the linked structural soffit. Stepped walls used the lowest soffit along the wall.",
                }));
            }
            catch (OperationCanceledException)
            {
                return Task.FromResult(CommandResult.Fail(
                    "Wall-height measurement was cancelled; any active transaction was rolled back.",
                    "Retry with fewer walls or a narrower link_name_contains filter."));
            }
            catch (Exception ex)
            {
                return Task.FromResult(CommandResult.Fail(
                    $"Failed to set wall heights to linked soffit: {ex.Message}",
                    "Select a small set of walls on one level and retry. If the ST link is unloaded, reload it first."));
            }
        }

        private static WallPlan MeasureWall(
            Document doc,
            Wall wall,
            List<CachedFloor> floors,
            double stepTolFeet,
            CancellationToken cancellationToken)
        {
            var id = wall.Id.GetValue();
            var attached = wall.get_Parameter(BuiltInParameter.WALL_TOP_IS_ATTACHED);
            if (attached != null && attached.AsInteger() == 1)
            {
                return new WallPlan
                {
                    WallId = id,
                    Status = "top_attached",
                    Reason = "Top is already attached. Detach Top/Base in the UI first.",
                };
            }

            if (wall.Location is not LocationCurve loc || loc.Curve == null)
            {
                return new WallPlan
                {
                    WallId = id,
                    Status = "no_location",
                    Reason = "Wall has no location curve.",
                };
            }

            var baseLevelId = wall.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT)?.AsElementId();
            var baseLevel = baseLevelId == null || baseLevelId == ElementId.InvalidElementId
                ? null
                : doc.GetElement(baseLevelId) as Level;
            if (baseLevel == null)
            {
                return new WallPlan
                {
                    WallId = id,
                    Status = "no_base_level",
                    Reason = "Wall has no base constraint level.",
                };
            }

            var baseOffset = wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET)?.AsDouble() ?? 0;
            var currentHeight = wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.AsDouble() ?? 0;
            var wallBaseZ = baseLevel.Elevation + baseOffset;
            var curve = loc.Curve;
            var samples = new[] { 0.1, 0.5, 0.9 };
            var hits = new List<SoffitHit>();

            foreach (var t in samples)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var p = curve.Evaluate(t, true);
                var origin = new XYZ(p.X, p.Y, wallBaseZ + MmToFt(50));
                var hit = FindSoffit(origin, floors);
                if (hit != null)
                    hits.Add(hit);
            }

            if (hits.Count == 0)
            {
                return new WallPlan
                {
                    WallId = id,
                    TypeName = wall.WallType?.Name,
                    LevelName = baseLevel.Name,
                    Status = "no_soffit",
                    Reason = "No linked structural floor above this wall. Check XY overlap with the ST slab.",
                    CurrentHeightMm = RoundMm(FtToMm(currentHeight)),
                    BaseOffsetMm = RoundMm(FtToMm(baseOffset)),
                };
            }

            var minZ = hits.Min(h => h.SoffitZ);
            var maxZ = hits.Max(h => h.SoffitZ);
            var stepped = (maxZ - minZ) > stepTolFeet;
            var newHeight = minZ - wallBaseZ;
            if (newHeight < MmToFt(100) || newHeight > MmToFt(20000))
            {
                return new WallPlan
                {
                    WallId = id,
                    TypeName = wall.WallType?.Name,
                    LevelName = baseLevel.Name,
                    Status = "implausible_height",
                    Reason = $"Computed height {RoundMm(FtToMm(newHeight))} mm is outside 100–20000 mm.",
                    SoffitZ = minZ,
                    CurrentHeightMm = RoundMm(FtToMm(currentHeight)),
                };
            }

            var best = hits.OrderBy(h => h.SoffitZ).First();
            return new WallPlan
            {
                WallId = id,
                TypeName = wall.WallType?.Name,
                LevelName = baseLevel.Name,
                Status = stepped ? "stepped" : "ok",
                Reason = stepped
                    ? $"Soffit steps {RoundMm(FtToMm(maxZ - minZ))} mm along the wall. Applied the lowest soffit so the wall does not penetrate the lower slab."
                    : null,
                SoffitZ = minZ,
                SoffitMaxZ = maxZ,
                NewHeightFeet = newHeight,
                CurrentHeightFeet = currentHeight,
                BaseOffsetMm = RoundMm(FtToMm(baseOffset)),
                CurrentHeightMm = RoundMm(FtToMm(currentHeight)),
                NewHeightMm = RoundMm(FtToMm(newHeight)),
                SoffitZMm = RoundMm(FtToMm(minZ)),
                DeltaMm = RoundMm(FtToMm(newHeight - currentHeight)),
                FloorId = best.FloorId,
                FloorName = best.FloorName,
                LinkName = best.LinkName,
                Structural = best.Structural,
                SampleCount = hits.Count,
            };
        }

        private static SoffitHit FindSoffit(XYZ originHost, List<CachedFloor> floors)
        {
            SoffitHit best = null;
            foreach (var floor in floors)
            {
                if (!XyOverlaps(floor, originHost))
                    continue;

                var originLink = floor.Inverse.OfPoint(originHost);
                var dirLink = floor.Inverse.OfVector(XYZ.BasisZ);
                if (dirLink.GetLength() < 1e-9)
                    continue;
                dirLink = dirLink.Normalize();

                Line ray;
                try
                {
                    ray = Line.CreateBound(originLink, originLink + dirLink * RayLengthFeet);
                }
                catch
                {
                    continue;
                }

                foreach (var solid in floor.Solids)
                {
                    foreach (Face face in solid.Faces)
                    {
                        IntersectionResultArray results;
                        SetComparisonResult sc;
                        try
                        {
                            sc = face.Intersect(ray, out results);
                        }
                        catch
                        {
                            continue;
                        }

                        if (sc != SetComparisonResult.Overlap || results == null)
                            continue;

                        foreach (IntersectionResult ir in results)
                        {
                            if (ir?.XYZPoint == null)
                                continue;
                            var hostPt = floor.Transform.OfPoint(ir.XYZPoint);
                            if (hostPt.Z <= originHost.Z + 1e-6)
                                continue;
                            if (best == null || hostPt.Z < best.SoffitZ)
                            {
                                best = new SoffitHit
                                {
                                    SoffitZ = hostPt.Z,
                                    FloorId = floor.FloorId,
                                    FloorName = floor.FloorName,
                                    LinkName = floor.LinkName,
                                    Structural = floor.Structural,
                                };
                            }
                        }
                    }
                }
            }

            return best;
        }

        private static bool XyOverlaps(CachedFloor floor, XYZ hostPoint)
        {
            const double pad = 1.0;
            return hostPoint.X >= floor.HostMin.X - pad &&
                   hostPoint.X <= floor.HostMax.X + pad &&
                   hostPoint.Y >= floor.HostMin.Y - pad &&
                   hostPoint.Y <= floor.HostMax.Y + pad;
        }

        private static string ApplyHeight(Wall wall, double newHeightFeet)
        {
            var heightParam = wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM);
            if (heightParam == null)
                return "Unconnected Height parameter is missing.";
            if (heightParam.IsReadOnly)
                return "Unconnected Height is read-only. Clear the top constraint first.";

            var topConstraint = wall.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE);
            if (topConstraint != null && !topConstraint.IsReadOnly)
            {
                var current = topConstraint.AsElementId();
                if (current != null && current != ElementId.InvalidElementId &&
                    !topConstraint.Set(ElementId.InvalidElementId))
                    return "Revit rejected clearing the wall's top constraint.";
            }

            if (!heightParam.Set(newHeightFeet))
                return "Revit rejected the requested unconnected height.";
            return null;
        }

        private static List<RevitLinkInstance> CollectStructuralLinks(Document doc, string filter)
        {
            var result = new List<RevitLinkInstance>();
            foreach (var link in new FilteredElementCollector(doc)
                         .OfClass(typeof(RevitLinkInstance))
                         .Cast<RevitLinkInstance>())
            {
                var ld = link.GetLinkDocument();
                if (ld == null) continue;
                var name = link.Name ?? string.Empty;
                if (!string.IsNullOrEmpty(filter) &&
                    name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                result.Add(link);
            }
            return result;
        }

        private static List<CachedFloor> BuildFloorCache(
            List<RevitLinkInstance> links,
            CancellationToken cancellationToken)
        {
            var options = new Options
            {
                ComputeReferences = false,
                IncludeNonVisibleObjects = true,
                DetailLevel = ViewDetailLevel.Medium,
            };

            var cache = new List<CachedFloor>();
            foreach (var link in links)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ld = link.GetLinkDocument();
                if (ld == null) continue;
                var transform = link.GetTotalTransform() ?? Transform.Identity;
                var inverse = transform.Inverse;

                foreach (var floor in new FilteredElementCollector(ld)
                             .OfClass(typeof(Floor))
                             .WhereElementIsNotElementType()
                             .Cast<Floor>())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var solids = GetSolids(floor.get_Geometry(options)).ToList();
                    if (solids.Count == 0) continue;

                    var hostMin = new XYZ(double.MaxValue, double.MaxValue, double.MaxValue);
                    var hostMax = new XYZ(double.MinValue, double.MinValue, double.MinValue);
                    var any = false;
                    var bb = floor.get_BoundingBox(null);
                    if (bb != null)
                    {
                        Expand(ref hostMin, ref hostMax, transform.OfPoint(bb.Min), ref any);
                        Expand(ref hostMin, ref hostMax, transform.OfPoint(bb.Max), ref any);
                    }
                    if (!any) continue;

                    var structural = false;
                    var sp = floor.get_Parameter(BuiltInParameter.FLOOR_PARAM_IS_STRUCTURAL);
                    if (sp != null && sp.AsInteger() == 1)
                        structural = true;

                    cache.Add(new CachedFloor
                    {
                        FloorId = floor.Id.GetValue(),
                        FloorName = floor.Name,
                        LinkName = link.Name,
                        Transform = transform,
                        Inverse = inverse,
                        Solids = solids,
                        HostMin = hostMin,
                        HostMax = hostMax,
                        Structural = structural,
                    });
                }
            }

            // Prefer structural slabs when both exist at the same XY.
            return cache
                .OrderByDescending(f => f.Structural)
                .ToList();
        }

        private static IEnumerable<Solid> GetSolids(GeometryElement geo)
        {
            if (geo == null) yield break;
            foreach (GeometryObject obj in geo)
            {
                switch (obj)
                {
                    case Solid solid when solid.Faces.Size > 0 && solid.Volume > 1e-6:
                        yield return solid;
                        break;
                    case GeometryInstance instance:
                        foreach (var inner in GetSolids(instance.GetInstanceGeometry()))
                            yield return inner;
                        break;
                }
            }
        }

        private static void Expand(ref XYZ min, ref XYZ max, XYZ p, ref bool any)
        {
            min = new XYZ(Math.Min(min.X, p.X), Math.Min(min.Y, p.Y), Math.Min(min.Z, p.Z));
            max = new XYZ(Math.Max(max.X, p.X), Math.Max(max.Y, p.Y), Math.Max(max.Z, p.Z));
            any = true;
        }

        private static List<long> ResolveWallIds(Dictionary<string, object> parameters)
        {
            if (parameters.TryGetValue("element_ids", out var idsObj) && idsObj != null)
                return ParseElementIds(idsObj);

            var selected = SelectionContext.Current;
            if (selected == null || selected.Length == 0)
                return new List<long>();
            return selected.Select(id => id.GetValue()).Where(v => v > 0).Distinct().ToList();
        }

        private static List<long> ParseElementIds(object idsObj)
        {
            var result = new List<long>();
            if (idsObj is IEnumerable<object> enumerable)
            {
                foreach (var item in enumerable)
                {
                    if (item == null || !long.TryParse(item.ToString(), out var id) || id <= 0)
                        throw new ArgumentException($"element_ids contains an invalid positive integer ID: '{item}'.");
                    result.Add(id);
                }
            }
            else if (idsObj is string str)
            {
                foreach (var part in str.Split(','))
                {
                    if (!long.TryParse(part.Trim(), out var id) || id <= 0)
                        throw new ArgumentException($"element_ids contains an invalid positive integer ID: '{part}'.");
                    result.Add(id);
                }
            }
            else if (long.TryParse(idsObj?.ToString(), out var singleId) && singleId > 0)
            {
                result.Add(singleId);
            }
            else
            {
                throw new ArgumentException("element_ids must contain positive integer element IDs.");
            }
            return result.Distinct().ToList();
        }

        private static Dictionary<string, object> ToResult(WallPlan plan, bool applied)
        {
            return new Dictionary<string, object>
            {
                ["id"] = plan.WallId,
                ["type_name"] = plan.TypeName,
                ["level"] = plan.LevelName,
                ["status"] = plan.Status,
                ["reason"] = plan.Reason,
                ["applied"] = applied,
                ["link_name"] = plan.LinkName,
                ["floor_id"] = plan.FloorId,
                ["floor_name"] = plan.FloorName,
                ["structural"] = plan.Structural,
                ["current_height_mm"] = plan.CurrentHeightMm,
                ["new_height_mm"] = plan.NewHeightMm,
                ["delta_mm"] = plan.DeltaMm,
                ["base_offset_mm"] = plan.BaseOffsetMm,
                ["soffit_z_mm"] = plan.SoffitZMm,
                ["soffit_z_ft"] = plan.SoffitZ,
                ["sample_count"] = plan.SampleCount,
            };
        }

        private static Dictionary<string, object> Skip(
            long id,
            string status,
            string reason,
            WallPlan plan = null)
        {
            var d = new Dictionary<string, object>
            {
                ["id"] = id,
                ["status"] = status,
                ["reason"] = reason,
            };
            if (plan != null)
            {
                d["type_name"] = plan.TypeName;
                d["level"] = plan.LevelName;
                d["current_height_mm"] = plan.CurrentHeightMm;
            }
            return d;
        }

        private static string GetString(Dictionary<string, object> p, string key)
        {
            if (!p.TryGetValue(key, out var v) || v == null) return null;
            var s = v.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(s))
                throw new ArgumentException($"{key} cannot be blank when supplied.");
            return s;
        }

        private static double MmToFt(double mm) => mm / 304.8;
        private static double FtToMm(double ft) => ft * 304.8;
        private static double RoundMm(double mm) => Math.Round(mm, 1);

        private sealed class WallPlan
        {
            public long WallId;
            public string TypeName;
            public string LevelName;
            public string Status;
            public string Reason;
            public double SoffitZ;
            public double SoffitMaxZ;
            public double NewHeightFeet;
            public double CurrentHeightFeet;
            public double BaseOffsetMm;
            public double CurrentHeightMm;
            public double NewHeightMm;
            public double SoffitZMm;
            public double DeltaMm;
            public long FloorId;
            public string FloorName;
            public string LinkName;
            public bool Structural;
            public int SampleCount;
        }

        private sealed class SoffitHit
        {
            public double SoffitZ;
            public long FloorId;
            public string FloorName;
            public string LinkName;
            public bool Structural;
        }

        private sealed class CachedFloor
        {
            public long FloorId;
            public string FloorName;
            public string LinkName;
            public Transform Transform;
            public Transform Inverse;
            public List<Solid> Solids;
            public XYZ HostMin;
            public XYZ HostMax;
            public bool Structural;
        }
    }
}
