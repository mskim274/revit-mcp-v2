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
    /// Creates a heterogeneous entity batch in the single transaction owned
    /// by AcadWebSocketServer. Item validation failures do not abort valid
    /// siblings; cancellation and command-level failures abort the batch.
    /// </summary>
    public class CreateEntitiesCommand : ICadCommand
    {
        public string Name => "create_entities";
        public string Category => "Create";

        private const int MaxEntities = 200;
        private const int MaxPolylineVertices = 10_000;

        public Task<CommandResult> ExecuteAsync(
            Database db,
            Transaction tr,
            Dictionary<string, object> parameters,
            CancellationToken cancellationToken)
        {
            try
            {
                if (!parameters.TryGetValue("entities", out var rawEntities) ||
                    !(rawEntities is List<object> entities) ||
                    entities.Count < 1 ||
                    entities.Count > MaxEntities)
                {
                    return Fail(
                        $"'entities' must be an array containing 1 to {MaxEntities} items.",
                        "Send one batch with at most 200 line, polyline, circle, arc, or text specifications.");
                }

                var layerTable = (LayerTable)tr.GetObject(
                    db.LayerTableId,
                    OpenMode.ForRead);
                var blockTable = (BlockTable)tr.GetObject(
                    db.BlockTableId,
                    OpenMode.ForRead);
                var modelSpace = (BlockTableRecord)tr.GetObject(
                    blockTable[BlockTableRecord.ModelSpace],
                    OpenMode.ForWrite);

                var results = new List<Dictionary<string, object>>();
                var createdHandles = new List<string>();

                for (var index = 0; index < entities.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var item = entities[index] as Dictionary<string, object>;
                    var type = GetString(item, "type")?.Trim().ToLowerInvariant();

                    if (item == null || string.IsNullOrWhiteSpace(type))
                    {
                        results.Add(ItemFailure(
                            index,
                            type ?? "(missing)",
                            "Each entity must be an object with a supported string 'type'.",
                            "Use type line, polyline, circle, arc, or text."));
                        continue;
                    }

                    Entity entity = null;
                    try
                    {
                        var layerId = ResolveLayer(
                            item,
                            layerTable,
                            tr,
                            db.Clayer,
                            out var layerName);
                        entity = BuildEntity(
                            type,
                            item,
                            db,
                            cancellationToken);
                        entity.SetDatabaseDefaults(db);
                        entity.LayerId = layerId;

                        modelSpace.AppendEntity(entity);
                        tr.AddNewlyCreatedDBObject(entity, true);

                        var handle = FormatHandle(entity.Handle);
                        createdHandles.Add(handle);
                        results.Add(new Dictionary<string, object>
                        {
                            ["index"] = index,
                            ["type"] = type,
                            ["status"] = "created",
                            ["handle"] = handle,
                            ["layer"] = layerName,
                        });
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception itemError)
                    {
                        var reason = itemError.Message;
                        if (entity != null)
                        {
                            try
                            {
                                if (entity.ObjectId.IsNull)
                                    entity.Dispose();
                                else if (!entity.IsErased)
                                    entity.Erase(true);
                            }
                            catch (Exception cleanupError)
                            {
                                throw new InvalidOperationException(
                                    $"Entity {index} failed ('{reason}') and its partial database object could not be cleaned up: {cleanupError.Message}",
                                    cleanupError);
                            }
                        }
                        results.Add(ItemFailure(
                            index,
                            type,
                            reason,
                            SuggestionFor(type)));
                    }
                }

                var createdCount = createdHandles.Count;
                var failedCount = entities.Count - createdCount;
                var verification = new Dictionary<string, object>
                {
                    ["performed"] = false,
                    ["phase"] = "pre_commit",
                    ["provisional"] = true,
                    ["commit_verified"] = false,
                    ["created_count"] = createdCount,
                    ["sample_handle"] = createdCount > 0
                        ? createdHandles[0]
                        : null,
                    ["issues"] = new[]
                    {
                        createdCount > 0
                            ? "Final verification is pending transaction commit."
                            : "No valid entities were created; the transaction has no model changes."
                    },
                };

                return Task.FromResult(CommandResult.Ok(
                    new Dictionary<string, object>
                    {
                        ["requested_count"] = entities.Count,
                        ["created_count"] = createdCount,
                        ["failed_count"] = failedCount,
                        ["created_handles"] = createdHandles,
                        ["results"] = results,
                        ["verification_sample_handle"] = createdCount > 0
                            ? createdHandles[0]
                            : null,
                        ["verification"] = verification,
                    }));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return Fail(
                    $"create_entities failed: {ex.Message}",
                    "Check the entity array shape and drawing state, then retry the whole batch with a new idempotency_key unless the prior outcome was uncertain.");
            }
        }

        private static Entity BuildEntity(
            string type,
            Dictionary<string, object> item,
            Database db,
            CancellationToken cancellationToken)
        {
            switch (type)
            {
                case "line":
                {
                    var start = RequirePoint(item, "start");
                    var end = RequirePoint(item, "end");
                    if (start.DistanceTo(end) < 1e-9)
                        throw new ArgumentException(
                            "Line start and end must be different points.");
                    return new Line(start, end);
                }
                case "polyline":
                    return BuildPolyline(item, cancellationToken);
                case "circle":
                {
                    var center = RequirePoint(item, "center");
                    var radius = RequirePositiveDouble(item, "radius");
                    return new Circle(center, Vector3d.ZAxis, radius);
                }
                case "arc":
                {
                    var center = RequirePoint(item, "center");
                    var radius = RequirePositiveDouble(item, "radius");
                    var startDegrees = RequireFiniteDouble(item, "start_angle_deg");
                    var endDegrees = RequireFiniteDouble(item, "end_angle_deg");
                    var sweep = Math.Abs((endDegrees - startDegrees) % 360.0);
                    if (sweep < 1e-10)
                        throw new ArgumentException(
                            "Arc start_angle_deg and end_angle_deg must define a non-zero, non-360-degree sweep; use circle for a full circle.");
                    return new Arc(
                        center,
                        radius,
                        DegreesToRadians(startDegrees),
                        DegreesToRadians(endDegrees));
                }
                case "text":
                {
                    var position = RequirePoint(item, "position");
                    var contents = GetString(item, "contents");
                    if (string.IsNullOrEmpty(contents))
                        throw new ArgumentException(
                            "Text 'contents' must be a non-empty string.");
                    var height = item.ContainsKey("height")
                        ? RequirePositiveDouble(item, "height")
                        : db.Textsize;
                    if (double.IsNaN(height) ||
                        double.IsInfinity(height) ||
                        height <= 0)
                    {
                        throw new ArgumentException(
                            "Text height must be positive and finite, and the drawing TEXTSIZE must be valid when height is omitted.");
                    }

                    return new DBText
                    {
                        Position = position,
                        TextString = contents,
                        Height = height,
                        TextStyleId = db.Textstyle,
                    };
                }
                default:
                    throw new ArgumentException(
                        $"Unsupported entity type '{type}'.");
            }
        }

        private static Entity BuildPolyline(
            Dictionary<string, object> item,
            CancellationToken cancellationToken)
        {
            if (!item.TryGetValue("points", out var rawPoints) ||
                !(rawPoints is List<object> pointItems) ||
                pointItems.Count < 2 ||
                pointItems.Count > MaxPolylineVertices)
            {
                throw new ArgumentException(
                    $"Polyline 'points' must contain 2 to {MaxPolylineVertices} points.");
            }

            var points = new List<Point3d>(pointItems.Count);
            for (var index = 0; index < pointItems.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                points.Add(RequirePoint(
                    pointItems[index],
                    $"points[{index}]"));
            }
            var closed = GetOptionalBool(item, "closed", false);
            var commonZ = points[0].Z;
            var isPlanarAtCommonZ = true;
            foreach (var point in points)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Math.Abs(point.Z - commonZ) > 1e-9)
                {
                    isPlanarAtCommonZ = false;
                    break;
                }
            }

            if (isPlanarAtCommonZ)
            {
                var polyline = new Polyline(points.Count)
                {
                    Closed = closed,
                    Elevation = commonZ,
                };
                for (var index = 0; index < points.Count; index++)
                {
                    polyline.AddVertexAt(
                        index,
                        new Point2d(points[index].X, points[index].Y),
                        0,
                        0,
                        0);
                }
                return polyline;
            }

            return new Polyline3d(
                Poly3dType.SimplePoly,
                new Point3dCollection(points.ToArray()),
                closed);
        }

        private static ObjectId ResolveLayer(
            Dictionary<string, object> item,
            LayerTable layerTable,
            Transaction tr,
            ObjectId currentLayerId,
            out string layerName)
        {
            if (!item.TryGetValue("layer", out var rawLayer) ||
                rawLayer == null)
            {
                var current = (LayerTableRecord)tr.GetObject(
                    currentLayerId,
                    OpenMode.ForRead);
                layerName = current.Name;
                return currentLayerId;
            }

            if (!(rawLayer is string suppliedLayer) ||
                string.IsNullOrWhiteSpace(suppliedLayer))
            {
                throw new ArgumentException(
                    "'layer' must be a non-empty string when supplied.");
            }

            layerName = suppliedLayer.Trim();
            if (!layerTable.Has(layerName))
                throw new ArgumentException(
                    $"Layer '{layerName}' does not exist.");
            return layerTable[layerName];
        }

        private static Point3d RequirePoint(
            Dictionary<string, object> item,
            string key)
        {
            if (!item.TryGetValue(key, out var raw))
                throw new ArgumentException(
                    $"Missing required point '{key}'.");
            return RequirePoint(raw, key);
        }

        private static Point3d RequirePoint(object raw, string label)
        {
            if (!(raw is List<object> values) ||
                values.Count < 2 ||
                values.Count > 3)
            {
                throw new ArgumentException(
                    $"'{label}' must contain exactly 2 or 3 finite numbers.");
            }

            return new Point3d(
                ToFiniteDouble(values[0], label),
                ToFiniteDouble(values[1], label),
                values.Count == 3
                    ? ToFiniteDouble(values[2], label)
                    : 0.0);
        }

        private static double RequireFiniteDouble(
            Dictionary<string, object> item,
            string key)
        {
            if (!item.TryGetValue(key, out var raw))
                throw new ArgumentException(
                    $"Missing required number '{key}'.");
            return ToFiniteDouble(raw, key);
        }

        private static double RequirePositiveDouble(
            Dictionary<string, object> item,
            string key)
        {
            var value = RequireFiniteDouble(item, key);
            if (value <= 0)
                throw new ArgumentException(
                    $"'{key}' must be greater than zero.");
            return value;
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

        private static bool GetOptionalBool(
            Dictionary<string, object> item,
            string key,
            bool defaultValue)
        {
            if (!item.TryGetValue(key, out var raw) || raw == null)
                return defaultValue;
            if (raw is bool value)
                return value;
            throw new ArgumentException($"'{key}' must be a boolean.");
        }

        private static string GetString(
            Dictionary<string, object> item,
            string key)
            => item != null &&
               item.TryGetValue(key, out var raw) &&
               raw is string value
                ? value
                : null;

        private static string FormatHandle(Handle handle)
            => handle.Value.ToString(CultureInfo.InvariantCulture);

        private static double DegreesToRadians(double degrees)
            => degrees * Math.PI / 180.0;

        private static Dictionary<string, object> ItemFailure(
            int index,
            string type,
            string reason,
            string suggestion)
            => new Dictionary<string, object>
            {
                ["index"] = index,
                ["type"] = type,
                ["status"] = "failed",
                ["reason"] = reason,
                ["suggestion"] = suggestion,
            };

        private static string SuggestionFor(string type)
        {
            if (type == "arc")
                return "Use a positive radius and finite degree angles with a non-zero sweep; use type='circle' for 360 degrees.";
            if (type == "polyline")
                return "Provide 2 to 10000 points, each as [x,y] or [x,y,z].";
            if (type == "text")
                return "Provide position, non-empty contents, and an optional positive height.";
            return "Check required finite coordinates and use an existing layer from cad_get_layers.";
        }

        private static Task<CommandResult> Fail(
            string message,
            string suggestion)
            => Task.FromResult(CommandResult.Fail(message, suggestion));
    }
}
