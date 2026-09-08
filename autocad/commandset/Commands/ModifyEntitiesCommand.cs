using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AutoCADMCP.CommandSet.Interfaces;

namespace AutoCADMCP.CommandSet.Commands
{
    /// <summary>
    /// Batch move/copy/erase/rotate. The dispatcher supplies and commits the
    /// one transaction; item-specific API failures are reported and skipped.
    /// </summary>
    public class ModifyEntitiesCommand : ICadCommand
    {
        public string Name => "modify_entities";
        public string Category => "Modify";

        private const int MaxHandles = 500;

        private sealed class BatchIntegrityException : Exception
        {
            public BatchIntegrityException(string message, Exception inner)
                : base(message, inner)
            {
            }
        }

        public Task<CommandResult> ExecuteAsync(
            Database db,
            Transaction tr,
            Dictionary<string, object> parameters,
            CancellationToken cancellationToken)
        {
            try
            {
                var operation = GetString(parameters, "op")?
                    .Trim()
                    .ToLowerInvariant();
                if (operation != "move" &&
                    operation != "copy" &&
                    operation != "erase" &&
                    operation != "rotate")
                {
                    return Fail(
                        "'op' must be move, copy, erase, or rotate.",
                        "Choose exactly one supported batch operation.");
                }

                if (!parameters.TryGetValue("handles", out var rawHandles) ||
                    !(rawHandles is List<object> handleItems) ||
                    handleItems.Count < 1 ||
                    handleItems.Count > MaxHandles)
                {
                    return Fail(
                        $"'handles' must contain 1 to {MaxHandles} strings.",
                        "Use the id strings returned by cad_query_entities and split larger jobs into batches of 500.");
                }

                var displacement = new Vector3d(0, 0, 0);
                var origin = Point3d.Origin;
                var angleRadians = 0.0;
                if (operation == "move" || operation == "copy")
                {
                    displacement = RequireVector(parameters, "vector");
                }
                else if (operation == "rotate")
                {
                    origin = RequirePoint(parameters, "origin");
                    angleRadians = RequireFiniteDouble(
                        parameters,
                        "angle_deg") * Math.PI / 180.0;
                }

                var results = new List<Dictionary<string, object>>();
                var mutatedHandles = new List<string>();
                var createdHandles = new List<string>();
                var seen = new HashSet<long>();
                string sampleHandle = null;

                for (var index = 0; index < handleItems.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var supplied = handleItems[index] as string;
                    if (!TryParseQueryHandle(supplied, out var handleValue))
                    {
                        results.Add(ItemSkipped(
                            index,
                            supplied,
                            "Handle must be a positive string in the decimal format returned by cad_query_entities (hex with A-F or 0x prefix is also accepted)."));
                        continue;
                    }
                    if (!seen.Add(handleValue))
                    {
                        results.Add(ItemSkipped(
                            index,
                            supplied,
                            "Duplicate handle in the same request; it was already processed once."));
                        continue;
                    }

                    try
                    {
                        var objectId = db.GetObjectId(
                            false,
                            new Handle(handleValue),
                            0);
                        if (objectId.IsNull)
                            throw new InvalidOperationException(
                                "Handle does not resolve to an object in this drawing.");

                        if (operation == "copy")
                        {
                            var source = tr.GetObject(
                                objectId,
                                OpenMode.ForRead,
                                false) as Entity;
                            if (source == null)
                                throw new InvalidOperationException(
                                    "Handle does not refer to an entity.");

                            var owner = tr.GetObject(
                                source.OwnerId,
                                OpenMode.ForWrite) as BlockTableRecord;
                            if (owner == null)
                                throw new InvalidOperationException(
                                    "The source entity owner is not a writable block table record.");

                            Entity clone = null;
                            try
                            {
                                clone = source.Clone() as Entity;
                                if (clone == null)
                                    throw new InvalidOperationException(
                                        "AutoCAD could not clone this entity type.");
                                clone.TransformBy(
                                    Matrix3d.Displacement(displacement));
                                owner.AppendEntity(clone);
                                tr.AddNewlyCreatedDBObject(clone, true);

                                var newHandle = FormatHandle(clone.Handle);
                                createdHandles.Add(newHandle);
                                mutatedHandles.Add(newHandle);
                                sampleHandle ??= newHandle;
                                results.Add(ItemSuccess(
                                    index,
                                    supplied,
                                    operation,
                                    newHandle));
                            }
                            catch
                            {
                                if (clone != null)
                                {
                                    try
                                    {
                                        if (clone.ObjectId.IsNull)
                                            clone.Dispose();
                                        else if (!clone.IsErased)
                                            clone.Erase(true);
                                    }
                                    catch (Exception cleanupError)
                                    {
                                        throw new BatchIntegrityException(
                                            $"Copy of handle '{supplied}' failed and its partial database object could not be cleaned up: {cleanupError.Message}",
                                            cleanupError);
                                    }
                                }
                                throw;
                            }
                            continue;
                        }

                        var entity = tr.GetObject(
                            objectId,
                            OpenMode.ForWrite,
                            false) as Entity;
                        if (entity == null)
                            throw new InvalidOperationException(
                                "Handle does not refer to an entity.");

                        // Capture the handle before erase so reporting never
                        // needs to inspect an already-erased wrapper.
                        var canonicalHandle = FormatHandle(entity.Handle);

                        switch (operation)
                        {
                            case "move":
                                entity.TransformBy(
                                    Matrix3d.Displacement(displacement));
                                break;
                            case "rotate":
                                entity.TransformBy(
                                    Matrix3d.Rotation(
                                        angleRadians,
                                        Vector3d.ZAxis,
                                        origin));
                                break;
                            case "erase":
                                entity.Erase(true);
                                break;
                        }

                        mutatedHandles.Add(canonicalHandle);
                        sampleHandle ??= canonicalHandle;
                        results.Add(ItemSuccess(
                            index,
                            supplied,
                            operation,
                            operation == "erase" ? null : canonicalHandle));
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception itemError)
                    {
                        if (itemError is BatchIntegrityException)
                            throw;
                        results.Add(ItemSkipped(
                            index,
                            supplied,
                            itemError.Message));
                    }
                }

                var mutatedCount = mutatedHandles.Count;
                var verification = new Dictionary<string, object>
                {
                    ["performed"] = false,
                    ["phase"] = "pre_commit",
                    ["provisional"] = true,
                    ["commit_verified"] = false,
                    ["mutated_count"] = mutatedCount,
                    ["sample_handle"] = sampleHandle,
                    ["sample_expected_exists"] = operation != "erase",
                    ["issues"] = new[]
                    {
                        mutatedCount > 0
                            ? "Final verification is pending transaction commit."
                            : "No valid entities were mutated; the transaction has no model changes."
                    },
                };

                return Task.FromResult(CommandResult.Ok(
                    new Dictionary<string, object>
                    {
                        ["op"] = operation,
                        ["requested_count"] = handleItems.Count,
                        ["mutated_count"] = mutatedCount,
                        ["skipped_count"] = handleItems.Count - mutatedCount,
                        ["mutated_handles"] = mutatedHandles,
                        ["created_handles"] = createdHandles,
                        ["results"] = results,
                        ["verification_sample_handle"] = sampleHandle,
                        ["verification_sample_expected_exists"] =
                            operation != "erase",
                        ["verification"] = verification,
                    }));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ArgumentException inputError)
            {
                return Fail(
                    inputError.Message,
                    "move/copy require vector [dx,dy] or [dx,dy,dz]; rotate requires origin plus finite angle_deg.");
            }
            catch (Exception ex)
            {
                return Fail(
                    $"modify_entities failed: {ex.Message}",
                    "Check that the active drawing and handles are still current, then retry with a new idempotency_key unless the prior outcome was uncertain.");
            }
        }

        private static Point3d RequirePoint(
            Dictionary<string, object> parameters,
            string key)
        {
            var values = RequireCoordinateArray(parameters, key);
            return new Point3d(
                values[0],
                values[1],
                values.Count == 3 ? values[2] : 0.0);
        }

        private static Vector3d RequireVector(
            Dictionary<string, object> parameters,
            string key)
        {
            var values = RequireCoordinateArray(parameters, key);
            return new Vector3d(
                values[0],
                values[1],
                values.Count == 3 ? values[2] : 0.0);
        }

        private static List<double> RequireCoordinateArray(
            Dictionary<string, object> parameters,
            string key)
        {
            if (!parameters.TryGetValue(key, out var raw) ||
                !(raw is List<object> items) ||
                items.Count < 2 ||
                items.Count > 3)
            {
                throw new ArgumentException(
                    $"'{key}' must contain exactly 2 or 3 finite numbers.");
            }

            var result = new List<double>();
            foreach (var item in items)
                result.Add(ToFiniteDouble(item, key));
            return result;
        }

        private static double RequireFiniteDouble(
            Dictionary<string, object> parameters,
            string key)
        {
            if (!parameters.TryGetValue(key, out var raw))
                throw new ArgumentException(
                    $"Missing required number '{key}'.");
            return ToFiniteDouble(raw, key);
        }

        private static double ToFiniteDouble(object raw, string label)
        {
            double value;
            switch (raw)
            {
                case double d:
                    value = d;
                    break;
                case float f:
                    value = f;
                    break;
                case long l:
                    value = l;
                    break;
                case int i:
                    value = i;
                    break;
                case decimal m:
                    value = (double)m;
                    break;
                default:
                    throw new ArgumentException(
                        $"'{label}' must contain finite numbers.");
            }

            if (double.IsNaN(value) || double.IsInfinity(value))
                throw new ArgumentException(
                    $"'{label}' must contain finite numbers.");
            return value;
        }

        private static bool TryParseQueryHandle(
            string supplied,
            out long value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(supplied))
                return false;
            var text = supplied.Trim();

            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                return long.TryParse(
                    text.Substring(2),
                    NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture,
                    out value) && value > 0;
            }

            var containsHexLetter = false;
            foreach (var character in text)
            {
                if ((character >= 'A' && character <= 'F') ||
                    (character >= 'a' && character <= 'f'))
                {
                    containsHexLetter = true;
                    break;
                }
            }

            return long.TryParse(
                text,
                containsHexLetter
                    ? NumberStyles.AllowHexSpecifier
                    : NumberStyles.None,
                CultureInfo.InvariantCulture,
                out value) && value > 0;
        }

        private static string GetString(
            Dictionary<string, object> parameters,
            string key)
            => parameters.TryGetValue(key, out var raw) &&
               raw is string value
                ? value
                : null;

        private static string FormatHandle(Handle handle)
            => handle.Value.ToString(CultureInfo.InvariantCulture);

        private static Dictionary<string, object> ItemSuccess(
            int index,
            string suppliedHandle,
            string operation,
            string resultHandle)
        {
            var data = new Dictionary<string, object>
            {
                ["index"] = index,
                ["input_handle"] = suppliedHandle,
                ["status"] = "mutated",
                ["op"] = operation,
            };
            if (resultHandle != null)
                data[operation == "copy" ? "new_handle" : "handle"] =
                    resultHandle;
            return data;
        }

        private static Dictionary<string, object> ItemSkipped(
            int index,
            string suppliedHandle,
            string reason)
            => new Dictionary<string, object>
            {
                ["index"] = index,
                ["input_handle"] = suppliedHandle ?? "",
                ["status"] = "skipped",
                ["reason"] = reason,
                ["suggestion"] =
                    "Refresh handles with cad_query_entities and ensure the entity is writable and not on a locked layer.",
            };

        private static Task<CommandResult> Fail(
            string message,
            string suggestion)
            => Task.FromResult(CommandResult.Fail(message, suggestion));
    }
}
