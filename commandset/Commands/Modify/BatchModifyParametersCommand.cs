using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
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
    ///        (e.g. element_ids=[1,2,3], parameters={"SK_SIZE":"250A","SK_RM":"COM"})
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
            public int ElementId;
            public string ParameterName;
            public object Value;
            public bool IsTypeParam;
        }

        public Task<CommandResult> ExecuteAsync(
            Document doc,
            Dictionary<string, object> parameters,
            CancellationToken cancellationToken)
        {
            try
            {
                var onlyIfEmpty = GetBool(parameters, "only_if_empty", false);

                // ─── Build the flat set list from input shape A or B ───
                var requests = new List<SetRequest>();

                if (parameters != null
                    && parameters.TryGetValue("modifications", out var modsObj)
                    && modsObj is List<object> mods && mods.Count > 0)
                {
                    foreach (var item in mods)
                    {
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

                        requests.Add(new SetRequest
                        {
                            ElementId = Convert.ToInt32(eid),
                            ParameterName = pn.ToString(),
                            Value = val,
                            IsTypeParam = m.TryGetValue("is_type_param", out var itp)
                                          && itp != null && Convert.ToBoolean(itp)
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
                        var eid = Convert.ToInt32(idObj);
                        foreach (var kv in paramMap)
                        {
                            requests.Add(new SetRequest
                            {
                                ElementId = eid,
                                ParameterName = kv.Key,
                                Value = kv.Value,
                                IsTypeParam = false
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
                var elementCache = new Dictionary<int, Element>();
                SetRequest lastSucceeded = null;

                using (var tx = new Transaction(doc, $"MCP: Batch set {requests.Count} parameters"))
                {
                    tx.Start();

                    foreach (var req in requests)
                    {
                        // NOTE: no ThrowIfCancellationRequested inside the
                        // transaction loop — cancelling mid-transaction on
                        // workshared models causes "operation canceled" churn.
                        var succeededBefore = succeeded;
                        var failure = TryApply(doc, elementCache, req, onlyIfEmpty,
                            ref succeeded, ref skippedNotEmpty);
                        if (failure != null)
                            failures.Add(failure);
                        else if (succeeded > succeededBefore)
                            lastSucceeded = req;
                    }

                    tx.Commit();
                }

                // ─── Post-transaction verification on the last successful set ───
                Dictionary<string, object> verification = null;
                if (lastSucceeded != null && succeeded > 0)
                {
                    var elem = doc.GetElement(new ElementId(lastSucceeded.ElementId));
                    var target = lastSucceeded.IsTypeParam && elem != null
                        ? doc.GetElement(elem.GetTypeId())
                        : elem;
                    var p = target?.LookupParameter(lastSucceeded.ParameterName);
                    var actual = p != null ? GetParamDisplayValue(p) : null;
                    verification = new Dictionary<string, object>
                    {
                        ["performed"] = true,
                        ["sample_element_id"] = lastSucceeded.ElementId,
                        ["sample_parameter"] = lastSucceeded.ParameterName,
                        ["sample_actual_value"] = actual ?? "(null)"
                    };
                }

                var data = new Dictionary<string, object>
                {
                    ["total_requested"] = requests.Count,
                    ["succeeded"] = succeeded,
                    ["skipped_not_empty"] = skippedNotEmpty,
                    ["failed"] = failures.Count,
                    ["only_if_empty"] = onlyIfEmpty,
                    // Failures are reported individually; successes only as a count
                    // to keep the response small for large batches.
                    ["failures"] = failures.Take(100).ToList()
                };
                if (failures.Count > 100)
                    data["failures_truncated"] = $"{failures.Count - 100} more failures not shown";
                if (verification != null)
                    data["verification"] = verification;

                return Task.FromResult(CommandResult.Ok(data));
            }
            catch (OperationCanceledException)
            {
                return Task.FromResult(CommandResult.Fail(
                    "Batch modify was cancelled before the transaction started.",
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
            Dictionary<int, Element> elementCache,
            SetRequest req,
            bool onlyIfEmpty,
            ref int succeeded,
            ref int skippedNotEmpty)
        {
            if (!elementCache.TryGetValue(req.ElementId, out var element))
            {
                element = doc.GetElement(new ElementId(req.ElementId));
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

            if (!SetParameterValue(param, req.Value))
                return Failure(req,
                    $"Value type mismatch (storage type: {param.StorageType}).");

            succeeded++;
            return null;
        }

        private static Dictionary<string, object> Failure(SetRequest req, string reason)
        {
            return new Dictionary<string, object>
            {
                ["element_id"] = req.ElementId,
                ["parameter_name"] = req.ParameterName,
                ["reason"] = reason
            };
        }

        private static bool IsValueEmpty(Parameter param)
        {
            if (!param.HasValue) return true;
            return string.IsNullOrEmpty(param.AsString())
                && string.IsNullOrEmpty(param.AsValueString());
        }

        private static bool GetBool(Dictionary<string, object> parameters, string key, bool defaultValue)
        {
            if (parameters == null || !parameters.TryGetValue(key, out var v) || v == null)
                return defaultValue;
            try { return Convert.ToBoolean(v); }
            catch { return defaultValue; }
        }

        private static bool SetParameterValue(Parameter param, object value)
        {
            try
            {
                switch (param.StorageType)
                {
                    case StorageType.String:
                        param.Set(value?.ToString() ?? "");
                        return true;

                    case StorageType.Integer:
                        if (value is bool boolVal)
                        {
                            param.Set(boolVal ? 1 : 0);
                            return true;
                        }
                        param.Set(Convert.ToInt32(value));
                        return true;

                    case StorageType.Double:
                        param.Set(Convert.ToDouble(value));
                        return true;

                    case StorageType.ElementId:
                        param.Set(new ElementId(Convert.ToInt32(value)));
                        return true;

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
                case StorageType.ElementId: return param.AsElementId().IntegerValue.ToString();
                default: return null;
            }
        }
    }
}
