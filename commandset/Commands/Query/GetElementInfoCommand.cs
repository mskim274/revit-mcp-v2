using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using RevitMCP.CommandSet.Interfaces;

namespace RevitMCP.CommandSet.Commands.Query
{
    /// <summary>
    /// Get detailed information about a specific element by ID.
    /// Returns all instance and type parameters.
    /// </summary>
    public class GetElementInfoCommand : IRevitCommand
    {
        public string Name => "get_element_info";
        public string Category => "Query";

        public Task<CommandResult> ExecuteAsync(
            Document doc,
            Dictionary<string, object> parameters,
            CancellationToken cancellationToken)
        {
            try
            {
                if (!parameters.TryGetValue("element_id", out var idObj))
                    return Task.FromResult(CommandResult.Fail(
                        "Missing required parameter: element_id",
                        "Provide an element ID. Use revit_query_elements to find element IDs."));

                var elementId = ElementIdCompatibility.Create(Convert.ToInt64(idObj));
                var elem = doc.GetElement(elementId);

                if (elem == null)
                    return Task.FromResult(CommandResult.Fail(
                        $"Element with ID {elementId.GetValue()} not found.",
                        "The element may have been deleted. Use revit_query_elements to find valid elements."));

                var result = new Dictionary<string, object>
                {
                    ["id"] = elem.Id.GetValue(),
                    ["name"] = elem.Name ?? "",
                    ["category"] = elem.Category?.Name ?? "Unknown",
                    ["class"] = elem.GetType().Name
                };

                // Type info
                var typeId = elem.GetTypeId();
                if (typeId != null && typeId != ElementId.InvalidElementId)
                {
                    var typeElem = doc.GetElement(typeId);
                    result["type_name"] = typeElem?.Name ?? "";
                    result["family_name"] = (typeElem as ElementType)?.FamilyName ?? "";
                }

                // Level
                var levelId = elem.LevelId;
                if (levelId != null && levelId != ElementId.InvalidElementId)
                {
                    var level = doc.GetElement(levelId) as Level;
                    result["level"] = level?.Name ?? "";
                }

                // Location
                if (elem.Location is LocationPoint lp)
                {
                    result["location"] = new Dictionary<string, double>
                    {
                        ["x"] = Math.Round(lp.Point.X, 4),
                        ["y"] = Math.Round(lp.Point.Y, 4),
                        ["z"] = Math.Round(lp.Point.Z, 4)
                    };
                }
                else if (elem.Location is LocationCurve lc)
                {
                    result["location_start"] = new Dictionary<string, double>
                    {
                        ["x"] = Math.Round(lc.Curve.GetEndPoint(0).X, 4),
                        ["y"] = Math.Round(lc.Curve.GetEndPoint(0).Y, 4),
                        ["z"] = Math.Round(lc.Curve.GetEndPoint(0).Z, 4)
                    };
                    result["location_end"] = new Dictionary<string, double>
                    {
                        ["x"] = Math.Round(lc.Curve.GetEndPoint(1).X, 4),
                        ["y"] = Math.Round(lc.Curve.GetEndPoint(1).Y, 4),
                        ["z"] = Math.Round(lc.Curve.GetEndPoint(1).Z, 4)
                    };
                    result["length"] = Math.Round(lc.Curve.Length, 4);
                }

                // Bounding box
                var bb = elem.get_BoundingBox(null);
                if (bb != null)
                {
                    result["bounding_box"] = new Dictionary<string, object>
                    {
                        ["min"] = new Dictionary<string, double>
                        {
                            ["x"] = Math.Round(bb.Min.X, 4),
                            ["y"] = Math.Round(bb.Min.Y, 4),
                            ["z"] = Math.Round(bb.Min.Z, 4)
                        },
                        ["max"] = new Dictionary<string, double>
                        {
                            ["x"] = Math.Round(bb.Max.X, 4),
                            ["y"] = Math.Round(bb.Max.Y, 4),
                            ["z"] = Math.Round(bb.Max.Z, 4)
                        }
                    };
                }

                // Instance parameters
                var instanceParams = new Dictionary<string, object>();
                var instanceParamDetails = new List<Dictionary<string, object>>();
                foreach (Parameter param in elem.Parameters)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (param.Definition == null) continue;
                    var val = GetParameterValue(param);
                    if (val != null)
                        AddLegacyParameterValue(instanceParams, param.Definition.Name, val);
                    instanceParamDetails.Add(SerializeParameter(param));
                }
                result["instance_parameters"] = instanceParams;
                result["instance_parameter_details"] = instanceParamDetails;

                // Type parameters
                if (typeId != null && typeId != ElementId.InvalidElementId)
                {
                    var typeElem = doc.GetElement(typeId);
                    if (typeElem != null)
                    {
                        var typeParams = new Dictionary<string, object>();
                        var typeParamDetails = new List<Dictionary<string, object>>();
                        foreach (Parameter param in typeElem.Parameters)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (param.Definition == null) continue;
                            var val = GetParameterValue(param);
                            if (val != null)
                                AddLegacyParameterValue(typeParams, param.Definition.Name, val);
                            typeParamDetails.Add(SerializeParameter(param));
                        }
                        result["type_parameters"] = typeParams;
                        result["type_parameter_details"] = typeParamDetails;
                    }
                }

                return Task.FromResult(CommandResult.Ok(result));
            }
            catch (Exception ex)
            {
                return Task.FromResult(CommandResult.Fail(
                    $"Failed to get element info: {ex.Message}",
                    "Verify the element_id is a valid integer."));
            }
        }

        private object GetParameterValue(Parameter param)
        {
            if (!param.HasValue) return null;

            switch (param.StorageType)
            {
                case StorageType.String:
                    return param.AsString();
                case StorageType.Integer:
                    return param.AsInteger();
                case StorageType.Double:
                    var valueString = param.AsValueString();
                    return valueString ?? Math.Round(param.AsDouble(), 6).ToString();
                case StorageType.ElementId:
                    return param.AsElementId()?.GetValue();
                default:
                    return param.AsValueString();
            }
        }

        private static void AddLegacyParameterValue(
            Dictionary<string, object> values,
            string name,
            object value)
        {
            var key = name;
            var suffix = 2;
            while (values.ContainsKey(key))
                key = $"{name} ({suffix++})";
            values[key] = value;
        }

        private static Dictionary<string, object> SerializeParameter(Parameter param)
        {
            object rawValue = null;
            if (param.HasValue)
            {
                switch (param.StorageType)
                {
                    case StorageType.String: rawValue = param.AsString(); break;
                    case StorageType.Integer: rawValue = param.AsInteger(); break;
                    case StorageType.Double: rawValue = param.AsDouble(); break;
                    case StorageType.ElementId: rawValue = param.AsElementId()?.GetValue(); break;
                }
            }

            string specTypeId = null;
            try { specTypeId = param.Definition.GetDataType()?.TypeId; }
            catch { }

            string sharedGuid = null;
            try
            {
                if (param.IsShared) sharedGuid = param.GUID.ToString("D");
            }
            catch { }

            return new Dictionary<string, object>
            {
                ["name"] = param.Definition.Name,
                ["parameter_id"] = param.Id.GetValue(),
                ["shared_guid"] = sharedGuid ?? "",
                ["storage_type"] = param.StorageType.ToString(),
                ["spec_type_id"] = specTypeId ?? "",
                ["has_value"] = param.HasValue,
                ["read_only"] = param.IsReadOnly,
                ["raw_value"] = rawValue,
                ["display_value"] = param.HasValue ? param.AsValueString() ?? rawValue?.ToString() : null
            };
        }
    }
}
