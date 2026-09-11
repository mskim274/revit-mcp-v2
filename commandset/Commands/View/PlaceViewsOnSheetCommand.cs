using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using RevitMCP.CommandSet.Helpers;
using RevitMCP.CommandSet.Interfaces;

namespace RevitMCP.CommandSet.Commands.View
{
    /// <summary>Place existing non-template views on one existing sheet.</summary>
    public class PlaceViewsOnSheetCommand : IRevitCommand
    {
        public string Name => "place_views_on_sheet";
        public string Category => "View";

        private const int MaxPlacements = 50;
        private const double PositionToleranceFeet = 0.01;

        public Task<CommandResult> ExecuteAsync(
            Document doc,
            Dictionary<string, object> parameters,
            CancellationToken cancellationToken)
        {
            try
            {
                if (parameters == null || !parameters.TryGetValue("sheet_id", out var sheetIdValue))
                    return Task.FromResult(CommandResult.Fail(
                        "sheet_id is required.",
                        "Use revit_get_sheets and provide an existing sheet ID."));

                ViewSheet sheet;
                try
                {
                    sheet = doc.GetElement(ElementIdCompatibility.Create(sheetIdValue)) as ViewSheet;
                }
                catch (Exception ex)
                {
                    return Task.FromResult(CommandResult.Fail(
                        $"Invalid sheet_id: {ex.Message}",
                        "Use a positive sheet ElementId returned by revit_get_sheets."));
                }
                if (sheet == null)
                    return Task.FromResult(CommandResult.Fail(
                        $"Element '{sheetIdValue}' is not an existing sheet.",
                        "Use revit_get_sheets to find a ViewSheet ID; this tool does not create sheets."));

                if (!parameters.TryGetValue("placements", out var placementsValue) ||
                    !TryAsObjectList(placementsValue, out var placements) ||
                    placements.Count == 0)
                {
                    return Task.FromResult(CommandResult.Fail(
                        "placements must be a non-empty array.",
                        "Provide 1-50 objects with view_id and point [x,y]."));
                }
                if (placements.Count > MaxPlacements)
                    return Task.FromResult(CommandResult.Fail(
                        $"Too many viewport placements: {placements.Count} (max {MaxPlacements}).",
                        "Split the operation into batches of at most 50 views."));

                var inputUnit = "feet";
                if (parameters.TryGetValue("input_unit", out var unitValue))
                {
                    inputUnit = unitValue?.ToString()?.Trim().ToLowerInvariant();
                    if (inputUnit != "feet" && inputUnit != "mm")
                        return Task.FromResult(CommandResult.Fail(
                            $"Unsupported input_unit '{unitValue}'.",
                            "Use input_unit='feet' or input_unit='mm'."));
                }

                var results = new List<Dictionary<string, object>>();
                CreatedViewport firstCreated = null;
                var createdCount = 0;
                using (var tx = new Transaction(doc, $"MCP: Place {placements.Count} views on sheet"))
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
                            if (!placement.TryGetValue("view_id", out var viewIdValue))
                                throw new ArgumentException("view_id is required for each placement.");

                            var viewId = ElementIdCompatibility.Create(viewIdValue);
                            var view = doc.GetElement(viewId) as global::Autodesk.Revit.DB.View;
                            if (view == null || view.IsTemplate || view is ViewSheet)
                                throw new ArgumentException($"view_id '{viewIdValue}' is not a placeable non-template view.");
                            if (view is ViewSchedule)
                                throw new InvalidOperationException("Schedules require ScheduleSheetInstance and are not supported by this viewport tool.");
                            if (!Viewport.CanAddViewToSheet(doc, sheet.Id, view.Id))
                                throw new InvalidOperationException(
                                    $"View '{view.Name}' cannot be added to sheet '{sheet.SheetNumber}'; it may already be placed or be an unsupported view type.");

                            var point = ParsePoint(placement, inputUnit);
                            subTransaction = new SubTransaction(doc);
                            subTransaction.Start();
                            var viewport = Viewport.Create(doc, sheet.Id, view.Id, point);
                            if (viewport == null)
                                throw new InvalidOperationException("Revit returned no Viewport.");
                            cancellationToken.ThrowIfCancellationRequested();
                            var subStatus = subTransaction.Commit();
                            if (subStatus != TransactionStatus.Committed)
                                throw new InvalidOperationException($"Viewport subtransaction did not commit ({subStatus}).");

                            createdCount++;
                            row["ok"] = true;
                            row["view_id"] = view.Id.GetValue();
                            row["view_name"] = view.Name;
                            row["viewport_id"] = viewport.Id.GetValue();
                            row["point_feet"] = PointDictionary(point);
                            if (firstCreated == null)
                            {
                                firstCreated = new CreatedViewport
                                {
                                    ViewportId = viewport.Id.GetValue(),
                                    ViewId = view.Id.GetValue(),
                                    SheetId = sheet.Id.GetValue(),
                                    ExpectedPoint = point,
                                };
                            }
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
                    if (createdCount == 0)
                        tx.RollBack();
                    else
                        tx.CommitOrThrow();
                }

                return Task.FromResult(CommandResult.Ok(new Dictionary<string, object>
                {
                    ["sheet_id"] = sheet.Id.GetValue(),
                    ["sheet_number"] = sheet.SheetNumber,
                    ["sheet_name"] = sheet.Name,
                    ["requested"] = placements.Count,
                    ["created_count"] = createdCount,
                    ["failed_count"] = placements.Count - createdCount,
                    ["input_unit"] = inputUnit,
                    ["mutation_committed"] = createdCount > 0,
                    ["results"] = results,
                    ["verification"] = VerifyFirstViewport(doc, firstCreated, cancellationToken),
                }));
            }
            catch (OperationCanceledException)
            {
                return Task.FromResult(CommandResult.Fail(
                    "Viewport placement was cancelled; the transaction was rolled back.",
                    "Retry with fewer placements."));
            }
            catch (Exception ex)
            {
                return Task.FromResult(CommandResult.Fail(
                    $"Failed to place views on sheet: {ex.Message}",
                    "Use revit_get_sheets and revit_get_views to verify existing IDs; schedules are not supported by this tool."));
            }
        }

        private static XYZ ParsePoint(Dictionary<string, object> placement, string inputUnit)
        {
            if (!placement.TryGetValue("point", out var pointValue) ||
                !TryAsObjectList(pointValue, out var coordinates) ||
                coordinates.Count != 2)
                throw new ArgumentException("point must contain exactly [x, y].");

            if (!RawParameterValidation.TryConvertFiniteParameterDouble(coordinates[0], out var x) ||
                !RawParameterValidation.TryConvertFiniteParameterDouble(coordinates[1], out var y))
                throw new ArgumentException("point coordinates must be finite numbers.");
            if (inputUnit == "mm")
            {
                x /= 304.8;
                y /= 304.8;
            }
            return new XYZ(x, y, 0);
        }

        private static Dictionary<string, object> VerifyFirstViewport(
            Document doc,
            CreatedViewport expected,
            CancellationToken cancellationToken)
        {
            if (expected == null)
                return new Dictionary<string, object>
                {
                    ["performed"] = false,
                    ["match"] = false,
                    ["issues"] = new List<string> { "No viewport was created." },
                };

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var viewport = doc.GetElement(ElementIdCompatibility.Create(expected.ViewportId)) as Viewport;
                var actualPoint = viewport?.GetBoxCenter();
                var exists = viewport != null;
                var sheetMatch = exists && viewport.SheetId.GetValue() == expected.SheetId;
                var viewMatch = exists && viewport.ViewId.GetValue() == expected.ViewId;
                var locationMatch = actualPoint != null &&
                    actualPoint.DistanceTo(expected.ExpectedPoint) <= PositionToleranceFeet;
                var issues = new List<string>();
                if (!exists) issues.Add("The first viewport was not found after commit.");
                if (exists && !sheetMatch) issues.Add("The first viewport is on a different sheet.");
                if (exists && !viewMatch) issues.Add("The first viewport references a different view.");
                if (exists && !locationMatch) issues.Add("The first viewport center differs by more than 0.01 ft.");

                return new Dictionary<string, object>
                {
                    ["performed"] = true,
                    ["match"] = exists && sheetMatch && viewMatch && locationMatch,
                    ["viewport_id"] = expected.ViewportId,
                    ["exists"] = exists,
                    ["sheet_match"] = sheetMatch,
                    ["view_match"] = viewMatch,
                    ["location_match"] = locationMatch,
                    ["actual_point_feet"] = actualPoint == null ? null : PointDictionary(actualPoint),
                    ["issues"] = issues,
                };
            }
            catch (Exception verificationError)
            {
                return new Dictionary<string, object>
                {
                    ["performed"] = false,
                    ["match"] = false,
                    ["error"] = verificationError.Message,
                };
            }
        }

        private static Dictionary<string, double> PointDictionary(XYZ point)
        {
            return new Dictionary<string, double>
            {
                ["x"] = Math.Round(point.X, 6),
                ["y"] = Math.Round(point.Y, 6),
            };
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

        private sealed class CreatedViewport
        {
            public long ViewportId;
            public long ViewId;
            public long SheetId;
            public XYZ ExpectedPoint;
        }
    }
}
