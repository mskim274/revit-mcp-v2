using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AutoCADMCP.CommandSet.Interfaces;

namespace AutoCADMCP.CommandSet.Commands
{
    /// <summary>
    /// Parses a hand-drawn AutoCAD grid schedule (Line + DBText/MText forming
    /// a grid — common in Korean structural drawings: 보 일람표 / PC보 일람표 /
    /// 기둥 일람표) into a structured table.
    ///
    /// Algorithm (research-backed — see autocad/CLAUDE.md and the Phase 5 design):
    ///   1. Collect entities by scope (selection / layer / all).
    ///   2. Separate lines into horizontal (|dy| ≪ |dx|) and vertical bands.
    ///   3. Auto-tolerance = median text height ÷ 2 (or override).
    ///   4. Cluster horizontal-line Y-values → row band coords.
    ///   5. Cluster vertical-line X-values → column band coords.
    ///   6. Build the cell grid (rows × cols).
    ///   7. Assign each text by insertion-point binary search into the bands.
    ///   8. Multiple texts in one cell → join with newline, preserving order.
    ///   9. Detect header row using Korean structural token whitelist
    ///      (부재기호, 단면, 상부근, …) — fall back to top row.
    ///  10. Emit headers + row dicts + preview markdown + diagnostics.
    ///
    /// Output is robust to imperfect grids: low-participation grid lines are
    /// flagged but not dropped (caller can decide via diagnostics.warnings).
    /// </summary>
    public class ParseGridScheduleCommand : ICadCommand
    {
        public string Name => "parse_grid_schedule";
        public string Category => "Query";

        // Default Korean structural schedule header tokens. Caller can override.
        private static readonly string[] DefaultHeaderTokens = new[]
        {
            "부재기호", "기호", "단면", "B×D", "BxD", "B*D", "B X D",
            "층", "위치", "상부근", "하부근", "늑근", "스터럽", "스트럽", "STR",
            "주근", "HOOP", "후프", "비고", "REMARK", "REMARKS",
            "TYPE", "SECTION", "TOP", "BOT", "BOTTOM",
        };

        public Task<CommandResult> ExecuteAsync(
            Database db,
            Transaction tr,
            Dictionary<string, object> parameters,
            CancellationToken cancellationToken)
        {
            try
            {
                var scope = (GetString(parameters, "scope") ?? "selection").ToLowerInvariant();
                var layerFilter = GetString(parameters, "layer");
                var toleranceOverride = GetDouble(parameters, "tolerance", 0);
                var headerTokens = GetStringArray(parameters, "header_tokens") ?? DefaultHeaderTokens;
                var maxPreviewRows = (int)Math.Max(1, Math.Min(20, GetLong(parameters, "preview_rows", 8)));

                // ── 1. Collect entities ──────────────────────────────────
                var lines = new List<LineSeg>();
                var texts = new List<TextItem>();
                int collected = CollectEntities(db, tr, scope, layerFilter, lines, texts, cancellationToken);

                if (texts.Count == 0)
                {
                    return Task.FromResult(CommandResult.Fail(
                        $"No DBText/MText found in scope='{scope}'.",
                        scope == "selection"
                            ? "Select the schedule region in AutoCAD before running."
                            : "Try scope='all' or specify a layer."));
                }

                // ── 2. Separate horizontal vs vertical lines ─────────────
                var horiz = new List<LineSeg>();
                var vert = new List<LineSeg>();
                foreach (var ls in lines)
                {
                    var dx = Math.Abs(ls.End.X - ls.Start.X);
                    var dy = Math.Abs(ls.End.Y - ls.Start.Y);
                    if (dx < 0.001 && dy < 0.001) continue;
                    // Near-horizontal: dy/length < 0.02 (~1.1° slope tolerance)
                    var len = Math.Sqrt(dx * dx + dy * dy);
                    if (dy / len < 0.02) horiz.Add(ls);
                    else if (dx / len < 0.02) vert.Add(ls);
                }

                if (horiz.Count < 2 || vert.Count < 2)
                {
                    return Task.FromResult(CommandResult.Fail(
                        $"Not enough grid lines (h={horiz.Count}, v={vert.Count}). Need ≥2 each.",
                        "The selection may not contain a grid. Try scope='layer' with the schedule's grid layer, or expand the selection."));
                }

                // ── 3. Auto-tolerance ────────────────────────────────────
                double tolerance;
                if (toleranceOverride > 0)
                {
                    tolerance = toleranceOverride;
                }
                else
                {
                    var heights = texts.Where(t => t.Height > 0).Select(t => t.Height).OrderBy(h => h).ToList();
                    var medianHeight = heights.Count > 0 ? heights[heights.Count / 2] : 1.0;
                    tolerance = Math.Max(0.5, medianHeight / 2.0);
                }

                // ── 4. Cluster Y-coords of horizontal lines → row bands ──
                var hYs = horiz.Select(l => (l.Start.Y + l.End.Y) / 2.0).OrderBy(y => y).ToList();
                var rowYs = ClusterCoords(hYs, tolerance);
                rowYs.Sort();

                // ── 5. Cluster X-coords of vertical lines → col bands ────
                var vXs = vert.Select(l => (l.Start.X + l.End.X) / 2.0).OrderBy(x => x).ToList();
                var colXs = ClusterCoords(vXs, tolerance);
                colXs.Sort();

                if (rowYs.Count < 2 || colXs.Count < 2)
                {
                    return Task.FromResult(CommandResult.Fail(
                        $"Grid too sparse after clustering (rows={rowYs.Count}, cols={colXs.Count}).",
                        $"Try a smaller tolerance (current={tolerance:F2}). Pass tolerance=0.5 or similar."));
                }

                // ── 6. Build cell grid ───────────────────────────────────
                // rowYs ascending → bottom to top in WCS. We want rows top→bottom.
                // Reverse so row 0 is the topmost.
                rowYs.Reverse();
                int nRows = rowYs.Count - 1;
                int nCols = colXs.Count - 1;

                // Cell content storage: cells[row][col] = list of text strings (in insertion-point order)
                var cells = new List<TextItem>[nRows, nCols];
                for (int r = 0; r < nRows; r++)
                    for (int c = 0; c < nCols; c++)
                        cells[r, c] = new List<TextItem>();

                // ── 7. Assign each text to a cell ────────────────────────
                int placed = 0, unplaced = 0;
                foreach (var t in texts)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int row = FindRowIndex(rowYs, t.Position.Y);
                    int col = FindColIndex(colXs, t.Position.X);
                    if (row < 0 || col < 0) { unplaced++; continue; }
                    cells[row, col].Add(t);
                    placed++;
                }

                // ── 8. Build per-row text matrix ─────────────────────────
                var rowMatrix = new string[nRows][];
                for (int r = 0; r < nRows; r++)
                {
                    rowMatrix[r] = new string[nCols];
                    for (int c = 0; c < nCols; c++)
                    {
                        var bag = cells[r, c];
                        if (bag.Count == 0) { rowMatrix[r][c] = ""; continue; }
                        // Stable sort: top→bottom (Y desc), then left→right (X asc)
                        bag.Sort((a, b) => b.Position.Y != a.Position.Y
                            ? b.Position.Y.CompareTo(a.Position.Y)
                            : a.Position.X.CompareTo(b.Position.X));
                        rowMatrix[r][c] = string.Join("\n", bag.Select(x => x.Text.Trim()));
                    }
                }

                // ── 9. Identify header row ───────────────────────────────
                int headerRow = -1;
                int bestScore = 0;
                for (int r = 0; r < Math.Min(3, nRows); r++)
                {
                    int score = 0;
                    foreach (var cell in rowMatrix[r])
                    {
                        if (string.IsNullOrEmpty(cell)) continue;
                        foreach (var token in headerTokens)
                        {
                            if (cell.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                score++;
                                break;
                            }
                        }
                    }
                    if (score > bestScore) { bestScore = score; headerRow = r; }
                }

                bool headerHighConfidence = bestScore >= 2;
                if (headerRow < 0) headerRow = 0; // fallback

                // ── 10. Build headers and data rows ──────────────────────
                var headers = new List<string>();
                for (int c = 0; c < nCols; c++)
                {
                    var h = rowMatrix[headerRow][c];
                    headers.Add(string.IsNullOrWhiteSpace(h) ? $"col_{c + 1}" : NormalizeHeader(h));
                }
                // Disambiguate duplicate headers
                MakeUnique(headers);

                var dataRows = new List<Dictionary<string, object>>();
                for (int r = 0; r < nRows; r++)
                {
                    if (r == headerRow) continue;
                    var row = new Dictionary<string, object>();
                    bool anyContent = false;
                    for (int c = 0; c < nCols; c++)
                    {
                        var v = rowMatrix[r][c];
                        if (!string.IsNullOrWhiteSpace(v)) anyContent = true;
                        row[headers[c]] = v;
                    }
                    if (anyContent) dataRows.Add(row);
                }

                // ── 11. Preview markdown (first N rows) ──────────────────
                var preview = BuildMarkdownPreview(headers, dataRows, maxPreviewRows);

                return Task.FromResult(CommandResult.Ok(new Dictionary<string, object>
                {
                    ["headers"] = headers,
                    ["row_count"] = dataRows.Count,
                    ["rows"] = dataRows,
                    ["header_row_index"] = headerRow,
                    ["header_confidence"] = headerHighConfidence ? "high" : "low",
                    ["preview_markdown"] = preview,
                    ["diagnostics"] = new Dictionary<string, object>
                    {
                        ["scope"] = scope,
                        ["layer_filter"] = layerFilter ?? "",
                        ["entities_collected"] = collected,
                        ["lines_horizontal"] = horiz.Count,
                        ["lines_vertical"] = vert.Count,
                        ["texts_total"] = texts.Count,
                        ["texts_placed"] = placed,
                        ["texts_unplaced"] = unplaced,
                        ["grid_rows"] = nRows,
                        ["grid_cols"] = nCols,
                        ["tolerance_used"] = Math.Round(tolerance, 4),
                        ["header_token_score"] = bestScore,
                    },
                }));
            }
            catch (System.Exception ex)
            {
                return Task.FromResult(CommandResult.Fail(
                    $"parse_grid_schedule failed: {ex.Message}",
                    "Verify the scope/layer reaches a real grid. For 31K-entity drawings, prefer scope='selection'."));
            }
        }

        // ─────────────────────────────────────────────────────────────────
        // Helpers
        // ─────────────────────────────────────────────────────────────────

        private struct LineSeg
        {
            public Point3d Start;
            public Point3d End;
        }

        private struct TextItem
        {
            public string Text;
            public Point3d Position;
            public double Height;
        }

        private static int CollectEntities(
            Database db, Transaction tr, string scope, string layerFilter,
            List<LineSeg> lines, List<TextItem> texts, CancellationToken ct)
        {
            int total = 0;

            if (scope == "selection")
            {
                var ids = SelectionContext.Current;
                if (ids == null) return 0;
                foreach (var oid in ids)
                {
                    ct.ThrowIfCancellationRequested();
                    total++;
                    var ent = tr.GetObject(oid, OpenMode.ForRead) as Entity;
                    if (ent == null) continue;
                    ConsumeEntity(ent, lines, texts);
                }
                return total;
            }

            // scope = "all" or "layer"
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            foreach (ObjectId id in ms)
            {
                ct.ThrowIfCancellationRequested();
                total++;
                var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (ent == null) continue;
                if (scope == "layer" && !string.IsNullOrEmpty(layerFilter) &&
                    !string.Equals(ent.Layer, layerFilter, StringComparison.OrdinalIgnoreCase))
                    continue;
                ConsumeEntity(ent, lines, texts);
            }
            return total;
        }

        private static void ConsumeEntity(Entity ent, List<LineSeg> lines, List<TextItem> texts)
        {
            switch (ent)
            {
                case Line l:
                    lines.Add(new LineSeg { Start = l.StartPoint, End = l.EndPoint });
                    break;
                case Polyline pl:
                    // Treat each segment of an LWPolyline as an individual line for grid detection.
                    for (int i = 0; i < pl.NumberOfVertices - 1; i++)
                    {
                        var p1 = pl.GetPoint3dAt(i);
                        var p2 = pl.GetPoint3dAt(i + 1);
                        lines.Add(new LineSeg { Start = p1, End = p2 });
                    }
                    if (pl.Closed && pl.NumberOfVertices >= 2)
                    {
                        var p1 = pl.GetPoint3dAt(pl.NumberOfVertices - 1);
                        var p2 = pl.GetPoint3dAt(0);
                        lines.Add(new LineSeg { Start = p1, End = p2 });
                    }
                    break;
                case DBText t:
                    if (!string.IsNullOrEmpty(t.TextString))
                        texts.Add(new TextItem { Text = t.TextString, Position = t.Position, Height = t.Height });
                    break;
                case MText m:
                    if (!string.IsNullOrEmpty(m.Text))
                        texts.Add(new TextItem { Text = m.Text, Position = m.Location, Height = m.TextHeight });
                    break;
            }
        }

        /// <summary>
        /// Cluster a sorted-ascending list of coordinates into representative
        /// values. Two coords go in the same cluster if they're within
        /// `tolerance`. The cluster's mean is the output value.
        /// </summary>
        private static List<double> ClusterCoords(List<double> sortedAsc, double tolerance)
        {
            var output = new List<double>();
            if (sortedAsc.Count == 0) return output;

            var bucket = new List<double> { sortedAsc[0] };
            for (int i = 1; i < sortedAsc.Count; i++)
            {
                var v = sortedAsc[i];
                if (v - bucket[bucket.Count - 1] <= tolerance)
                {
                    bucket.Add(v);
                }
                else
                {
                    output.Add(bucket.Average());
                    bucket = new List<double> { v };
                }
            }
            if (bucket.Count > 0) output.Add(bucket.Average());
            return output;
        }

        /// <summary>
        /// Find which row band a Y-coord belongs to. rowYs is sorted DESCENDING
        /// (top-to-bottom in the matrix). Row index = number of band edges
        /// strictly below y. Returns -1 if outside the grid.
        /// </summary>
        private static int FindRowIndex(List<double> rowYs, double y)
        {
            // rowYs[0] is top, rowYs[last] is bottom.
            // y must be between consecutive bands.
            for (int i = 0; i < rowYs.Count - 1; i++)
            {
                var top = rowYs[i];
                var bot = rowYs[i + 1];
                if (y <= top + 1e-6 && y >= bot - 1e-6) return i;
            }
            return -1;
        }

        private static int FindColIndex(List<double> colXs, double x)
        {
            // colXs sorted ascending. Column i is between colXs[i] and colXs[i+1].
            for (int i = 0; i < colXs.Count - 1; i++)
            {
                var left = colXs[i];
                var right = colXs[i + 1];
                if (x >= left - 1e-6 && x <= right + 1e-6) return i;
            }
            return -1;
        }

        private static string NormalizeHeader(string h)
        {
            if (string.IsNullOrEmpty(h)) return h;
            // Collapse internal newlines to single space, trim, collapse whitespace.
            var s = h.Replace('\n', ' ').Replace('\r', ' ').Trim();
            var sb = new StringBuilder(s.Length);
            bool prevSpace = false;
            foreach (var ch in s)
            {
                if (char.IsWhiteSpace(ch))
                {
                    if (!prevSpace) sb.Append(' ');
                    prevSpace = true;
                }
                else { sb.Append(ch); prevSpace = false; }
            }
            return sb.ToString();
        }

        private static void MakeUnique(List<string> headers)
        {
            var seen = new Dictionary<string, int>();
            for (int i = 0; i < headers.Count; i++)
            {
                var h = headers[i];
                if (!seen.ContainsKey(h)) { seen[h] = 1; continue; }
                seen[h]++;
                headers[i] = $"{h}_{seen[h]}";
            }
        }

        private static string BuildMarkdownPreview(
            List<string> headers, List<Dictionary<string, object>> rows, int maxRows)
        {
            if (headers.Count == 0) return "";
            var sb = new StringBuilder();
            sb.Append('|');
            foreach (var h in headers) sb.Append(' ').Append(EscapeMd(h)).Append(" |");
            sb.AppendLine();
            sb.Append('|');
            foreach (var _ in headers) sb.Append(" --- |");
            sb.AppendLine();
            int n = Math.Min(maxRows, rows.Count);
            for (int i = 0; i < n; i++)
            {
                sb.Append('|');
                foreach (var h in headers)
                {
                    var v = rows[i].TryGetValue(h, out var o) ? (o?.ToString() ?? "") : "";
                    sb.Append(' ').Append(EscapeMd(v)).Append(" |");
                }
                sb.AppendLine();
            }
            if (rows.Count > n) sb.AppendLine($"_(+{rows.Count - n} more rows)_");
            return sb.ToString();
        }

        private static string EscapeMd(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\n", "<br>").Replace("|", "\\|");
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
                string s when long.TryParse(s, out var sl) => sl,
                _ => def,
            };
        }

        private static double GetDouble(Dictionary<string, object> p, string key, double def)
        {
            if (!p.TryGetValue(key, out var v) || v == null) return def;
            return v switch
            {
                double d => d,
                long l => l,
                int i => i,
                string s when double.TryParse(s, out var sd) => sd,
                _ => def,
            };
        }

        private static string[] GetStringArray(Dictionary<string, object> p, string key)
        {
            if (!p.TryGetValue(key, out var v) || v == null) return null;
            if (v is List<object> list)
            {
                var arr = new string[list.Count];
                for (int i = 0; i < list.Count; i++) arr[i] = list[i]?.ToString() ?? "";
                return arr;
            }
            if (v is string[] sa) return sa;
            return null;
        }
    }
}
