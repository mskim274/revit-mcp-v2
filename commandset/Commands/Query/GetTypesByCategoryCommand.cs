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

                var types = new FilteredElementCollector(doc)
                    .OfCategory(builtInCat)
                    .WhereElementIsElementType()
                    .OrderBy(t => t.Name)
                    .Select(t =>
                    {
                        var info = new Dictionary<string, object>
                        {
                            ["id"] = t.Id.IntegerValue,
                            ["name"] = t.Name
                        };

                        if (t is ElementType et)
                            info["family_name"] = et.FamilyName ?? "";

                        if (t is FamilySymbol fs)
                            info["is_active"] = fs.IsActive;

                        // Count instances using this type
                        var instanceCount = new FilteredElementCollector(doc)
                            .OfCategory(builtInCat)
                            .WhereElementIsNotElementType()
                            .Where(e => e.GetTypeId() == t.Id)
                            .Count();
                        info["instance_count"] = instanceCount;

                        return info;
                    })
                    .ToList();

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
