using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using AutoCADMCP.CommandSet.Interfaces;

namespace AutoCADMCP.CommandSet.Commands
{
    /// <summary>
    /// Returns text-only payload for the user's PICKFIRST selection — lighter
    /// than get_selected_entities when you only need the text content for
    /// downstream parsing (e.g. extracting beam type symbols, dimensions).
    ///
    /// Each row: { handle, type ("DBText"/"MText"), text, layer, height,
    /// rotation, position[x,y,z] }. Other entity types in the selection are
    /// counted in skipped_non_text but not returned.
    /// </summary>
    public class GetSelectionTextsCommand : ICadCommand
    {
        public string Name => "get_selection_texts";
        public string Category => "Query";

        public Task<CommandResult> ExecuteAsync(
            Database db,
            Transaction tr,
            Dictionary<string, object> parameters,
            CancellationToken cancellationToken)
        {
            try
            {
                var ids = SelectionContext.Current;
                if (ids == null || ids.Length == 0)
                {
                    return Task.FromResult(CommandResult.Ok(new Dictionary<string, object>
                    {
                        ["count"] = 0,
                        ["total_selected"] = 0,
                        ["skipped_non_text"] = 0,
                        ["texts"] = new List<object>(),
                        ["note"] = "No PICKFIRST selection. Select text/grid in AutoCAD first, then re-run.",
                    }));
                }
                int totalSelected = ids.Length;
                int skipped = 0;
                var texts = new List<Dictionary<string, object>>();

                foreach (var oid in ids)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var ent = tr.GetObject(oid, OpenMode.ForRead) as Entity;
                    if (ent == null) { skipped++; continue; }

                    // Order matters: MText first because it does NOT inherit from
                    // DBText in AutoCAD .NET, but defensive.
                    if (ent is MText m)
                    {
                        texts.Add(new Dictionary<string, object>
                        {
                            ["handle"] = ent.Handle.Value.ToString("X"),
                            ["type"] = "MText",
                            ["text"] = m.Text,
                            ["contents"] = m.Contents,
                            ["layer"] = ent.Layer,
                            ["height"] = m.TextHeight,
                            ["rotation"] = m.Rotation,
                            ["width"] = m.Width,
                            ["position"] = new[] { m.Location.X, m.Location.Y, m.Location.Z },
                        });
                    }
                    else if (ent is DBText t)
                    {
                        texts.Add(new Dictionary<string, object>
                        {
                            ["handle"] = ent.Handle.Value.ToString("X"),
                            ["type"] = "DBText",
                            ["text"] = t.TextString,
                            ["layer"] = ent.Layer,
                            ["height"] = t.Height,
                            ["rotation"] = t.Rotation,
                            ["width_factor"] = t.WidthFactor,
                            ["position"] = new[] { t.Position.X, t.Position.Y, t.Position.Z },
                        });
                    }
                    else
                    {
                        skipped++;
                    }
                }

                return Task.FromResult(CommandResult.Ok(new Dictionary<string, object>
                {
                    ["count"] = texts.Count,
                    ["total_selected"] = totalSelected,
                    ["skipped_non_text"] = skipped,
                    ["texts"] = texts,
                }));
            }
            catch (System.Exception ex)
            {
                return Task.FromResult(CommandResult.Fail(
                    $"get_selection_texts failed: {ex.Message}",
                    "Ensure the drawing has a selection containing DBText/MText."));
            }
        }
    }
}
