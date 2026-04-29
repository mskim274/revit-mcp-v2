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

                var newTypeId = new ElementId(Convert.ToInt32(typeIdObj));
                var newType = doc.GetElement(newTypeId) as ElementType;
                if (newType == null)
                    return Task.FromResult(CommandResult.Fail(
                        $"new_type_id {newTypeId.IntegerValue} is not a valid ElementType.",
                        "Use revit_get_family_types to find valid type IDs."));

                // Coerce instance_ids to a list
                var idList = new List<int>();
                if (idsObj is List<object> list)
                {
                    foreach (var v in list) idList.Add(Convert.ToInt32(v));
                }
                else if (idsObj is int[] arr)
                {
                    idList.AddRange(arr);
                }
                else
                {
                    idList.Add(Convert.ToInt32(idsObj));
                }

                if (idList.Count == 0)
                    return Task.FromResult(CommandResult.Fail(
                        "instance_ids is empty.",
                        "Provide at least one instance ID."));

                if (idList.Count > 1000)
                    return Task.FromResult(CommandResult.Fail(
                        $"Too many instance_ids ({idList.Count}). Max 1000 per call.",
                        "Batch in chunks of ≤1000."));

                var changed = new List<Dictionary<string, object>>();
                var failed = new List<Dictionary<string, object>>();

                using (var tx = new Transaction(doc, $"MCP: Change instance types → {newType.Name}"))
                {
                    tx.Start();

                    foreach (var instanceId in idList)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var inst = doc.GetElement(new ElementId(instanceId));
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
                            changed.Add(new Dictionary<string, object>
                            {
                                ["instance_id"] = instanceId,
                                ["previous_type_id"] = prevTypeId.IntegerValue,
                                ["new_type_id"] = newTypeId.IntegerValue,
                                ["unchanged"] = true,
                            });
                            continue;
                        }

                        try
                        {
                            inst.ChangeTypeId(newTypeId);
                            changed.Add(new Dictionary<string, object>
                            {
                                ["instance_id"] = instanceId,
                                ["previous_type_id"] = prevTypeId.IntegerValue,
                                ["new_type_id"] = newTypeId.IntegerValue,
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

                    if (changed.Count == 0 && failed.Count > 0)
                    {
                        // All failed — rollback
                        tx.RollBack();
                        return Task.FromResult(CommandResult.Fail(
                            $"All {failed.Count} type changes failed. Transaction rolled back.",
                            "Inspect 'failed' details. Common causes: incompatible category, type-locked elements, workset permissions."));
                    }

                    tx.Commit();
                }

                return Task.FromResult(CommandResult.Ok(new Dictionary<string, object>
                {
                    ["new_type_id"] = newTypeId.IntegerValue,
                    ["new_type_name"] = newType.Name,
                    ["requested"] = idList.Count,
                    ["changed_count"] = changed.Count,
                    ["failed_count"] = failed.Count,
                    ["changed"] = changed,
                    ["failed"] = failed,
                }));
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
