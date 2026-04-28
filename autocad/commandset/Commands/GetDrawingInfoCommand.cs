using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AutoCADMCP.CommandSet.Interfaces;

namespace AutoCADMCP.CommandSet.Commands
{
    /// <summary>
    /// Drawing-level metadata: units, model-space extents, layout list,
    /// and the current/UCS coordinate origins. Cheap read — no per-entity
    /// iteration. Equivalent to Revit MCP's get_project_info.
    /// </summary>
    public class GetDrawingInfoCommand : ICadCommand
    {
        public string Name => "get_drawing_info";
        public string Category => "Query";

        public Task<CommandResult> ExecuteAsync(
            Database db,
            Transaction tr,
            Dictionary<string, object> parameters,
            CancellationToken cancellationToken)
        {
            try
            {
                var doc = Application.DocumentManager.MdiActiveDocument;

                // Layouts: walk the DBDictionary of layouts.
                var layoutNames = new List<string>();
                var layoutDict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                foreach (DBDictionaryEntry entry in layoutDict)
                {
                    var layout = (Layout)tr.GetObject(entry.Value, OpenMode.ForRead);
                    layoutNames.Add(layout.LayoutName);
                }

                // Model-space extents from EXTMIN/EXTMAX system variables.
                // These are updated by ZOOM EXTENTS or REGEN — may be stale or
                // wildly inaccurate on a fresh empty drawing (default is
                // ±1.0E20). We surface them as-is and let the LLM judge.
                var extMin = db.Extmin;
                var extMax = db.Extmax;
                bool extentsLookValid =
                    Math.Abs(extMin.X) < 1e19 && Math.Abs(extMax.X) < 1e19;
                Dictionary<string, object> extents = extentsLookValid
                    ? new Dictionary<string, object>
                    {
                        ["min"] = new[] { extMin.X, extMin.Y, extMin.Z },
                        ["max"] = new[] { extMax.X, extMax.Y, extMax.Z },
                        ["note"] = "From EXTMIN/EXTMAX. Run ZOOM EXTENTS in AutoCAD if these look wrong.",
                    }
                    : null;

                var data = new Dictionary<string, object>
                {
                    ["autocad_version"] = Application.Version.ToString(),
                    ["document_name"] = doc?.Name ?? "(unknown)",
                    ["document_path"] = doc?.Database?.Filename ?? "",
                    ["units"] = new Dictionary<string, object>
                    {
                        ["insertion"] = db.Insunits.ToString(),
                        ["angbase_radians"] = db.Angbase,
                        ["angdir_clockwise"] = db.Angdir,
                    },
                    ["model_space_extents"] = extents,
                    ["layouts"] = layoutNames,
                    ["layout_count"] = layoutNames.Count,
                };

                return Task.FromResult(CommandResult.Ok(data));
            }
            catch (System.Exception ex)
            {
                return Task.FromResult(CommandResult.Fail(
                    $"get_drawing_info failed: {ex.Message}",
                    "Ensure a drawing is open in AutoCAD."));
            }
        }
    }
}
