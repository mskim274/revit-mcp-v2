using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using AutoCADMCP.CommandSet.Interfaces;

namespace AutoCADMCP.CommandSet.Commands
{
    /// <summary>
    /// Enumerate the layer table. Returns name, color (ACI/RGB), linetype,
    /// frozen/locked/off/plot flags, and the current layer marker.
    /// </summary>
    public class GetLayersCommand : ICadCommand
    {
        public string Name => "get_layers";
        public string Category => "Query";

        public Task<CommandResult> ExecuteAsync(
            Database db,
            Transaction tr,
            Dictionary<string, object> parameters,
            CancellationToken cancellationToken)
        {
            try
            {
                var layerTable = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                var layers = new List<Dictionary<string, object>>();

                foreach (var id in layerTable)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var rec = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);

                    // Linetype name lookup.
                    string linetypeName = null;
                    try
                    {
                        var lt = (LinetypeTableRecord)tr.GetObject(rec.LinetypeObjectId, OpenMode.ForRead);
                        linetypeName = lt.Name;
                    }
                    catch { /* missing linetype — leave null */ }

                    layers.Add(new Dictionary<string, object>
                    {
                        ["name"] = rec.Name,
                        ["color_index"] = rec.Color.IsByAci ? (int?)rec.Color.ColorIndex : null,
                        ["color_rgb"] = rec.Color.IsByColor
                            ? new[] { (int)rec.Color.Red, (int)rec.Color.Green, (int)rec.Color.Blue }
                            : null,
                        ["linetype"] = linetypeName,
                        ["frozen"] = rec.IsFrozen,
                        ["locked"] = rec.IsLocked,
                        ["off"] = rec.IsOff,
                        ["plottable"] = rec.IsPlottable,
                        ["is_current"] = (id == db.Clayer),
                    });
                }

                return Task.FromResult(CommandResult.Ok(new Dictionary<string, object>
                {
                    ["total"] = layers.Count,
                    ["layers"] = layers,
                }));
            }
            catch (System.Exception ex)
            {
                return Task.FromResult(CommandResult.Fail(
                    $"get_layers failed: {ex.Message}",
                    "Ensure a drawing is open."));
            }
        }
    }
}
