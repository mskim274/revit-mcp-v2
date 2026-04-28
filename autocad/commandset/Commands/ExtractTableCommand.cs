using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using AutoCADMCP.CommandSet.Interfaces;

namespace AutoCADMCP.CommandSet.Commands
{
    /// <summary>
    /// Extract data from one or more AutoCAD Table entities (the dedicated
    /// `Table` class — `RXClass.GetClass(typeof(Table))`). For Korean
    /// structural drawings: this catches schedules drawn with INSERT TABLE
    /// or imported from Excel via OLE → Convert to AutoCAD Table.
    ///
    /// Does NOT handle Line+Text schedules (see future ExtractGridSchedule)
    /// or BlockReference+Attribute schedules (see future ExtractBlockSchedule).
    ///
    /// Parameters:
    ///   handle      — optional. Specific table entity handle (hex string,
    ///                  e.g. "2A4"). If omitted, all Table entities in
    ///                  model space are extracted.
    ///   header_row  — optional, default 0. Which row contains column
    ///                  headers (0-indexed). Used to label data columns.
    ///   limit       — optional, max number of tables to return (default 5).
    /// </summary>
    public class ExtractTableCommand : ICadCommand
    {
        public string Name => "extract_table";
        public string Category => "Query";

        public Task<CommandResult> ExecuteAsync(
            Database db,
            Transaction tr,
            Dictionary<string, object> parameters,
            CancellationToken cancellationToken)
        {
            try
            {
                var requestedHandle = GetString(parameters, "handle");
                var headerRow = (int)GetLong(parameters, "header_row", 0);
                var limit = (int)Math.Max(1, GetLong(parameters, "limit", 5));

                // Find Table entities in model space.
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                var tables = new List<Dictionary<string, object>>();
                int scanned = 0;
                int matched = 0;

                foreach (ObjectId id in ms)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    scanned++;
                    var ent = tr.GetObject(id, OpenMode.ForRead);
                    if (ent is not Table tbl) continue;

                    if (!string.IsNullOrEmpty(requestedHandle) &&
                        !string.Equals(id.Handle.Value.ToString("X"), requestedHandle, StringComparison.OrdinalIgnoreCase))
                        continue;

                    matched++;
                    if (tables.Count >= limit) continue; // count for total but stop collecting

                    tables.Add(ExtractOne(tbl, id, headerRow, cancellationToken));
                }

                return Task.FromResult(CommandResult.Ok(new Dictionary<string, object>
                {
                    ["tables_found"] = matched,
                    ["tables_returned"] = tables.Count,
                    ["entities_scanned"] = scanned,
                    ["truncated"] = matched > tables.Count,
                    ["tables"] = tables,
                    ["hint"] = matched == 0
                        ? "No Table entities in model space. The schedule may be drawn with Line+Text (use future extract_grid_schedule) or as a Block with Attributes (use future extract_block_schedule)."
                        : null,
                }));
            }
            catch (System.Exception ex)
            {
                return Task.FromResult(CommandResult.Fail(
                    $"extract_table failed: {ex.Message}",
                    "Verify the drawing has AutoCAD Table entities (not Line+Text grids)."));
            }
        }

        private static Dictionary<string, object> ExtractOne(
            Table tbl, ObjectId id, int headerRow, CancellationToken ct)
        {
            int rows = tbl.Rows.Count;
            int cols = tbl.Columns.Count;

            // Header row → column labels.
            var headers = new List<string>();
            if (headerRow >= 0 && headerRow < rows)
            {
                for (int c = 0; c < cols; c++)
                {
                    headers.Add(SafeCellText(tbl, headerRow, c));
                }
            }

            // Data rows = everything below the header row.
            var dataRows = new List<Dictionary<string, object>>();
            int firstDataRow = (headerRow >= 0) ? headerRow + 1 : 0;
            for (int r = firstDataRow; r < rows; r++)
            {
                ct.ThrowIfCancellationRequested();
                var row = new Dictionary<string, object>();
                bool nonEmpty = false;
                for (int c = 0; c < cols; c++)
                {
                    var key = c < headers.Count && !string.IsNullOrWhiteSpace(headers[c])
                        ? headers[c]
                        : $"col_{c}";
                    var value = SafeCellText(tbl, r, c);
                    if (!string.IsNullOrWhiteSpace(value)) nonEmpty = true;
                    row[key] = value;
                }
                if (nonEmpty) dataRows.Add(row);
            }

            return new Dictionary<string, object>
            {
                ["handle"] = id.Handle.Value.ToString("X"),
                ["position"] = new[] { tbl.Position.X, tbl.Position.Y, tbl.Position.Z },
                ["layer"] = tbl.Layer,
                ["rows"] = rows,
                ["columns"] = cols,
                ["header_row_index"] = headerRow,
                ["headers"] = headers,
                ["data"] = dataRows,
                ["data_row_count"] = dataRows.Count,
            };
        }

        // Defensive cell read — Cells[r,c].TextString throws on certain
        // merged-cell or formula configurations. Fall back to Contents
        // when the simple path fails.
        private static string SafeCellText(Table tbl, int row, int col)
        {
            try
            {
                var cell = tbl.Cells[row, col];
                var text = cell.TextString;
                if (!string.IsNullOrEmpty(text)) return StripMTextFormatting(text);

                if (cell.Contents != null && cell.Contents.Count > 0)
                {
                    var first = cell.Contents[0];
                    return StripMTextFormatting(first?.TextString ?? "");
                }
                return "";
            }
            catch
            {
                return "";
            }
        }

        // MText cells return text wrapped in formatting codes like
        // "{\fArial|b0|i0|c129|p34;G1}" — strip them for clean output.
        // Conservative: only handles the common patterns; complex MText
        // (paragraph formatting, fields) may leak through.
        private static string StripMTextFormatting(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return raw;
            var s = raw;
            // \f...; (font), \H...; (height), \C...; (color), \W...; (width), \T...; (tracking)
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\\[fHCWT][^;]*;", "");
            // \p... (paragraph)
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\\p[^;]*;", "");
            // {...} braces (group markers)
            s = s.Replace("{", "").Replace("}", "");
            // \\ → \
            s = s.Replace(@"\\", @"\");
            // \P → newline
            s = s.Replace(@"\P", "\n");
            return s.Trim();
        }

        private static string GetString(Dictionary<string, object> p, string key)
            => p.TryGetValue(key, out var v) && v is string s ? s : null;
        private static long GetLong(Dictionary<string, object> p, string key, long def)
        {
            if (!p.TryGetValue(key, out var v) || v == null) return def;
            return v switch
            {
                long l => l,
                int i => i,
                double d => (long)d,
                _ => def,
            };
        }
    }
}
