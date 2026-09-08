/**
 * Schedule and view-image export tools.
 */

import type { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { EXPORT_TIMEOUT_MS } from "@kimminsub/mcp-cad-core";
import type { RevitWebSocketClient } from "../services/websocket-client.js";
import { sendAndFormat } from "../services/response-formatter.js";
import { elementIdSchema, idempotencyKeySchema } from "./shared.js";

// Export commands create files and can replace one only when the caller
// explicitly opts into overwrite, so they are not read-only tools.
const EXPORT_ANNOTATIONS = {
  readOnlyHint: false,
  destructiveHint: true,
  idempotentHint: true,
  openWorldHint: false,
} as const;

const EXPORT_SCHEDULE_INPUT_SCHEMA = z
  .object({
    schedule_name: z
      .string()
      .trim()
      .min(1)
      .optional()
      .describe(
        "Schedule name (case-insensitive exact match first, then contains)."
      ),
    schedule_id: elementIdSchema("Schedule view ElementId")
      .optional()
      .describe("Schedule view ElementId. Takes precedence over schedule_name."),
    format: z.enum(["json", "csv", "both"]).optional().default("json"),
    include_data: z.boolean().optional().default(true),
    max_rows: z
      .number()
      .int()
      .min(1)
      .max(200_000)
      .optional()
      .default(50_000)
      .describe(
        "Maximum schedule body rows to export (default 50000, maximum 200000)."
      ),
    output_dir: z
      .string()
      .trim()
      .min(1)
      .optional()
      .describe(
        "CSV directory. Default is %TEMP%\\revit-mcp-exports\\. The directory is created if missing."
      ),
    csv_encoding: z
      .enum(["utf8-bom", "utf8"])
      .optional()
      .default("utf8-bom"),
    overwrite: z
      .boolean()
      .optional()
      .default(false)
      .describe(
        "Allow replacing an existing CSV. Default false refuses replacement."
      ),
    idempotency_key: idempotencyKeySchema(),
  })
  .refine(
    (value) =>
      value.schedule_id !== undefined || value.schedule_name !== undefined,
    { message: "Provide schedule_id or a non-empty schedule_name." }
  );

const EXPORT_VIEW_INPUT_SCHEMA = z
  .object({
    view_name: z
      .string()
      .trim()
      .min(1)
      .optional()
      .describe(
        "View name (case-insensitive exact match first, then unambiguous contains). Omit with view_id to export the active view."
      ),
    view_id: elementIdSchema("View ElementId")
      .optional()
      .describe(
        "View ElementId. Takes precedence over view_name; omit both target fields to export the active view."
      ),
    format: z.enum(["png", "jpg"]).optional().default("png"),
    output_dir: z
      .string()
      .trim()
      .min(1)
      .optional()
      .describe(
        "Image directory. Default is %TEMP%\\revit-mcp-exports\\. The directory is created if missing."
      ),
    overwrite: z
      .boolean()
      .optional()
      .default(false)
      .describe(
        "Allow replacing an existing image with the same view-derived filename. Default false refuses replacement."
      ),
    idempotency_key: idempotencyKeySchema(),
  })
  .strict();

export function registerExportTools(
  server: McpServer,
  wsClient: RevitWebSocketClient
): void {
  server.registerTool(
    "revit_export_schedule",
    {
      title: "Export ViewSchedule",
      description: `Export a Revit ViewSchedule as inline JSON, CSV, or both.

Resolve the schedule by ElementId or name. Name matching is exact first, then
case-insensitive contains; ambiguous matches fail with candidates.

CSV defaults to UTF-8 with BOM for Excel compatibility. Export is capped by
max_rows (50,000 by default; 200,000 maximum). File replacement is refused
unless overwrite=true. Because a timeout can occur after a file was
written, pass idempotency_key and reuse the same key before retrying. The
response verifies the written file's size and line count.`,
      inputSchema: EXPORT_SCHEDULE_INPUT_SCHEMA,
      annotations: EXPORT_ANNOTATIONS,
    },
    async (params) =>
      sendAndFormat(
        wsClient,
        "export_schedule",
        {
          schedule_name: params.schedule_name ?? null,
          schedule_id: params.schedule_id ?? null,
          format: params.format ?? "json",
          include_data: params.include_data ?? true,
          max_rows: params.max_rows ?? 50_000,
          output_dir: params.output_dir ?? null,
          csv_encoding: params.csv_encoding ?? "utf8-bom",
          overwrite: params.overwrite ?? false,
          idempotency_key: params.idempotency_key,
        },
        EXPORT_TIMEOUT_MS
      )
  );

  server.registerTool(
    "revit_export_view",
    {
      title: "Export Revit View Image",
      description: `Export one non-template Revit view to PNG (default) or JPG.

Omit view_id and view_name to export the active view. A supplied ID takes precedence over a name; name matching is case-insensitive exact first, then contains, and ambiguous matches fail with candidates. Specified views are exported directly with Document.ExportImage and do not require changing the Revit UI's active view.

Files default to %TEMP%\\revit-mcp-exports\\. overwrite=false refuses replacement. The response verifies that the final file exists and has non-zero size. Because a timeout can occur after a file is written, pass idempotency_key and reuse the same key before retrying.`,
      inputSchema: EXPORT_VIEW_INPUT_SCHEMA,
      annotations: EXPORT_ANNOTATIONS,
    },
    async (params) =>
      sendAndFormat(
        wsClient,
        "export_view",
        {
          view_name: params.view_name ?? null,
          view_id: params.view_id ?? null,
          format: params.format ?? "png",
          output_dir: params.output_dir ?? null,
          overwrite: params.overwrite ?? false,
          idempotency_key: params.idempotency_key,
        },
        EXPORT_TIMEOUT_MS
      )
  );
}
