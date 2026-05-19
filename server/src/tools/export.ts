/**
 * Export Tools — read-only data extraction from the Revit model.
 *
 * Tools:
 *   revit_export_schedule — Export a ViewSchedule (일람표) to JSON and/or CSV.
 *
 * Design notes:
 *   - All export tools are read-only on the Revit model. They may write files
 *     to %TEMP% (or user-specified output_dir), but the model is untouched.
 *   - Large schedules can exceed the inline response limit. The shared
 *     `sendAndFormat` helper auto-spills oversized responses to
 *     %TEMP%\revit-mcp-spill\ and returns a preview + file path.
 *   - For Korean projects, CSV defaults to UTF-8 BOM so Excel opens the file
 *     correctly instead of mis-detecting cp949.
 */

import type { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import type { RevitWebSocketClient } from "../services/websocket-client.js";
import { sendAndFormat } from "../services/response-formatter.js";

// Schedule export reads the model but writes files; idempotent over the same
// (schedule, format, output_dir) tuple — calling twice produces the same file.
const SCHEDULE_EXPORT_ANNOTATIONS = {
  readOnlyHint: true,
  destructiveHint: false,
  idempotentHint: true,
  openWorldHint: false,
} as const;

export function registerExportTools(
  server: McpServer,
  wsClient: RevitWebSocketClient
): void {
  // ─── revit_export_schedule ───
  server.registerTool(
    "revit_export_schedule",
    {
      title: "Export ViewSchedule",
      description: `Export a Revit ViewSchedule (일람표) as JSON and/or CSV.

**Resolution:** Provide \`schedule_id\` (preferred) or \`schedule_name\`. Name matching is exact first, then case-insensitive contains — ambiguous names return an error with the candidates.

**Output formats:**
  - \`json\` (default) — Rows returned inline as objects keyed by header. Use \`include_data=false\` to get only metadata (header list + row count) when probing structure.
  - \`csv\` — Writes a UTF-8 BOM CSV to \`output_dir\` (default \`%TEMP%\\revit-mcp-exports\\\`). BOM ensures Excel opens Korean text correctly. Response carries the file path.
  - \`both\` — JSON rows + CSV file.

**Korean / multi-line cells:** Cells are normalized (\\r\\n → \\n, trimmed). CSV escaping follows RFC 4180 (fields with comma/quote/newline are quoted, internal quotes doubled).

**Discovery:** Use \`revit_get_views(view_type="Schedule")\` first to list schedules.

**Verification (Harness Tier 1):** After writing CSV, re-reads the file and reports actual vs expected line count in the \`verification\` block. Use it to detect partial writes or encoding mismatches.

Examples:
  - \`revit_export_schedule(schedule_name="RC Beam Schedule")\` → JSON with all rows
  - \`revit_export_schedule(schedule_id=12345, format="both")\` → JSON + CSV file
  - \`revit_export_schedule(schedule_name="Walls", format="csv", output_dir="C:\\\\reports")\` → CSV only`,
      inputSchema: {
        schedule_name: z
          .string()
          .optional()
          .describe(
            "Schedule view name (exact or case-insensitive contains match). Either this or schedule_id is required."
          ),
        schedule_id: z
          .number()
          .int()
          .optional()
          .describe(
            "Schedule view ElementId. Takes priority over schedule_name when both are provided."
          ),
        format: z
          .enum(["json", "csv", "both"])
          .optional()
          .default("json")
          .describe(
            "Output format. json = inline rows (default), csv = file only, both = inline rows + CSV file."
          ),
        include_data: z
          .boolean()
          .optional()
          .default(true)
          .describe(
            "Include row data in JSON response. Set false to get only headers + counts (probe mode)."
          ),
        output_dir: z
          .string()
          .optional()
          .describe(
            "Directory for the CSV file. Default %TEMP%\\revit-mcp-exports\\. Created if missing."
          ),
        csv_encoding: z
          .enum(["utf8-bom", "utf8"])
          .optional()
          .default("utf8-bom")
          .describe(
            "CSV file encoding. utf8-bom (default) for Excel on Korean Windows; utf8 for plain UTF-8 without BOM."
          ),
      },
      annotations: SCHEDULE_EXPORT_ANNOTATIONS,
    },
    async (params) => {
      return sendAndFormat(wsClient, "export_schedule", {
        schedule_name: params.schedule_name ?? null,
        schedule_id: params.schedule_id ?? null,
        format: params.format ?? "json",
        include_data: params.include_data ?? true,
        output_dir: params.output_dir ?? null,
        csv_encoding: params.csv_encoding ?? "utf8-bom",
      });
    }
  );
}
