using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using RevitMCP.CommandSet.Helpers;
using RevitMCP.CommandSet.Interfaces;

namespace RevitMCP.CommandSet.Commands.Modify
{
    /// <summary>
    /// Modify a parameter value on a Revit element.
    /// Supports both instance and type parameters.
    ///
    /// Parameters:
    ///   element_id      (int, required)    — Element ID to modify
    ///   parameter_name  (string, required) — Parameter name to set
    ///   value           (object, required) — New value (string, number, or bool)
    ///   is_type_param   (bool, optional)   — Set on the element's type instead of instance (default: false)
    /// </summary>
    public class ModifyElementParameterCommand : IRevitCommand
    {
        public string Name => "modify_element_parameter";
        public string Category => "Modify";

        public Task<CommandResult> ExecuteAsync(
            Document doc,
            Dictionary<string, object> parameters,
            CancellationToken cancellationToken)
        {
            try
            {
                // Parse parameters
                if (parameters == null || !parameters.TryGetValue("element_id", out var eidObj))
                    return Task.FromResult(CommandResult.Fail(
                        "Missing required parameter: element_id",
                        "Provide the Revit element ID. Use revit_query_elements to find element IDs."));

                var elementId = Convert.ToInt64(eidObj);

                if (!parameters.TryGetValue("parameter_name", out var pnObj) || pnObj == null)
                    return Task.FromResult(CommandResult.Fail(
                        "Missing required parameter: parameter_name",
                        "Provide the parameter name. Use revit_get_element_info to see available parameters."));

                var paramName = pnObj.ToString();

                if (!parameters.TryGetValue("value", out var valueObj) ||
                    valueObj == null)
                    return Task.FromResult(CommandResult.Fail(
                        "Missing or null required parameter: value",
                        "Provide a string, finite number, or boolean value."));
                if (RawParameterValidation.IsNonFiniteNumeric(valueObj))
                    return Task.FromResult(CommandResult.Fail(
                        "value must not be NaN or Infinity.",
                        "Provide a finite numeric value."));

                if (!RawParameterValidation.TryGetOptionalStrictBool(
                        parameters,
                        "is_type_param",
                        defaultValue: false,
                        out var isTypeParam,
                        out var isTypeParamError))
                {
                    return Task.FromResult(CommandResult.Fail(
                        isTypeParamError,
                        "Pass is_type_param as true or false, or omit it to use false."));
                }
                var valueMode = parameters.TryGetValue("value_mode", out var vmObj) && vmObj != null
                    ? vmObj.ToString().ToLowerInvariant()
                    : "internal";
                if (valueMode != "internal" && valueMode != "display")
                    return Task.FromResult(CommandResult.Fail(
                        $"Invalid value_mode '{valueMode}'.",
                        "Use value_mode=\"internal\" for raw Revit storage values or \"display\" for a unit-formatted value."));

                // Get element
                var element = doc.GetElement(ElementIdCompatibility.Create(elementId));
                if (element == null)
                    return Task.FromResult(CommandResult.Fail(
                        $"Element with ID {elementId} not found.",
                        "Verify the element ID exists. Use revit_query_elements to find valid IDs."));

                // Resolve target element (instance or type)
                Element target = element;
                if (isTypeParam)
                {
                    var typeId = element.GetTypeId();
                    if (typeId == null || typeId == ElementId.InvalidElementId)
                        return Task.FromResult(CommandResult.Fail(
                            $"Element {elementId} has no type.",
                            "This element does not have an associated type. Try without is_type_param."));

                    target = doc.GetElement(typeId);
                    if (target == null)
                        return Task.FromResult(CommandResult.Fail(
                            $"Type element for {elementId} not found.",
                            "The element's type could not be resolved."));
                }

                // Find parameter
                var param = target.LookupParameter(paramName);
                if (param == null)
                    return Task.FromResult(CommandResult.Fail(
                        $"Parameter '{paramName}' not found on element {(isTypeParam ? "(type)" : "(instance)")}.",
                        "Use revit_get_element_info to see available parameter names."));

                if (param.IsReadOnly)
                    return Task.FromResult(CommandResult.Fail(
                        $"Parameter '{paramName}' is read-only.",
                        "This parameter cannot be modified. Check for an equivalent writable parameter."));

                object valueToSet = valueObj;
                if (param.StorageType == StorageType.Double &&
                    valueMode == "internal")
                {
                    if (!RawParameterValidation.TryConvertFiniteParameterDouble(
                            valueObj,
                            out var finiteDouble))
                    {
                        return Task.FromResult(CommandResult.Fail(
                            $"Parameter '{paramName}' requires a finite numeric value in internal mode.",
                            "Pass a finite number, or use value_mode=\"display\" with a valid Revit-formatted value."));
                    }
                    valueToSet = finiteDouble;
                }

                // Execute in transaction
                using (var tx = new Transaction(doc, $"MCP: Set {paramName}"))
                {
                    tx.Start();

                    var oldValue = GetParamDisplayValue(param);
                    bool success = SetParameterValue(param, valueToSet, valueMode);

                    if (!success)
                    {
                        tx.RollBack();
                        return Task.FromResult(CommandResult.Fail(
                            $"Failed to set parameter '{paramName}'. Value type mismatch.",
                            $"Parameter storage type is {param.StorageType}. Provide a compatible value."));
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    tx.CommitOrThrow();

                    var newValue = GetParamDisplayValue(param);
                    var verification = new Dictionary<string, object>();
                    try
                    {
                        verification["performed"] = true;
                        verification["set_returned_true"] = true;
                        verification["actual_display_value"] = newValue ?? "(null)";
                        verification["actual_internal_value"] = GetParamRawValue(param) ?? "(null)";
                    }
                    catch (Exception verificationError)
                    {
                        verification["performed"] = false;
                        verification["error"] = verificationError.Message;
                    }

                    return Task.FromResult(CommandResult.Ok(new Dictionary<string, object>
                    {
                        ["element_id"] = elementId,
                        ["parameter_name"] = paramName,
                        ["old_value"] = oldValue ?? "(null)",
                        ["new_value"] = newValue ?? "(null)",
                        ["storage_type"] = param.StorageType.ToString(),
                        ["is_type_parameter"] = isTypeParam,
                        ["value_mode"] = valueMode,
                        ["mutation_committed"] = true,
                        ["verification"] = verification
                    }));
                }
            }
            catch (OperationCanceledException)
            {
                return Task.FromResult(CommandResult.Fail(
                    "Operation cancelled.",
                    "Try again."));
            }
            catch (Exception ex)
            {
                return Task.FromResult(CommandResult.Fail(
                    $"Failed to modify parameter: {ex.Message}",
                    "Ensure the element exists and the parameter value is compatible."));
            }
        }

        private bool SetParameterValue(Parameter param, object value, string valueMode)
        {
            try
            {
                switch (param.StorageType)
                {
                    case StorageType.String:
                        return param.Set(value?.ToString() ?? "");

                    case StorageType.Integer:
                        if (valueMode == "display")
                            return param.SetValueString(value?.ToString() ?? "");
                        if (value is bool boolVal)
                            return param.Set(boolVal ? 1 : 0);
                        return param.Set(Convert.ToInt32(value));

                    case StorageType.Double:
                        if (valueMode == "display")
                            return param.SetValueString(value?.ToString() ?? "");
                        if (!RawParameterValidation.TryConvertFiniteParameterDouble(
                                value,
                                out var finiteDouble))
                            return false;
                        return param.Set(finiteDouble);

                    case StorageType.ElementId:
                        if (valueMode == "display") return false;
                        return param.Set(ElementIdCompatibility.Create(Convert.ToInt64(value)));

                    default:
                        return false;
                }
            }
            catch
            {
                return false;
            }
        }

        private string GetParamDisplayValue(Parameter param)
        {
            if (!param.HasValue) return null;

            var displayVal = param.AsValueString();
            if (!string.IsNullOrEmpty(displayVal)) return displayVal;

            switch (param.StorageType)
            {
                case StorageType.String: return param.AsString();
                case StorageType.Integer: return param.AsInteger().ToString();
                case StorageType.Double: return param.AsDouble().ToString("F4");
                case StorageType.ElementId: return param.AsElementId().GetValue().ToString();
                default: return null;
            }
        }

        private static object GetParamRawValue(Parameter param)
        {
            if (!param.HasValue) return null;
            switch (param.StorageType)
            {
                case StorageType.String: return param.AsString();
                case StorageType.Integer: return param.AsInteger();
                case StorageType.Double: return param.AsDouble();
                case StorageType.ElementId: return param.AsElementId().GetValue();
                default: return null;
            }
        }
    }
}
