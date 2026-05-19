using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using RevitMCP.CommandSet.Interfaces;

namespace RevitMCP.CommandSet.Commands.Export
{
    /// <summary>
    /// Export a Revit ViewSchedule (일람표) to JSON and/or CSV.
    ///
    /// Parameters:
    ///   schedule_name   (string, optional) — Schedule view name (exact or contains match).
    ///   schedule_id     (int,    optional) — ElementId of the schedule view. Takes priority over name.
    ///   format          (string, optional) — "json" | "csv" | "both" (default "json").
    ///   include_data    (bool,   optional) — Include row data in JSON response (default true).
    ///                                         Set false to get only headers + counts.
    ///   output_dir      (string, optional) — Directory for CSV file. Defaults to
    ///                                         %TEMP%\revit-mcp-exports\.
    ///   csv_encoding    (string, optional) — "utf8-bom" (default, Excel-friendly Korean) or "utf8".
    ///
    /// Behavior:
    ///   - Reads schedule via ViewSchedule.GetTableData() — preserves the visible
    ///     filtered/sorted state including grouping/totals shown in Revit.
    ///   - CSV escaping: RFC 4180 (quotes around fields containing comma/quote/newline,
    ///     doubled internal quotes).
    ///   - UTF-8 BOM by default so Excel on Korean Windows opens the file without
    ///     cp949 mis-detection (한글 깨짐 방지).
    ///
    /// Harness Tier 1:
    ///   - Read-only on the Revit model — no idempotency cache, no transaction.
    ///   - Post-export verification: file exists, file size > 0, line count matches
    ///     expected row count (header + body).
    /// </summary>
    public class ExportScheduleCommand : IRevitCommand
    {
        public string Name => "export_schedule";
        public string Category => "Export";

        public Task<CommandResult> ExecuteAsync(
            Document doc,
            Dictionary<string, object> parameters,
            CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                // ─── Resolve schedule view ───
                var schedule = ResolveSchedule(doc, parameters, out var failReason);
                if (schedule == null)
                {
                    var available = ListAvailableSchedules(doc, 8);
                    return Task.FromResult(CommandResult.Fail(
                        failReason,
                        $"Available schedules ({available.Count}): {string.Join(", ", available.Take(8))}. " +
                        "Use revit_get_views(view_type=\"Schedule\") for the full list. " +
                        "schedule_name supports exact and contains match (case-insensitive)."));
                }

                // ─── Read table data ───
                var table = schedule.GetTableData();
                var bodySection = table.GetSectionData(SectionType.Body);
                var headerSection = table.GetSectionData(SectionType.Header);

                if (bodySection == null)
                {
                    return Task.FromResult(CommandResult.Fail(
                        $"Schedule '{schedule.Name}' has no body section.",
                        "The schedule may be empty or a title-block revision schedule (not exportable)."));
                }

                int firstCol = bodySection.FirstColumnNumber;
                int lastCol = bodySection.LastColumnNumber;
                int firstBodyRow = bodySection.FirstRowNumber;
                int lastBodyRow = bodySection.LastRowNumber;
                int colCount = lastCol - firstCol + 1;
                int bodyRowCount = lastBodyRow - firstBodyRow + 1;

                // Headers come from the header section. Different schedules put labels
                // on different header rows (multi-row headers exist). We take the last
                // header row as the column label row — that's the convention Revit's
                // own UI shows above the body.
                var headers = new List<string>(colCount);
                if (headerSection != null && headerSection.LastRowNumber >= headerSection.FirstRowNumber)
                {
                    int labelRow = headerSection.LastRowNumber;
                    for (int c = firstCol; c <= lastCol; c++)
                    {
                        var label = SafeGetCellText(schedule, SectionType.Header, labelRow, c);
                        headers.Add(NormalizeCell(label));
                    }
                }
                else
                {
                    for (int c = firstCol; c <= lastCol; c++)
                        headers.Add($"Col{c - firstCol + 1}");
                }

                // Body rows
                cancellationToken.ThrowIfCancellationRequested();
                var rows = new List<List<string>>(bodyRowCount);
                for (int r = firstBodyRow; r <= lastBodyRow; r++)
                {
                    if ((r - firstBodyRow) % 200 == 0)
                        cancellationToken.ThrowIfCancellationRequested();
                    var row = new List<string>(colCount);
                    for (int c = firstCol; c <= lastCol; c++)
                        row.Add(NormalizeCell(SafeGetCellText(schedule, SectionType.Body, r, c)));
                    rows.Add(row);
                }

                // ─── Options ───
                var format = (parameters.TryGetValue("format", out var fObj) ? fObj?.ToString() : null)?.ToLowerInvariant() ?? "json";
                if (format != "json" && format != "csv" && format != "both")
                    format = "json";

                var includeData = !parameters.TryGetValue("include_data", out var idObj)
                    || idObj == null
                    || Convert.ToBoolean(idObj);

                var outputDir = parameters.TryGetValue("output_dir", out var odObj)
                    ? odObj?.ToString() : null;
                if (string.IsNullOrWhiteSpace(outputDir))
                    outputDir = Path.Combine(Path.GetTempPath(), "revit-mcp-exports");

                var encoding = (parameters.TryGetValue("csv_encoding", out var encObj) ? encObj?.ToString() : null)?.ToLowerInvariant();
                bool withBom = encoding != "utf8"; // default utf8-bom

                // ─── Write CSV if requested ───
                string csvPath = null;
                Dictionary<string, object> verification = null;
                if (format == "csv" || format == "both")
                {
                    Directory.CreateDirectory(outputDir);
                    var safeName = SanitizeFileName(schedule.Name);
                    csvPath = Path.Combine(outputDir, safeName + ".csv");

                    var enc = withBom ? new UTF8Encoding(true) : new UTF8Encoding(false);
                    using (var writer = new StreamWriter(csvPath, false, enc))
                    {
                        writer.WriteLine(string.Join(",", headers.Select(CsvEscape)));
                        foreach (var row in rows)
                            writer.WriteLine(string.Join(",", row.Select(CsvEscape)));
                    }

                    // ─── Harness Tier 1: post-export verification ───
                    var info = new FileInfo(csvPath);
                    int expectedLines = 1 + rows.Count; // header + body
                    int actualLines = -1;
                    try { actualLines = File.ReadAllLines(csvPath, Encoding.UTF8).Length; }
                    catch { /* swallow — report -1 */ }

                    verification = new Dictionary<string, object>
                    {
                        ["performed"] = true,
                        ["file_exists"] = info.Exists,
                        ["file_size_bytes"] = info.Exists ? info.Length : 0,
                        ["expected_lines"] = expectedLines,
                        ["actual_lines"] = actualLines,
                        ["line_count_match"] = actualLines == expectedLines,
                        ["encoding"] = withBom ? "utf8-bom" : "utf8",
                    };
                }

                // ─── Build result ───
                var category = ResolveCategoryName(doc, schedule);
                var result = new Dictionary<string, object>
                {
                    ["schedule_name"] = schedule.Name,
                    ["view_id"] = schedule.Id.IntegerValue,
                    ["category"] = category,
                    ["column_count"] = colCount,
                    ["row_count"] = rows.Count,
                    ["headers"] = headers,
                };

                if (csvPath != null) result["csv_path"] = csvPath;

                if (format == "json" || format == "both")
                {
                    if (includeData)
                    {
                        // Rows as list of dicts keyed by header. Duplicate headers
                        // become "Header (2)", "Header (3)" so JSON dict stays valid.
                        var uniqueHeaders = DisambiguateHeaders(headers);
                        var rowsAsDicts = new List<Dictionary<string, object>>(rows.Count);
                        foreach (var row in rows)
                        {
                            var d = new Dictionary<string, object>(colCount);
                            for (int i = 0; i < colCount; i++)
                                d[uniqueHeaders[i]] = row[i];
                            rowsAsDicts.Add(d);
                        }
                        result["rows"] = rowsAsDicts;
                    }
                    else
                    {
                        result["rows_omitted"] = true;
                        result["note"] = "include_data=false — only metadata returned. Set include_data=true or use format=csv for the full payload.";
                    }
                }

                if (verification != null) result["verification"] = verification;

                return Task.FromResult(CommandResult.Ok(result));
            }
            catch (OperationCanceledException)
            {
                return Task.FromResult(CommandResult.Fail(
                    "Export cancelled (timeout or user abort).",
                    "Retry with a more specific schedule, or use revit_get_views(view_type=\"Schedule\") to discover smaller schedules first."));
            }
            catch (Exception ex)
            {
                return Task.FromResult(CommandResult.Fail(
                    $"Failed to export schedule: {ex.Message}",
                    "Ensure the document is open and the schedule view is valid. " +
                    "For title-block revision schedules use a regular schedule view instead."));
            }
        }

        // ─── Helpers ───────────────────────────────────────────────────────

        private static ViewSchedule ResolveSchedule(
            Document doc,
            Dictionary<string, object> parameters,
            out string failReason)
        {
            failReason = null;

            // 1. By ID (priority)
            if (parameters != null && parameters.TryGetValue("schedule_id", out var idObj) && idObj != null)
            {
                if (TryParseInt(idObj, out var idInt))
                {
                    var elem = doc.GetElement(new ElementId(idInt));
                    if (elem is ViewSchedule vs) return vs;
                    failReason = $"Element id {idInt} is not a ViewSchedule (got {elem?.GetType().Name ?? "null"}).";
                    return null;
                }
                failReason = $"Invalid schedule_id: {idObj}";
                return null;
            }

            // 2. By name (exact, then contains)
            var name = parameters?.TryGetValue("schedule_name", out var nObj) == true ? nObj?.ToString() : null;
            if (string.IsNullOrWhiteSpace(name))
            {
                failReason = "Provide schedule_name or schedule_id.";
                return null;
            }

            var allSchedules = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSchedule))
                .Cast<ViewSchedule>()
                .Where(v => !v.IsTemplate && !v.IsTitleblockRevisionSchedule)
                .ToList();

            var exact = allSchedules.FirstOrDefault(s =>
                s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;

            var contains = allSchedules
                .Where(s => s.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();
            if (contains.Count == 1) return contains[0];
            if (contains.Count > 1)
            {
                failReason = $"Schedule name '{name}' is ambiguous — matches {contains.Count}: " +
                    string.Join(", ", contains.Take(5).Select(s => $"\"{s.Name}\""));
                return null;
            }

            failReason = $"Schedule '{name}' not found.";
            return null;
        }

        private static List<string> ListAvailableSchedules(Document doc, int limit)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSchedule))
                .Cast<ViewSchedule>()
                .Where(v => !v.IsTemplate && !v.IsTitleblockRevisionSchedule)
                .OrderBy(v => v.Name)
                .Take(limit)
                .Select(v => v.Name)
                .ToList();
        }

        /// <summary>
        /// Get the schedule's category name (e.g. "Walls", "Multi-Category", "Keynote Legend").
        /// </summary>
        private static string ResolveCategoryName(Document doc, ViewSchedule schedule)
        {
            try
            {
                var defn = schedule.Definition;
                if (defn == null) return null;
                var catId = defn.CategoryId;
                if (catId == null || catId.IntegerValue == -1) return "Multi-Category";
                // Use fully-qualified name — the IRevitCommand interface exposes a
                // string property called `Category` which would shadow the Revit
                // Category class inside this file.
                var cat = Autodesk.Revit.DB.Category.GetCategory(doc, catId);
                return cat?.Name;
            }
            catch { return null; }
        }

        /// <summary>
        /// Safely read a cell text. Some schedule sections raise on out-of-range
        /// indices — return empty rather than crashing the export.
        /// </summary>
        private static string SafeGetCellText(ViewSchedule schedule, SectionType section, int row, int col)
        {
            try { return schedule.GetCellText(section, row, col) ?? string.Empty; }
            catch { return string.Empty; }
        }

        /// <summary>
        /// Strip control characters and collapse internal newlines to "\n" so the
        /// cell stays parseable in CSV and JSON. Preserves leading/trailing spaces
        /// only after collapsing.
        /// </summary>
        private static string NormalizeCell(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            // Multi-line cells in schedules use \r\n. Keep as \n for CSV/JSON.
            s = s.Replace("\r\n", "\n").Replace("\r", "\n");
            return s.Trim();
        }

        private static string CsvEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            bool needsQuoting = s.IndexOfAny(new[] { ',', '"', '\n' }) >= 0;
            var v = s.Replace("\"", "\"\"");
            return needsQuoting ? "\"" + v + "\"" : v;
        }

        private static string SanitizeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);
            foreach (var ch in name)
                sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
            return sb.ToString();
        }

        /// <summary>
        /// If two columns share the same header text (rare but possible — e.g. a
        /// schedule that shows the same parameter in two places), the JSON dict
        /// would collide. Suffix duplicates with " (2)", " (3)", ...
        /// </summary>
        private static List<string> DisambiguateHeaders(List<string> headers)
        {
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            var result = new List<string>(headers.Count);
            foreach (var h in headers)
            {
                var key = string.IsNullOrEmpty(h) ? "(unnamed)" : h;
                if (seen.TryGetValue(key, out var n))
                {
                    seen[key] = n + 1;
                    result.Add($"{key} ({n + 1})");
                }
                else
                {
                    seen[key] = 1;
                    result.Add(key);
                }
            }
            return result;
        }

        private static bool TryParseInt(object obj, out int value)
        {
            value = 0;
            if (obj == null) return false;
            try { value = Convert.ToInt32(obj); return true; }
            catch { return false; }
        }
    }
}
