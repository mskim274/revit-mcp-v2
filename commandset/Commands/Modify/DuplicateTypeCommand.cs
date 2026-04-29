using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using RevitMCP.CommandSet.Interfaces;

namespace RevitMCP.CommandSet.Commands.Modify
{
    /// <summary>
    /// Duplicate an existing element type with a new name. Works on any
    /// ElementType (FamilySymbol for loadable families, WallType, FloorType,
    /// etc.).
    ///
    /// Parameters:
    ///   source_type_id (int, required)    — ElementId of the existing type
    ///   new_name       (string, required) — New unique type name
    ///
    /// Returns the new type's ID and name. Fails if a type with the new
    /// name already exists in the family/system.
    /// </summary>
    public class DuplicateTypeCommand : IRevitCommand
    {
        public string Name => "duplicate_type";
        public string Category => "Modify";

        public Task<CommandResult> ExecuteAsync(
            Document doc,
            Dictionary<string, object> parameters,
            CancellationToken cancellationToken)
        {
            try
            {
                if (parameters == null || !parameters.TryGetValue("source_type_id", out var idObj))
                    return Task.FromResult(CommandResult.Fail(
                        "Missing required parameter: source_type_id",
                        "Provide the ElementId of the existing type to duplicate."));

                if (!parameters.TryGetValue("new_name", out var nameObj) || nameObj == null)
                    return Task.FromResult(CommandResult.Fail(
                        "Missing required parameter: new_name",
                        "Provide a unique name for the new type."));

                var sourceId = Convert.ToInt32(idObj);
                var newName = nameObj.ToString().Trim();

                if (string.IsNullOrEmpty(newName))
                    return Task.FromResult(CommandResult.Fail(
                        "new_name cannot be empty.",
                        "Provide a non-empty type name."));

                var source = doc.GetElement(new ElementId(sourceId)) as ElementType;
                if (source == null)
                    return Task.FromResult(CommandResult.Fail(
                        $"Element {sourceId} is not an ElementType (type) — cannot duplicate.",
                        "Pass the ID of a TYPE element (not an instance). Use revit_get_family_types with include_types=true."));

                ElementType duplicated = null;
                using (var tx = new Transaction(doc, $"MCP: Duplicate type → {newName}"))
                {
                    tx.Start();
                    try
                    {
                        duplicated = source.Duplicate(newName);
                    }
                    catch (Autodesk.Revit.Exceptions.ArgumentException)
                    {
                        tx.RollBack();
                        return Task.FromResult(CommandResult.Fail(
                            $"Type name '{newName}' already exists or is invalid.",
                            "Choose a unique name. Names must not contain : { } | \\ / < > ? * etc."));
                    }
                    tx.Commit();
                }

                return Task.FromResult(CommandResult.Ok(new Dictionary<string, object>
                {
                    ["source_type_id"] = sourceId,
                    ["source_type_name"] = source.Name,
                    ["new_type_id"] = duplicated.Id.IntegerValue,
                    ["new_type_name"] = duplicated.Name,
                    ["family_name"] = duplicated.FamilyName,
                    ["category"] = duplicated.Category?.Name ?? "(unknown)",
                }));
            }
            catch (Exception ex)
            {
                return Task.FromResult(CommandResult.Fail(
                    $"duplicate_type failed: {ex.Message}",
                    "Verify source_type_id refers to a valid ElementType."));
            }
        }
    }
}
