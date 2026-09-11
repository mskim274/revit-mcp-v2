using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AutoCADMCP.CommandSet.Interfaces;

namespace AutoCADMCP.CommandSet.Commands
{
    /// <summary>
    /// Lists insertable block definitions or inserts one loaded definition in
    /// batches. The dispatcher owns the single transaction for the command.
    /// </summary>
    public class BlocksCommand : ICadCommand
    {
        public string Name => "blocks";
        public string Category => "Create";

        private const int MaxInsertions = 50;

        public Task<CommandResult> ExecuteAsync(
            Database db,
            Transaction tr,
            Dictionary<string, object> parameters,
            CancellationToken cancellationToken)
        {
            try
            {
                parameters ??= new Dictionary<string, object>();
                var op = GetOptionalString(parameters, "op", "list")
                    .ToLowerInvariant();
                switch (op)
                {
                    case "list":
                        return Task.FromResult(ListBlocks(
                            db,
                            tr,
                            cancellationToken));
                    case "insert":
                        return Task.FromResult(InsertBlocks(
                            db,
                            tr,
                            parameters,
                            cancellationToken));
                    default:
                        return Task.FromResult(CommandResult.Fail(
                            $"Unsupported blocks op '{op}'.",
                            "Use op='list' or op='insert'."));
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return Task.FromResult(CommandResult.Fail(
                    $"blocks failed: {ex.Message}",
                    "Use op='list' to discover an already-loaded exact block name, then retry a bounded insert batch."));
            }
        }

        private static CommandResult ListBlocks(
            Database db,
            Transaction tr,
            CancellationToken cancellationToken)
        {
            var blockTable = (BlockTable)tr.GetObject(
                db.BlockTableId,
                OpenMode.ForRead);
            var definitions = new Dictionary<ObjectId, BlockSummary>();
            foreach (ObjectId definitionId in blockTable)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var definition = tr.GetObject(
                    definitionId,
                    OpenMode.ForRead) as BlockTableRecord;
                if (!IsInsertableDefinition(definition))
                    continue;
                definitions[definitionId] = new BlockSummary
                {
                    Id = definitionId,
                    Name = definition.Name,
                    IsDynamic = definition.IsDynamicBlock,
                };
            }

            // Count only top-level references in model space and paper-space
            // layouts. Nested references inside definitions are not counted.
            foreach (ObjectId recordId in blockTable)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var record = tr.GetObject(
                    recordId,
                    OpenMode.ForRead) as BlockTableRecord;
                if (record == null || !record.IsLayout)
                    continue;

                foreach (ObjectId entityId in record)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var reference = tr.GetObject(
                        entityId,
                        OpenMode.ForRead) as BlockReference;
                    if (reference == null || reference.IsErased)
                        continue;

                    var definitionId = reference.BlockTableRecord;
                    if (reference.IsDynamicBlock)
                        definitionId = reference.DynamicBlockTableRecord;
                    if (definitions.TryGetValue(definitionId, out var summary))
                        summary.ReferenceCount++;
                }
            }

            var blocks = definitions.Values
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .Select(item => new Dictionary<string, object>
                {
                    ["name"] = item.Name,
                    ["definition_handle"] = FormatHandle(item.Id.Handle),
                    ["reference_count"] = item.ReferenceCount,
                    ["is_dynamic"] = item.IsDynamic,
                })
                .ToList();

            return CommandResult.Ok(new Dictionary<string, object>
            {
                ["op"] = "list",
                ["definition_count"] = blocks.Count,
                ["blocks"] = blocks,
                ["count_scope"] = "Top-level references in model space and all paper-space layouts; nested references are excluded.",
                ["note"] = blocks.Count == 0
                    ? "No insertable, non-xref block definitions are loaded in this drawing."
                    : "Use the exact name with op='insert'.",
            });
        }

        private static CommandResult InsertBlocks(
            Database db,
            Transaction tr,
            Dictionary<string, object> parameters,
            CancellationToken cancellationToken)
        {
            var blockName = RequireNonBlankString(parameters, "block_name");
            if (!parameters.TryGetValue("insertions", out var rawInsertions) ||
                !(rawInsertions is List<object> insertions) ||
                insertions.Count < 1 ||
                insertions.Count > MaxInsertions)
            {
                return CommandResult.Fail(
                    $"'insertions' must contain 1 to {MaxInsertions} items.",
                    "Provide each insertion as an object with point [x,y] or [x,y,z] and optional positive scale/rotation_deg.");
            }

            var blockTable = (BlockTable)tr.GetObject(
                db.BlockTableId,
                OpenMode.ForRead);
            var definition = ResolveDefinition(
                blockTable,
                tr,
                blockName,
                cancellationToken);
            if (definition == null)
            {
                return CommandResult.Fail(
                    $"No insertable loaded block definition exactly matches '{blockName}'.",
                    "Run cad_blocks(op='list') and use an exact returned name; this tool does not load DWG block definitions.");
            }

            var currentSpace = tr.GetObject(
                db.CurrentSpaceId,
                OpenMode.ForWrite) as BlockTableRecord;
            if (currentSpace == null || !currentSpace.IsLayout)
            {
                return CommandResult.Fail(
                    "The current database space is not an editable model/paper layout.",
                    "Activate model space or a paper-space layout and retry.");
            }

            var results = new List<Dictionary<string, object>>();
            var insertedHandles = new List<string>();
            Dictionary<string, object> firstExpected = null;

            for (var index = 0; index < insertions.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = insertions[index] as Dictionary<string, object>;
                BlockReference reference = null;
                try
                {
                    if (item == null)
                        throw new ArgumentException("Each insertion must be an object.");
                    var point = RequirePoint(item, "point");
                    var scale = GetOptionalPositiveDouble(item, "scale", 1.0);
                    var rotationDegrees = GetOptionalFiniteDouble(
                        item,
                        "rotation_deg",
                        0.0);
                    var rotationRadians = DegreesToRadians(rotationDegrees);

                    reference = new BlockReference(point, definition.ObjectId);
                    reference.SetDatabaseDefaults(db);
                    reference.ScaleFactors = new Scale3d(scale);
                    reference.Rotation = rotationRadians;
                    currentSpace.AppendEntity(reference);
                    tr.AddNewlyCreatedDBObject(reference, true);
                    var attributeCount = AddDefaultAttributes(
                        definition,
                        reference,
                        tr,
                        cancellationToken);

                    var handle = FormatHandle(reference.Handle);
                    insertedHandles.Add(handle);
                    results.Add(new Dictionary<string, object>
                    {
                        ["index"] = index,
                        ["status"] = "inserted",
                        ["handle"] = handle,
                        ["block_name"] = definition.Name,
                        ["point"] = PointArray(point),
                        ["scale"] = scale,
                        ["rotation_deg"] = rotationDegrees,
                        ["default_attribute_count"] = attributeCount,
                    });

                    if (firstExpected == null)
                    {
                        firstExpected = new Dictionary<string, object>
                        {
                            ["handle"] = handle,
                            ["block_name"] = definition.Name,
                            ["point"] = new List<object>
                            {
                                point.X,
                                point.Y,
                                point.Z,
                            },
                            ["scale"] = scale,
                            ["rotation_radians"] = rotationRadians,
                        };
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception itemError)
                {
                    if (reference != null)
                    {
                        try
                        {
                            if (reference.ObjectId.IsNull)
                                reference.Dispose();
                            else if (!reference.IsErased)
                                reference.Erase(true);
                        }
                        catch (Exception cleanupError)
                        {
                            throw new InvalidOperationException(
                                $"Insertion {index} failed ('{itemError.Message}') and its partial BlockReference could not be cleaned up: {cleanupError.Message}",
                                cleanupError);
                        }
                    }

                    results.Add(new Dictionary<string, object>
                    {
                        ["index"] = index,
                        ["status"] = "failed",
                        ["reason"] = itemError.Message,
                        ["suggestion"] = "Use finite WCS coordinates, a positive uniform scale, and a finite rotation in degrees.",
                    });
                }
            }

            var insertedCount = insertedHandles.Count;
            return CommandResult.Ok(new Dictionary<string, object>
            {
                ["op"] = "insert",
                ["block_name"] = definition.Name,
                ["space"] = DescribeSpace(currentSpace, tr),
                ["requested_count"] = insertions.Count,
                ["inserted_count"] = insertedCount,
                ["failed_count"] = insertions.Count - insertedCount,
                ["inserted_handles"] = insertedHandles,
                ["results"] = results,
                ["verification_sample"] = firstExpected,
                ["transaction"] = "pending dispatcher commit",
                ["verification"] = new Dictionary<string, object>
                {
                    ["performed"] = false,
                    ["phase"] = "pre_commit",
                    ["provisional"] = true,
                    ["commit_verified"] = false,
                    ["sample_handle"] = firstExpected?["handle"],
                    ["issues"] = new[]
                    {
                        insertedCount > 0
                            ? "Final verification is pending transaction commit."
                            : "No valid block references were inserted; the transaction has no drawing changes."
                    },
                },
            });
        }

        private static BlockTableRecord ResolveDefinition(
            BlockTable blockTable,
            Transaction tr,
            string requestedName,
            CancellationToken cancellationToken)
        {
            BlockTableRecord match = null;
            foreach (ObjectId definitionId in blockTable)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var candidate = tr.GetObject(
                    definitionId,
                    OpenMode.ForRead) as BlockTableRecord;
                if (!IsInsertableDefinition(candidate) ||
                    !string.Equals(
                        candidate.Name,
                        requestedName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (match != null)
                    throw new InvalidOperationException(
                        $"Block name '{requestedName}' is ambiguous.");
                match = candidate;
            }
            return match;
        }

        private static bool IsInsertableDefinition(BlockTableRecord definition)
            => definition != null &&
               !definition.IsLayout &&
               !definition.IsAnonymous &&
               !definition.IsFromExternalReference &&
               !definition.IsFromOverlayReference &&
               !definition.IsDependent;

        private static int AddDefaultAttributes(
            BlockTableRecord definition,
            BlockReference reference,
            Transaction tr,
            CancellationToken cancellationToken)
        {
            var count = 0;
            foreach (ObjectId entityId in definition)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributeDefinition = tr.GetObject(
                    entityId,
                    OpenMode.ForRead) as AttributeDefinition;
                if (attributeDefinition == null || attributeDefinition.Constant)
                    continue;

                var attribute = new AttributeReference();
                try
                {
                    attribute.SetAttributeFromBlock(
                        attributeDefinition,
                        reference.BlockTransform);
                    attribute.TextString = attributeDefinition.TextString;
                    reference.AttributeCollection.AppendAttribute(attribute);
                    tr.AddNewlyCreatedDBObject(attribute, true);
                    count++;
                }
                catch
                {
                    if (attribute.ObjectId.IsNull)
                        attribute.Dispose();
                    throw;
                }
            }
            return count;
        }

        private static Point3d RequirePoint(
            Dictionary<string, object> item,
            string key)
        {
            if (!item.TryGetValue(key, out var raw) ||
                !(raw is List<object> values) ||
                values.Count < 2 ||
                values.Count > 3)
            {
                throw new ArgumentException(
                    $"'{key}' must contain exactly 2 or 3 finite numbers.");
            }
            return new Point3d(
                ToFiniteDouble(values[0], $"{key}[0]"),
                ToFiniteDouble(values[1], $"{key}[1]"),
                values.Count == 3
                    ? ToFiniteDouble(values[2], $"{key}[2]")
                    : 0.0);
        }

        private static double GetOptionalPositiveDouble(
            Dictionary<string, object> item,
            string key,
            double defaultValue)
        {
            var value = GetOptionalFiniteDouble(item, key, defaultValue);
            if (value <= 0)
                throw new ArgumentException($"'{key}' must be greater than zero.");
            return value;
        }

        private static double GetOptionalFiniteDouble(
            Dictionary<string, object> item,
            string key,
            double defaultValue)
        {
            if (!item.TryGetValue(key, out var raw) || raw == null)
                return defaultValue;
            return ToFiniteDouble(raw, key);
        }

        private static double ToFiniteDouble(object raw, string label)
        {
            double value;
            switch (raw)
            {
                case double doubleValue:
                    value = doubleValue;
                    break;
                case float floatValue:
                    value = floatValue;
                    break;
                case long longValue:
                    value = longValue;
                    break;
                case int intValue:
                    value = intValue;
                    break;
                case decimal decimalValue:
                    value = (double)decimalValue;
                    break;
                default:
                    throw new ArgumentException($"'{label}' must be a finite number.");
            }
            if (double.IsNaN(value) || double.IsInfinity(value))
                throw new ArgumentException($"'{label}' must be a finite number.");
            return value;
        }

        private static string RequireNonBlankString(
            Dictionary<string, object> parameters,
            string key)
        {
            if (!parameters.TryGetValue(key, out var raw) ||
                !(raw is string supplied) ||
                string.IsNullOrWhiteSpace(supplied))
            {
                throw new ArgumentException($"'{key}' must be a non-empty string.");
            }
            return supplied.Trim();
        }

        private static string GetOptionalString(
            Dictionary<string, object> parameters,
            string key,
            string defaultValue)
        {
            if (!parameters.TryGetValue(key, out var raw) || raw == null)
                return defaultValue;
            if (!(raw is string supplied) || string.IsNullOrWhiteSpace(supplied))
                throw new ArgumentException($"'{key}' must be a non-empty string.");
            return supplied.Trim();
        }

        private static string DescribeSpace(
            BlockTableRecord space,
            Transaction tr)
        {
            if (!space.IsLayout || space.LayoutId.IsNull)
                return space.Name;
            var layout = tr.GetObject(space.LayoutId, OpenMode.ForRead) as Layout;
            return layout?.LayoutName ?? space.Name;
        }

        private static double[] PointArray(Point3d point)
            => new[] { point.X, point.Y, point.Z };

        private static string FormatHandle(Handle handle)
            => handle.Value.ToString(CultureInfo.InvariantCulture);

        private static double DegreesToRadians(double degrees)
            => degrees * Math.PI / 180.0;

        private sealed class BlockSummary
        {
            public ObjectId Id;
            public string Name;
            public int ReferenceCount;
            public bool IsDynamic;
        }
    }
}
