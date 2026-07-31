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
    /// Get all element types (system + loadable) for a given category.
    /// This is the go-to command when you need to know "what wall types exist?"
    ///
    /// Parameters:
    ///   category (string, required) — Category name (e.g. "Walls", "Floors", "StructuralFraming")
    /// </summary>
    public class GetTypesByCategoryCommand : IRevitCommand
    {
        public string Name => "get_types_by_category";
        public string Category => "Query";

        public Task<CommandResult> ExecuteAsync(
            Document doc,
            Dictionary<string, object> parameters,
            CancellationToken cancellationToken)
        {
            try
            {
                if (parameters == null || !parameters.TryGetValue("category", out var catObj) || catObj == null)
                    return Task.FromResult(CommandResult.Fail(
                        "Missing required parameter: category",
                        "Provide a category name like 'Walls', 'Floors'. Use revit_get_all_categories to list options."));

                var categoryName = catObj.ToString();

                if (!TryResolveCategory(categoryName, out BuiltInCategory builtInCat))
                    return Task.FromResult(CommandResult.Fail(
                        $"Unknown category: '{categoryName}'",
                        "Use revit_get_all_categories to see valid names."));

                cancellationToken.ThrowIfCancellationRequested();
                var elementTypes = new FilteredElementCollector(doc)
                    .OfCategory(builtInCat)
                    .WhereElementIsElementType()
                    .ToElements()
                    .OrderBy(t => t.Name)
                    .ThenBy(t => t.Id.GetValue())
                    .ToList();

                var instanceCounts = new Dictionary<ElementId, int>();
                foreach (var instance in new FilteredElementCollector(doc)
                    .OfCategory(builtInCat)
                    .WhereElementIsNotElementType())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var typeId = instance.GetTypeId();
                    if (typeId == null || typeId == ElementId.InvalidElementId) continue;
                    instanceCounts[typeId] = instanceCounts.TryGetValue(typeId, out var count)
                        ? count + 1
                        : 1;
                }

                var types = new List<Dictionary<string, object>>(elementTypes.Count);
                foreach (var t in elementTypes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var info = new Dictionary<string, object>
                    {
                        ["id"] = t.Id.GetValue(),
                        ["name"] = t.Name
                    };

                    if (t is ElementType et)
                        info["family_name"] = et.FamilyName ?? "";

                    if (t is FamilySymbol fs)
                        info["is_active"] = fs.IsActive;

                    info["instance_count"] = instanceCounts.TryGetValue(t.Id, out var instanceCount)
                        ? instanceCount
                        : 0;
                    types.Add(info);
                }

                return Task.FromResult(CommandResult.Ok(new Dictionary<string, object>
                {
                    ["category"] = categoryName,
                    ["count"] = types.Count,
                    ["types"] = types
                }));
            }
            catch (Exception ex)
            {
                return Task.FromResult(CommandResult.Fail(
                    $"Failed to get types: {ex.Message}",
                    "Verify the category name is valid."));
            }
        }

        private bool TryResolveCategory(string name, out BuiltInCategory category)
        {
            category = default;
            if (Enum.TryParse<BuiltInCategory>(name, true, out category)) return true;
            if (Enum.TryParse<BuiltInCategory>("OST_" + name, true, out category)) return true;

            var mappings = new Dictionary<string, BuiltInCategory>(StringComparer.OrdinalIgnoreCase)
            {
                ["Walls"] = BuiltInCategory.OST_Walls,
                ["Floors"] = BuiltInCategory.OST_Floors,
                ["Roofs"] = BuiltInCategory.OST_Roofs,
                ["Ceilings"] = BuiltInCategory.OST_Ceilings,
                ["Doors"] = BuiltInCategory.OST_Doors,
                ["Windows"] = BuiltInCategory.OST_Windows,
                ["Columns"] = BuiltInCategory.OST_Columns,
                ["StructuralColumns"] = BuiltInCategory.OST_StructuralColumns,
                ["StructuralFraming"] = BuiltInCategory.OST_StructuralFraming,
                ["Beams"] = BuiltInCategory.OST_StructuralFraming,
                ["StructuralFoundation"] = BuiltInCategory.OST_StructuralFoundation,
                ["Rooms"] = BuiltInCategory.OST_Rooms,
                ["Furniture"] = BuiltInCategory.OST_Furniture,
                ["GenericModel"] = BuiltInCategory.OST_GenericModel,
                ["Pipes"] = BuiltInCategory.OST_PipeCurves,
                ["Ducts"] = BuiltInCategory.OST_DuctCurves
            };

            return mappings.TryGetValue(name, out category);
        }
    }
}
