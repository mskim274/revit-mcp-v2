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
    /// Reassign the type of one or more instances. Supports batch operation
    /// in a single transaction so 50+ instances can be remapped efficiently.
    ///
    /// Parameters:
    ///   instance_ids (int OR int[], required) — One ID or list of instance IDs
    ///   new_type_id  (int, required)          — Target ElementType ID
    ///
    /// Returns per-instance success/failure + aggregate counts. Uses
    /// Element.ChangeTypeId which works across families (FamilyInstance,
    /// Wall, Floor, etc.).
    /// </summary>
    public class ChangeInstanceTypeCommand : IRevitCommand
    {
        public string Name => "change_instance_type";
        public string Category => "Modify";

        public Task<CommandResult> ExecuteAsync(
            Document doc,
            Dictionary<string, object> parameters,
            CancellationToken cancellationToken)
        {
            try
            {
                if (parameters == null || !parameters.TryGetValue("instance_ids", out var idsObj))
                    return Task.FromResult(CommandResult.Fail(
                        "Missing required parameter: instance_ids",
                        "Provide a single instance ID (int) or an array of IDs."));

                if (!parameters.TryGetValue("new_type_id", out var typeIdObj))
                    return Task.FromResult(CommandResult.Fail(
                        "Missing required parameter: new_type_id",
                        "Provide the ElementType ID to assign."));

                var newTypeId = ElementIdCompatibility.Create(Convert.ToInt64(typeIdObj));
                var newType = doc.GetElement(newTypeId) as ElementType;
                if (newType == null)
                    return Task.FromResult(CommandResult.Fail(
                        $"new_type_id {newTypeId.GetValue()} is not a valid ElementType.",
                        "Use revit_get_family_types to find valid type IDs."));

                // Coerce instance_ids to a list
                var idList = new List<long>();
                if (idsObj is List<object> list)
                {
                    foreach (var v in list) idList.Add(Convert.ToInt64(v));
                }
                else if (idsObj is long[] longArray)
                {
                    idList.AddRange(longArray);
                }
                else if (idsObj is int[] arr)
                {
                    idList.AddRange(arr.Select(v => (long)v));
                }
                else
                {
                    idList.Add(Convert.ToInt64(idsObj));
                }

                idList = idList.Distinct().ToList();
                if (idList.Count == 0)
                    return Task.FromResult(CommandResult.Fail(
                        "instance_ids is empty.",
                        "Provide at least one instance ID."));

                if (idList.Count > 1000)
                    return Task.FromResult(CommandResult.Fail(
                        $"Too many instance_ids ({idList.Count}). Max 1000 per call.",
                        "Batch in chunks of ≤1000."));

                var changed = new List<Dictionary<string, object>>();
                var unchanged = new List<Dictionary<string, object>>();
                var failed = new List<Dictionary<string, object>>();

                using (var tx = new Transaction(doc, $"MCP: Change instance types → {newType.Name}"))
                {
                    tx.Start();

                    foreach (var instanceId in idList)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var inst = doc.GetElement(ElementIdCompatibility.Create(instanceId));
                        if (inst == null)
                        {
                            failed.Add(new Dictionary<string, object>
                            {
                                ["instance_id"] = instanceId,
                                ["reason"] = "Element not found",
                            });
                            continue;
                        }

                        var prevTypeId = inst.GetTypeId();
                        if (prevTypeId == newTypeId)
                        {
                            // Already on the target type — count as no-op success
                            unchanged.Add(new Dictionary<string, object>
                            {
                                ["instance_id"] = instanceId,
                                ["previous_type_id"] = prevTypeId.GetValue(),
                                ["new_type_id"] = newTypeId.GetValue(),
                                ["unchanged"] = true,
                            });
                            continue;
                        }

                        try
                        {
                            var returnedId = inst.ChangeTypeId(newTypeId);
                            var actualInstanceId = returnedId != null
                                && returnedId != ElementId.InvalidElementId
                                ? returnedId
                                : inst.Id;
                            changed.Add(new Dictionary<string, object>
                            {
                                ["requested_instance_id"] = instanceId,
                                ["actual_instance_id"] = actualInstanceId.GetValue(),
                                ["previous_type_id"] = prevTypeId.GetValue(),
                                ["new_type_id"] = newTypeId.GetValue(),
                                ["element_replaced"] = actualInstanceId.GetValue() != instanceId,
                            });
                        }
                        catch (Exception ex)
                        {
                            failed.Add(new Dictionary<string, object>
                            {
                                ["instance_id"] = instanceId,
                                ["reason"] = ex.Message,
                            });
                        }
                    }

                    if (changed.Count == 0 && unchanged.Count == 0 && failed.Count > 0)
                    {
                        // All failed — rollback
                        tx.RollBack();
                        return Task.FromResult(CommandResult.Fail(
                            $"All {failed.Count} type changes failed. Transaction rolled back.",
                            "Inspect 'failed' details. Common causes: incompatible category, type-locked elements, workset permissions."));
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    tx.CommitOrThrow();
                }

                var verification = new Dictionary<string, object>();
                try
                {
                    var verified = 0;
                    var mismatches = new List<Dictionary<string, object>>();
                    var verificationTargets = changed
                        .Select(item => new Dictionary<string, object>
                        {
                            ["instance_id"] = item["actual_instance_id"],
                            ["result"] = "changed"
                        })
                        .Concat(unchanged.Select(item =>
                            new Dictionary<string, object>
                            {
                                ["instance_id"] = item["instance_id"],
                                ["result"] = "already_target_type"
                            }))
                        .ToList();

                    foreach (var item in verificationTargets)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var actualId = ElementIdCompatibility.Create(
                            Convert.ToInt64(item["instance_id"]));
                        var actual = doc.GetElement(actualId);
                        if (actual != null && actual.GetTypeId() == newTypeId)
                        {
                            verified++;
                        }
                        else
                        {
                            mismatches.Add(new Dictionary<string, object>
                            {
                                ["instance_id"] = actualId.GetValue(),
                                ["result"] = item["result"],
                                ["reason"] = actual == null
                                    ? "Element not found after commit."
                                    : $"Actual type is {actual.GetTypeId().GetValue()}, expected {newTypeId.GetValue()}."
                            });
                        }
                    }
                    verification["performed"] = true;
                    verification["expected_count"] =
                        verificationTargets.Count;
                    verification["verified_count"] = verified;
                    verification["match"] =
                        verified == verificationTargets.Count;
                    verification["mismatches"] = mismatches.Take(25).ToList();
                }
                catch (Exception verificationError)
                {
                    verification["performed"] = false;
                    verification["match"] = false;
                    verification["error"] = verificationError.Message;
                }

                return Task.FromResult(CommandResult.Ok(new Dictionary<string, object>
                {
                    ["new_type_id"] = newTypeId.GetValue(),
                    ["new_type_name"] = newType.Name,
                    ["requested"] = idList.Count,
                    ["changed_count"] = changed.Count,
                    ["unchanged_count"] = unchanged.Count,
                    ["failed_count"] = failed.Count,
                    ["changed"] = changed,
                    ["unchanged"] = unchanged,
                    ["failed"] = failed,
                    ["mutation_committed"] = changed.Count > 0,
                    ["verification"] = verification,
                }));
            }
            catch (OperationCanceledException)
            {
                return Task.FromResult(CommandResult.Fail(
                    "Type change was cancelled; an active transaction was rolled back.",
                    "Retry with a smaller batch and a new idempotency key."));
            }
            catch (Exception ex)
            {
                return Task.FromResult(CommandResult.Fail(
                    $"change_instance_type failed: {ex.Message}",
                    "Check that instance_ids and new_type_id are valid."));
            }
        }
    }
}
