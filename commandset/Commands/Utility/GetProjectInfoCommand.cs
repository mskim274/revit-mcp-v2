using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using RevitMCP.CommandSet.Interfaces;

namespace RevitMCP.CommandSet.Commands.Utility
{
    /// <summary>
    /// Returns project-level metadata (name, number, address, client,
    /// organization, author, etc.) from Document.ProjectInformation,
    /// plus document-level context (title, path, workshared, version).
    ///
    /// The TS server has shipped a revit_get_project_info tool since
    /// Sprint 1, but this backend command was never implemented — calls
    /// returned "Unknown command". This file closes that gap; the
    /// CommandDispatcher auto-discovers it via reflection (no registration).
    /// </summary>
    public class GetProjectInfoCommand : IRevitCommand
    {
        public string Name => "get_project_info";
        public string Category => "Utility";

        public Task<CommandResult> ExecuteAsync(
            Document doc,
            Dictionary<string, object> parameters,
            CancellationToken cancellationToken)
        {
            try
            {
                var info = doc.ProjectInformation;
                if (info == null)
                {
                    return Task.FromResult(CommandResult.Fail(
                        "This document has no ProjectInformation (likely a family or template).",
                        "Open a Revit project document (.rvt) and try again."));
                }

                // ProjectInfo getters can throw on certain document kinds —
                // read each defensively so one bad field doesn't fail the call.
                string Safe(Func<string> get)
                {
                    try { return get() ?? ""; } catch { return ""; }
                }

                var result = new Dictionary<string, object>
                {
                    ["project_name"] = Safe(() => info.Name),
                    ["project_number"] = Safe(() => info.Number),
                    ["project_address"] = Safe(() => info.Address),
                    ["project_status"] = Safe(() => info.Status),
                    ["client_name"] = Safe(() => info.ClientName),
                    ["organization_name"] = Safe(() => info.OrganizationName),
                    ["organization_description"] = Safe(() => info.OrganizationDescription),
                    ["building_name"] = Safe(() => info.BuildingName),
                    ["author"] = Safe(() => info.Author),
                    ["issue_date"] = Safe(() => info.IssueDate),

                    // Document-level context (mirrors ping, useful for orientation)
                    ["document_title"] = doc.Title ?? "",
                    ["document_path"] = doc.PathName ?? "",
                    ["is_workshared"] = doc.IsWorkshared,
                    ["revit_version"] = doc.Application.VersionNumber
                };

                return Task.FromResult(CommandResult.Ok(result));
            }
            catch (Exception ex)
            {
                return Task.FromResult(CommandResult.Fail(
                    $"get_project_info failed: {ex.Message}",
                    "Ensure a Revit project document is open."));
            }
        }
    }
}
