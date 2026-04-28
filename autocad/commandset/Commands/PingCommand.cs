using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using AutoCADMCP.CommandSet.Interfaces;

namespace AutoCADMCP.CommandSet.Commands
{
    /// <summary>
    /// First test command — connection health check. Returns AutoCAD version,
    /// active drawing name, and entity count. Mirrors Revit MCP's PingCommand.
    /// </summary>
    public class PingCommand : ICadCommand
    {
        public string Name => "ping";
        public string Category => "Utility";

        public Task<CommandResult> ExecuteAsync(
            Database db,
            Transaction tr,
            Dictionary<string, object> parameters,
            CancellationToken cancellationToken)
        {
            try
            {
                var doc = Application.DocumentManager.MdiActiveDocument;
                var version = Application.Version.ToString();
                var documentName = doc?.Name ?? "(no active document)";

                // Cheap entity count: walk the model space block table record.
                int entityCount = 0;
                if (db != null && tr != null)
                {
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(
                        bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                    foreach (var _ in ms) entityCount++;
                }

                var data = new Dictionary<string, object>
                {
                    ["autocad_version"] = version,
                    ["document_name"] = documentName,
                    ["entity_count"] = entityCount,
                    ["timestamp"] = DateTime.UtcNow.ToString("o"),
                };

                return Task.FromResult(CommandResult.Ok(data));
            }
            catch (Exception ex)
            {
                return Task.FromResult(CommandResult.Fail(
                    $"ping failed: {ex.Message}",
                    "Ensure a drawing is open in AutoCAD before calling ping."));
            }
        }
    }
}
