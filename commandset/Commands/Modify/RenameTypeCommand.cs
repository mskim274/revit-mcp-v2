using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using RevitMCP.CommandSet.Interfaces;

namespace RevitMCP.CommandSet.Commands.Modify
{
    /// <summary>
    /// Rename an existing element type. Works on any ElementType.
    ///
    /// Parameters:
    ///   type_id  (int, required)    — ElementId of the type to rename
    ///   new_name (string, required) — New unique type name
    ///
    /// Returns the type's old and new names. Fails if the new name
    /// collides with an existing type in the same family/category.
    /// </summary>
    public class RenameTypeCommand : IRevitCommand
    {
        public string Name => "rename_type";
        public string Category => "Modify";

        public Task<CommandResult> ExecuteAsync(
            Document doc,
            Dictionary<string, object> parameters,
            CancellationToken cancellationToken)
        {
            try
            {
                if (parameters == null || !parameters.TryGetValue("type_id", out var idObj))
                    return Task.FromResult(CommandResult.Fail(
                        "Missing required parameter: type_id",
                        "Provide the ElementId of the type to rename."));

                if (!parameters.TryGetValue("new_name", out var nameObj) || nameObj == null)
                    return Task.FromResult(CommandResult.Fail(
                        "Missing required parameter: new_name",
                        "Provide a unique new name for the type."));

                var typeId = Convert.ToInt32(idObj);
                var newName = nameObj.ToString().Trim();

                if (string.IsNullOrEmpty(newName))
                    return Task.FromResult(CommandResult.Fail(
                        "new_name cannot be empty.",
                        "Provide a non-empty type name."));

                var typeEl = doc.GetElement(new ElementId(typeId)) as ElementType;
                if (typeEl == null)
                    return Task.FromResult(CommandResult.Fail(
                        $"Element {typeId} is not an ElementType — cannot rename as type.",
                        "Pass the ID of a TYPE element (not an instance)."));

                var oldName = typeEl.Name;
                if (oldName == newName)
                {
                    return Task.FromResult(CommandResult.Ok(new Dictionary<string, object>
                    {
                        ["type_id"] = typeId,
                        ["old_name"] = oldName,
                        ["new_name"] = newName,
                        ["unchanged"] = true,
                        ["note"] = "New name equals current name — no rename performed.",
                    }));
                }

                using (var tx = new Transaction(doc, $"MCP: Rename type → {newName}"))
                {
                    tx.Start();
                    try
                    {
                        typeEl.Name = newName;
                    }
                    catch (Autodesk.Revit.Exceptions.ArgumentException)
                    {
                        tx.RollBack();
                        return Task.FromResult(CommandResult.Fail(
                            $"Cannot rename to '{newName}' — name conflict or invalid characters.",
                            "Choose a unique name in this family/category. Avoid : { } | \\ / < > ? * etc."));
                    }
                    tx.Commit();
                }

                return Task.FromResult(CommandResult.Ok(new Dictionary<string, object>
                {
                    ["type_id"] = typeId,
                    ["old_name"] = oldName,
                    ["new_name"] = typeEl.Name,
                    ["family_name"] = typeEl.FamilyName,
                    ["category"] = typeEl.Category?.Name ?? "(unknown)",
                }));
            }
            catch (Exception ex)
            {
                return Task.FromResult(CommandResult.Fail(
                    $"rename_type failed: {ex.Message}",
                    "Verify type_id refers to a valid ElementType."));
            }
        }
    }
}
