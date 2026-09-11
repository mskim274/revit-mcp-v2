using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using RevitMCP.CommandSet.Helpers;
using RevitMCP.CommandSet.Interfaces;

namespace RevitMCP.CommandSet.Commands.Create
{
    /// <summary>
    /// Place already-loaded, unhosted, one-level-based family symbols.
    /// Family and type names use case-insensitive exact matching.
    /// </summary>
    public class PlaceFamilyCommand : IRevitCommand
    {
        public string Name => "place_family";
        public string Category => "Create";

        private const int MaxPlacements = 50;
        private const double PositionToleranceFeet = 0.01;

        public Task<CommandResult> ExecuteAsync(
            Document doc,
            Dictionary<string, object> parameters,
            CancellationToken cancellationToken)
        {
            try
            {
                if (parameters == null ||
                    !parameters.TryGetValue("placements", out var placementsValue) ||
                    !TryAsObjectList(placementsValue, out var placements) ||
                    placements.Count == 0)
                {
                    return Task.FromResult(CommandResult.Fail(
                        "placements must be a non-empty array.",
                        "Provide 1-50 placement objects with a type selector and point [x,y,z]."));
                }

                if (placements.Count > MaxPlacements)
                {
                    return Task.FromResult(CommandResult.Fail(
                        $"Too many placements: {placements.Count} (max {MaxPlacements}).",
                        "Split the request into batches of at most 50 placements."));
                }

                var inputUnit = "feet";
                if (parameters.TryGetValue("input_unit", out var unitValue))
                {
                    inputUnit = unitValue?.ToString()?.Trim().ToLowerInvariant();
                    if (inputUnit != "feet" && inputUnit != "mm")
                    {
                        return Task.FromResult(CommandResult.Fail(
                            $"Unsupported input_unit '{unitValue}'.",
                            "Use input_unit='feet' for Revit internal coordinates or input_unit='mm' for millimetres."));
                    }
                }

                var results = new List<Dictionary<string, object>>();
                var created = new List<CreatedPlacement>();

                using (var tx = new Transaction(doc, $"MCP: Place {placements.Count} family instances"))
                {
                    tx.Start();
                    for (var index = 0; index < placements.Count; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var row = new Dictionary<string, object> { ["index"] = index };
                        SubTransaction subTransaction = null;
                        try
                        {
                            if (!(placements[index] is Dictionary<string, object> placement))
                                throw new ArgumentException("Each placements item must be an object.");

                            var symbol = ResolveSymbol(doc, placement);
                            var point = ParsePoint(placement, inputUnit);
                            var level = ResolveLevel(doc, placement, point.Z);
                            var placementType = symbol.Family?.FamilyPlacementType ?? FamilyPlacementType.Invalid;
                            if (placementType != FamilyPlacementType.OneLevelBased)
                            {
                                throw new InvalidOperationException(
                                    $"Family '{symbol.Family?.Name}', type '{symbol.Name}' uses placement type " +
                                    $"'{placementType}', but place_family supports only unhosted OneLevelBased symbols.");
                            }

                            subTransaction = new SubTransaction(doc);
                            subTransaction.Start();
                            if (!symbol.IsActive)
                            {
                                symbol.Activate();
                                doc.Regenerate();
                            }

                            var instance = doc.Create.NewFamilyInstance(
                                point,
                                symbol,
                                level,
                                StructuralType.NonStructural);
                            if (instance == null)
                                throw new InvalidOperationException("Revit returned no FamilyInstance.");

                            cancellationToken.ThrowIfCancellationRequested();
                            var subStatus = subTransaction.Commit();
                            if (subStatus != TransactionStatus.Committed)
                                throw new InvalidOperationException($"Placement subtransaction did not commit ({subStatus}).");

                            row["ok"] = true;
                            row["element_id"] = instance.Id.GetValue();
                            row["family_name"] = symbol.Family?.Name ?? string.Empty;
                            row["type_name"] = symbol.Name;
                            row["type_id"] = symbol.Id.GetValue();
                            row["level_id"] = level.Id.GetValue();
                            row["level_name"] = level.Name;
                            row["point_feet"] = PointDictionary(point);
                            created.Add(new CreatedPlacement
                            {
                                ElementId = instance.Id.GetValue(),
                                TypeId = symbol.Id.GetValue(),
                                LevelId = level.Id.GetValue(),
                                ExpectedPoint = point,
                            });
                        }
                        catch (OperationCanceledException)
                        {
                            if (subTransaction != null && subTransaction.GetStatus() == TransactionStatus.Started)
                                subTransaction.RollBack();
                            throw;
                        }
                        catch (Exception itemError)
                        {
                            if (subTransaction != null && subTransaction.GetStatus() == TransactionStatus.Started)
                                subTransaction.RollBack();
                            row["ok"] = false;
                            row["reason"] = itemError.Message;
                        }
                        finally
                        {
                            subTransaction?.Dispose();
                        }

                        results.Add(row);
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    if (created.Count == 0)
                        tx.RollBack();
                    else
                        tx.CommitOrThrow();
                }

                var verification = VerifyFirstPlacement(doc, created, cancellationToken);
                return Task.FromResult(CommandResult.Ok(new Dictionary<string, object>
                {
                    ["requested"] = placements.Count,
                    ["created_count"] = created.Count,
                    ["failed_count"] = placements.Count - created.Count,
                    ["input_unit"] = inputUnit,
                    ["mutation_committed"] = created.Count > 0,
                    ["results"] = results,
                    ["verification"] = verification,
                }));
            }
            catch (OperationCanceledException)
            {
                return Task.FromResult(CommandResult.Fail(
                    "Family placement was cancelled; the transaction was rolled back.",
                    "Retry with a smaller placements batch."));
            }
            catch (Exception ex)
            {
                return Task.FromResult(CommandResult.Fail(
                    $"Failed to place family instances: {ex.Message}",
                    "Use revit_get_family_types to confirm an already-loaded exact family/type and choose an unhosted OneLevelBased symbol."));
            }
        }

        private static FamilySymbol ResolveSymbol(Document doc, Dictionary<string, object> placement)
        {
            var hasTypeId = placement.TryGetValue("type_id", out var typeIdValue) && typeIdValue != null;
            var familyName = GetNonBlankString(placement, "family_name");
            var typeName = GetNonBlankString(placement, "type_name");
            if (hasTypeId && (familyName != null || typeName != null))
                throw new ArgumentException("Choose type_id OR family_name + type_name, not both.");

            if (hasTypeId)
            {
                var symbol = doc.GetElement(ElementIdCompatibility.Create(typeIdValue)) as FamilySymbol;
                if (symbol == null)
                    throw new ArgumentException($"type_id '{typeIdValue}' is not an already-loaded FamilySymbol.");
                return symbol;
            }

            if (familyName == null || typeName == null)
                throw new ArgumentException("Provide type_id, or provide both family_name and type_name.");

            var matches = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .Where(symbol =>
                    string.Equals(symbol.Family?.Name, familyName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(symbol.Name, typeName, StringComparison.OrdinalIgnoreCase))
                .Take(2)
                .ToList();

            if (matches.Count == 0)
                throw new ArgumentException($"No loaded family type exactly matches family '{familyName}' and type '{typeName}'.");
            if (matches.Count > 1)
                throw new ArgumentException($"Family '{familyName}' and type '{typeName}' are ambiguous; use type_id.");
            return matches[0];
        }

        private static XYZ ParsePoint(Dictionary<string, object> placement, string inputUnit)
        {
            if (!placement.TryGetValue("point", out var pointValue) ||
                !TryAsObjectList(pointValue, out var coordinates) ||
                coordinates.Count != 3)
            {
                throw new ArgumentException("point must contain exactly [x, y, z].");
            }

            var values = new double[3];
            for (var i = 0; i < values.Length; i++)
            {
                if (!RawParameterValidation.TryConvertFiniteParameterDouble(coordinates[i], out values[i]))
                    throw new ArgumentException($"point[{i}] must be a finite number.");
                if (inputUnit == "mm")
                    values[i] /= 304.8;
            }
            return new XYZ(values[0], values[1], values[2]);
        }

        private static Level ResolveLevel(Document doc, Dictionary<string, object> placement, double pointZ)
        {
            var hasLevelId = placement.TryGetValue("level_id", out var levelIdValue) && levelIdValue != null;
            var levelName = GetNonBlankString(placement, "level_name");
            if (hasLevelId && levelName != null)
                throw new ArgumentException("Choose level_id or level_name, not both.");

            if (hasLevelId)
            {
                var byId = doc.GetElement(ElementIdCompatibility.Create(levelIdValue)) as Level;
                if (byId == null)
                    throw new ArgumentException($"level_id '{levelIdValue}' is not a Level.");
                return byId;
            }

            var levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .ToList();
            if (levelName != null)
            {
                var byName = levels.FirstOrDefault(level =>
                    string.Equals(level.Name, levelName, StringComparison.OrdinalIgnoreCase));
                if (byName == null)
                    throw new ArgumentException($"No level exactly matches '{levelName}'.");
                return byName;
            }

            var nearest = levels.OrderBy(level => Math.Abs(level.Elevation - pointZ)).FirstOrDefault();
            if (nearest == null)
                throw new InvalidOperationException("The document contains no levels.");
            return nearest;
        }

        private static Dictionary<string, object> VerifyFirstPlacement(
            Document doc,
            List<CreatedPlacement> created,
            CancellationToken cancellationToken)
        {
            var result = new Dictionary<string, object>
            {
                ["performed"] = created.Count > 0,
                ["match"] = created.Count > 0,
                ["issues"] = new List<string>(),
            };
            if (created.Count == 0)
                return result;

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var expected = created[0];
                var instance = doc.GetElement(ElementIdCompatibility.Create(expected.ElementId)) as FamilyInstance;
                var issues = new List<string>();
                var actualPoint = (instance?.Location as LocationPoint)?.Point;
                var exists = instance != null;
                var typeMatch = exists && instance.GetTypeId().GetValue() == expected.TypeId;
                var levelMatch = exists && instance.LevelId.GetValue() == expected.LevelId;
                var locationMatch = actualPoint != null &&
                    actualPoint.DistanceTo(expected.ExpectedPoint) <= PositionToleranceFeet;

                if (!exists) issues.Add("The first placed instance was not found after commit.");
                if (exists && !typeMatch) issues.Add("The first placed instance type does not match the request.");
                if (exists && !levelMatch) issues.Add("The first placed instance level does not match the request.");
                if (exists && !locationMatch) issues.Add("The first placed instance location differs by more than 0.01 ft.");

                result["element_id"] = expected.ElementId;
                result["exists"] = exists;
                result["type_match"] = typeMatch;
                result["level_match"] = levelMatch;
                result["location_match"] = locationMatch;
                result["actual_point_feet"] = actualPoint == null ? null : PointDictionary(actualPoint);
                result["match"] = exists && typeMatch && levelMatch && locationMatch;
                result["issues"] = issues;
            }
            catch (Exception verificationError)
            {
                result["performed"] = false;
                result["match"] = false;
                result["error"] = verificationError.Message;
            }
            return result;
        }

        private static Dictionary<string, double> PointDictionary(XYZ point)
        {
            return new Dictionary<string, double>
            {
                ["x"] = Math.Round(point.X, 6),
                ["y"] = Math.Round(point.Y, 6),
                ["z"] = Math.Round(point.Z, 6),
            };
        }

        private static string GetNonBlankString(Dictionary<string, object> values, string key)
        {
            if (!values.TryGetValue(key, out var raw) || raw == null)
                return null;
            var text = raw.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(text))
                throw new ArgumentException($"{key} cannot be blank when supplied.");
            return text;
        }

        private static bool TryAsObjectList(object value, out List<object> items)
        {
            items = new List<object>();
            if (value == null || value is string || !(value is IEnumerable enumerable))
                return false;
            foreach (var item in enumerable)
                items.Add(item);
            return true;
        }

        private sealed class CreatedPlacement
        {
            public long ElementId;
            public long TypeId;
            public long LevelId;
            public XYZ ExpectedPoint;
        }
    }
}
