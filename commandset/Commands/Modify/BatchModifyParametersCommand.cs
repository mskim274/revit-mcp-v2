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
    /// Set parameter values on many elements in a SINGLE transaction.
    /// Replaces hundreds of individual modify_element_parameter calls.
    ///
    /// Input shapes (use exactly one):
    ///   A) modifications (array) — fine-grained control, one entry per set:
    ///        [{ element_id, parameter_name, value, is_type_param? }, ...]
    ///   B) element_ids (array) + parameters (object) — cross product:
    ///        every name→value pair is applied to every element.
    ///        (e.g. element_ids=[1,2,3], parameters={"Comments":"Reviewed"})
    ///
    /// Options:
    ///   only_if_empty (bool, default false) — only set parameters that currently
    ///        have no value. Existing values are reported as skipped, never
    ///        overwritten ("fill the blanks" mode).
    ///
    /// Limits: max 5000 individual sets per call.
    /// Partial success is allowed: failed/skipped items are reported per-item,
    /// successful sets are committed together.
    /// </summary>
    public class BatchModifyParametersCommand : IRevitCommand
    {
        public string Name => "batch_modify_parameters";
        public string Category => "Modify";

        private const int MaxSetsPerCall = 5000;

        private sealed class SetRequest
        {
            public long ElementId;
            public string ParameterName;
            public object Value;
            public bool IsTypeParam;
            public string ValueMode;
            public string ValidationError;
            public object ExpectedRawValue;
            public string ExpectedDisplayValue;
        }

        public Task<CommandResult> ExecuteAsync(
            Document doc,
            Dictionary<string, object> parameters,
            CancellationToken cancellationToken)
        {
            try
            {
                if (!RawParameterValidation.TryGetOptionalStrictBool(
                        parameters,
                        "only_if_empty",
                        defaultValue: false,
                        out var onlyIfEmpty,
                        out var onlyIfEmptyError))
                {
                    return Task.FromResult(CommandResult.Fail(
                        onlyIfEmptyError,
                        "Pass only_if_empty as true or false, or omit it to use false."));
                }
                var defaultValueMode = GetValueMode(parameters, "internal");

                // ─── Build the flat set list from input shape A or B ───
                var requests = new List<SetRequest>();

                if (parameters != null
                    && parameters.TryGetValue("modifications", out var modsObj)
                    && modsObj is List<object> mods && mods.Count > 0)
                {
                    for (var modificationIndex = 0;
                         modificationIndex < mods.Count;
                         modificationIndex++)
                    {
                        var item = mods[modificationIndex];
                        if (!(item is Dictionary<string, object> m))
                            return Task.FromResult(CommandResult.Fail(
                                "Each entry in 'modifications' must be an object.",
                                "Expected shape: {element_id, parameter_name, value, is_type_param?}."));

                        if (!m.TryGetValue("element_id", out var eid) || eid == null
                            || !m.TryGetValue("parameter_name", out var pn) || pn == null
                            || !m.TryGetValue("value", out var val))
                            return Task.FromResult(CommandResult.Fail(
                                "A 'modifications' entry is missing element_id, parameter_name, or value.",
                                "Every entry needs: element_id (int), parameter_name (string), value."));

                        if (!RawParameterValidation.TryGetOptionalStrictBool(
                                m,
                                "is_type_param",
                                defaultValue: false,
                                out var isTypeParam,
                                out var isTypeParamError))
                        {
                            return Task.FromResult(CommandResult.Fail(
                                $"modifications[{modificationIndex}]: {isTypeParamError}",
                                "Pass is_type_param as true or false, or omit it to use false."));
                        }

                        requests.Add(new SetRequest
                        {
                            ElementId = Convert.ToInt64(eid),
                            ParameterName = pn.ToString(),
                            Value = val,
                            IsTypeParam = isTypeParam,
                            ValueMode = GetValueMode(m, defaultValueMode),
                            ValidationError = GetValueValidationError(val)
                        });
                    }
                }
                else if (parameters != null
                    && parameters.TryGetValue("element_ids", out var idsObj)
                    && idsObj is List<object> ids && ids.Count > 0
                    && parameters.TryGetValue("parameters", out var paramsObj)
                    && paramsObj is Dictionary<string, object> paramMap && paramMap.Count > 0)
                {
                    foreach (var idObj in ids)
                    {
                        var eid = Convert.ToInt64(idObj);
                        foreach (var kv in paramMap)
                        {
                            requests.Add(new SetRequest
                            {
                                ElementId = eid,
                                ParameterName = kv.Key,
                                Value = kv.Value,
                                IsTypeParam = false,
                                ValueMode = defaultValueMode,
                                ValidationError =
                                    GetValueValidationError(kv.Value)
                            });
                        }
                    }
                }
                else
                {
                    return Task.FromResult(CommandResult.Fail(
                        "No modifications provided.",
                        "Pass either 'modifications' (array of {element_id, parameter_name, value}) " +
                        "or 'element_ids' (array) + 'parameters' (name→value object)."));
                }

                if (requests.Count > MaxSetsPerCall)
                    return Task.FromResult(CommandResult.Fail(
                        $"Too many parameter sets: {requests.Count} (max {MaxSetsPerCall}).",
                        "Split the batch into smaller calls."));

                cancellationToken.ThrowIfCancellationRequested();

                // ─── Apply everything in one transaction ───
                var succeeded = 0;
                var skippedNotEmpty = 0;
                var failures = new List<Dictionary<string, object>>();
                var elementCache = new Dictionary<long, Element>();
                SetRequest lastSucceeded = null;

                using (var tx = new Transaction(doc, $"MCP: Batch set {requests.Count} parameters"))
                {
                    tx.Start();

                    foreach (var req in requests)
                    {
                        // Cancelling before the next set exits the using scope,
                        // so the still-open transaction is rolled back on dispose.
                        cancellationToken.ThrowIfCancellationRequested();
                        var succeededBefore = succeeded;
                        var failure = TryApply(doc, elementCache, req, onlyIfEmpty,
                            ref succeeded, ref skippedNotEmpty);
                        if (failure != null)
                            failures.Add(failure);
                        else if (succeeded > succeededBefore)
                            lastSucceeded = req;
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    tx.CommitOrThrow();
                }

                // ─── Post-transaction verification on the last successful set ───
                Dictionary<string, object> verification;
                if (lastSucceeded != null && succeeded > 0)
                {
                    try
                    {
                        var elem = doc.GetElement(ElementIdCompatibility.Create(lastSucceeded.ElementId));
                        var target = lastSucceeded.IsTypeParam && elem != null
                            ? doc.GetElement(elem.GetTypeId())
                            : elem;
                        var p = target?.LookupParameter(lastSucceeded.ParameterName);
                        var actualDisplay =
                            p != null ? GetParamDisplayValue(p) : null;
                        var actualRaw =
                            p != null ? GetParamRawValue(p) : null;
                        var match =
                            p != null &&
                            ParameterValuesEqual(
                                lastSucceeded.ExpectedRawValue,
                                actualRaw);
                        verification = new Dictionary<string, object>
                        {
                            ["performed"] = true,
                            ["sample_element_id"] = lastSucceeded.ElementId,
                            ["sample_parameter"] = lastSucceeded.ParameterName,
                            ["sample_requested_value"] =
                                lastSucceeded.Value ?? "(null)",
                            ["sample_expected_display_value"] =
                                lastSucceeded.ExpectedDisplayValue ?? "(null)",
                            ["sample_actual_display_value"] =
                                actualDisplay ?? "(null)",
                            ["sample_expected_internal_value"] =
                                lastSucceeded.ExpectedRawValue ?? "(null)",
                            ["sample_actual_internal_value"] =
                                actualRaw ?? "(null)",
                            ["sample_value_mode"] = lastSucceeded.ValueMode,
                            ["match"] = match
                        };
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
                }
                else
                {
                    verification = new Dictionary<string, object>
                    {
                        ["performed"] = false,
                        ["match"] = false,
                        ["note"] =
                            "No successful parameter write was available to verify."
                    };
                }

                var data = new Dictionary<string, object>
                {
                    ["total_requested"] = requests.Count,
                    ["succeeded"] = succeeded,
                    ["skipped_not_empty"] = skippedNotEmpty,
                    ["failed"] = failures.Count,
                    ["only_if_empty"] = onlyIfEmpty,
                    ["value_mode"] = defaultValueMode,
                    ["mutation_committed"] = succeeded > 0,
                    // Failures are reported individually; successes only as a count
                    // to keep the response small for large batches.
                    ["failures"] = failures.Take(100).ToList()
                };
                if (failures.Count > 100)
                    data["failures_truncated"] = $"{failures.Count - 100} more failures not shown";
                data["verification"] = verification;

                return Task.FromResult(CommandResult.Ok(data));
            }
            catch (OperationCanceledException)
            {
                return Task.FromResult(CommandResult.Fail(
                    "Batch modify was cancelled; the transaction was rolled back.",
                    "Retry with a smaller batch."));
            }
            catch (Exception ex)
            {
                return Task.FromResult(CommandResult.Fail(
                    $"Batch modify failed: {ex.Message}",
                    "No changes were committed if the transaction did not complete. " +
                    "Check element IDs and parameter names, then retry."));
            }
        }

        /// <summary>
        /// Apply one set request. Returns null on success/skip, or a failure record.
        /// </summary>
        private Dictionary<string, object> TryApply(
            Document doc,
            Dictionary<long, Element> elementCache,
            SetRequest req,
            bool onlyIfEmpty,
            ref int succeeded,
            ref int skippedNotEmpty)
        {
            if (!string.IsNullOrEmpty(req.ValidationError))
                return Failure(req, req.ValidationError);

            if (!elementCache.TryGetValue(req.ElementId, out var element))
            {
                element = doc.GetElement(ElementIdCompatibility.Create(req.ElementId));
                elementCache[req.ElementId] = element;
            }

            if (element == null)
                return Failure(req, "Element not found.");

            Element target = element;
            if (req.IsTypeParam)
            {
                var typeId = element.GetTypeId();
                if (typeId == null || typeId == ElementId.InvalidElementId)
                    return Failure(req, "Element has no type.");
                target = doc.GetElement(typeId);
                if (target == null)
                    return Failure(req, "Type element not found.");
            }

            var param = target.LookupParameter(req.ParameterName);
            if (param == null)
                return Failure(req, $"Parameter '{req.ParameterName}' not found.");

            if (param.IsReadOnly)
                return Failure(req, $"Parameter '{req.ParameterName}' is read-only.");

            if (onlyIfEmpty && !IsValueEmpty(param))
            {
                skippedNotEmpty++;
                return null;
            }

            if (param.StorageType == StorageType.Double &&
                req.ValueMode == "internal" &&
                !RawParameterValidation.TryConvertFiniteParameterDouble(
                    req.Value,
                    out _))
            {
                return Failure(
                    req,
                    "Double parameter requires a finite numeric value in internal mode.");
            }

            if (!SetParameterValue(param, req.Value, req.ValueMode))
                return Failure(req,
                    $"Value type mismatch (storage type: {param.StorageType}, value_mode: {req.ValueMode}).");

            req.ExpectedRawValue = GetParamRawValue(param);
            req.ExpectedDisplayValue = GetParamDisplayValue(param);
            succeeded++;
            return null;
        }

        private static Dictionary<string, object> Failure(SetRequest req, string reason)
        {
            return new Dictionary<string, object>
            {
                ["element_id"] = req.ElementId,
                ["parameter_name"] = req.ParameterName,
                ["value_mode"] = req.ValueMode,
                ["reason"] = reason
            };
        }

        private static bool IsValueEmpty(Parameter param)
        {
            if (!param.HasValue) return true;
            return string.IsNullOrEmpty(param.AsString())
                && string.IsNullOrEmpty(param.AsValueString());
        }

        private static string GetValueValidationError(object value)
        {
            if (value == null)
                return "Value must be a string, finite number, or boolean; null is not allowed.";
            if (RawParameterValidation.IsNonFiniteNumeric(value))
                return "Value must not be NaN or Infinity.";
            return null;
        }

        private static string GetValueMode(
            Dictionary<string, object> parameters,
            string defaultValue)
        {
            var valueMode = parameters != null
                && parameters.TryGetValue("value_mode", out var raw)
                && raw != null
                ? raw.ToString().ToLowerInvariant()
                : defaultValue;
            if (valueMode != "internal" && valueMode != "display")
                throw new ArgumentException(
                    $"Invalid value_mode '{valueMode}'. Use 'internal' or 'display'.");
            return valueMode;
        }

        private static bool SetParameterValue(Parameter param, object value, string valueMode)
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

        private static string GetParamDisplayValue(Parameter param)
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
                case StorageType.String:
                    return param.AsString();
                case StorageType.Integer:
                    return param.AsInteger();
                case StorageType.Double:
                    return param.AsDouble();
                case StorageType.ElementId:
                    return param.AsElementId().GetValue();
                default:
                    return null;
            }
        }

        private static bool ParameterValuesEqual(object expected, object actual)
        {
            if (expected == null || actual == null)
                return expected == null && actual == null;

            if (expected is double expectedDouble &&
                actual is double actualDouble)
            {
                var tolerance = Math.Max(
                    1e-9,
                    Math.Abs(expectedDouble) * 1e-9);
                return Math.Abs(expectedDouble - actualDouble) <= tolerance;
            }

            return Equals(expected, actual);
        }
    }
}
