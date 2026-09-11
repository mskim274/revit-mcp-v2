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
    /// Lists Revit link instances without changing their load state.
    /// link_id is the RevitLinkType id; instance_id identifies the placement.
    /// </summary>
    public class GetLinkedModelsCommand : IRevitCommand
    {
        public string Name => "get_linked_models";
        public string Category => "Query";

        public Task<CommandResult> ExecuteAsync(
            Document doc,
            Dictionary<string, object> parameters,
            CancellationToken cancellationToken)
        {
            try
            {
                var links = new List<Dictionary<string, object>>();
                foreach (var instance in new FilteredElementCollector(doc)
                    .OfClass(typeof(RevitLinkInstance))
                    .WhereElementIsNotElementType()
                    .Cast<RevitLinkInstance>())
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var typeId = instance.GetTypeId();
                    var linkType = typeId != null && typeId != ElementId.InvalidElementId
                        ? doc.GetElement(typeId) as RevitLinkType
                        : null;
                    var linkedDocument = instance.GetLinkDocument();
                    var isLoaded = linkedDocument != null;

                    links.Add(new Dictionary<string, object>
                    {
                        ["link_id"] = typeId == null ? -1 : typeId.GetValue(),
                        ["instance_id"] = instance.Id.GetValue(),
                        ["name"] = instance.Name ?? "",
                        ["path"] = ResolvePath(doc, typeId),
                        ["is_loaded"] = isLoaded,
                        ["is_unloaded"] = !isLoaded,
                        ["workset"] = ResolveWorksetName(doc, instance),
                        ["type_name"] = linkType?.Name ?? ""
                    });
                }

                links = links
                    .OrderBy(link => link["name"]?.ToString(), StringComparer.OrdinalIgnoreCase)
                    .ThenBy(link => Convert.ToInt64(link["instance_id"]))
                    .ToList();

                var result = new Dictionary<string, object>
                {
                    ["count"] = links.Count,
                    ["links"] = links
                };
                if (links.Count == 0)
                {
                    result["suggestion"] =
                        "No Revit link instances were found. Link a Revit model in the host project, then call revit_get_linked_models again.";
                }

                return Task.FromResult(CommandResult.Ok(result));
            }
            catch (OperationCanceledException)
            {
                return Task.FromResult(CommandResult.Fail(
                    "Linked-model discovery was cancelled due to timeout.",
                    "Retry when Revit is idle; this command only enumerates host RevitLinkInstance elements."));
            }
            catch (Exception ex)
            {
                return Task.FromResult(CommandResult.Fail(
                    $"Failed to list linked models: {ex.Message}",
                    "Ensure a Revit project document is open, then retry revit_get_linked_models."));
            }
        }

        private static string ResolvePath(Document doc, ElementId typeId)
        {
            if (typeId == null || typeId == ElementId.InvalidElementId)
                return "";

            try
            {
                var reference = ExternalFileUtils.GetExternalFileReference(doc, typeId);
                if (reference == null)
                    return "";
                var modelPath = reference.GetAbsolutePath() ?? reference.GetPath();
                return modelPath == null
                    ? ""
                    : ModelPathUtils.ConvertModelPathToUserVisiblePath(modelPath) ?? "";
            }
            catch
            {
                return "";
            }
        }

        private static string ResolveWorksetName(Document doc, Element element)
        {
            try
            {
                return doc.GetWorksetTable()?.GetWorkset(element.WorksetId)?.Name ?? "";
            }
            catch
            {
                return "";
            }
        }
    }
}
