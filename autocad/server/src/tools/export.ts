import type { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import type { AcadWebSocketClient } from "../services/websocket-client.js";
import { sendAndFormat } from "../services/response-formatter.js";

const plotPdfSchema = z
  .object({
    layout_name: z
      .string()
      .trim()
      .min(1)
      .optional()
      .describe(
        "Existing layout name; case-insensitive exact match. Omit for AutoCAD's current layout."
      ),
    output_path: z
      .string()
      .trim()
      .min(1)
      .refine((value) => value.toLowerCase().endsWith(".pdf"), {
        message: "output_path must end in .pdf.",
      })
      .optional()
      .describe(
        "Absolute PDF filename. Environment variables such as %TEMP% are expanded. Omit to generate a filename under %TEMP%\\cad-mcp-exports."
      ),
    overwrite: z
      .boolean()
      .optional()
      .default(false)
      .describe("Default false; existing files are preserved unless explicitly true."),
    idempotency_key: z
      .string()
      .trim()
      .min(1)
      .max(512)
      .optional()
      .describe("Stable deduplication key for an identical uncertain retry."),
  })
  .strict();

export function registerExportTools(
  server: McpServer,
  wsClient: AcadWebSocketClient
): void {
  server.registerTool(
    "cad_plot_pdf",
    {
      title: "Plot AutoCAD Layout to PDF",
      description: `Plot AutoCAD's current layout, or an existing layout_name resolved by case-insensitive EXACT match, through DWG To PDF.pc3.

output_path is an optional absolute .pdf filename (environment variables are expanded). When omitted, the tool creates a unique filename under %TEMP%\\cad-mcp-exports. overwrite defaults to false. The plot is written to a unique staging file first, verified to be non-empty, and only then moved onto the requested destination so a failed plot does not destroy an existing PDF.

The result includes the resolved layout, final absolute path, byte size, and verification. Pass idempotency_key and reuse it only with an identical payload after an uncertain timeout.`,
      inputSchema: plotPdfSchema,
      annotations: {
        readOnlyHint: false,
        destructiveHint: false,
        idempotentHint: false,
        openWorldHint: true,
      },
    },
    async (params) =>
      sendAndFormat(
        wsClient,
        "plot_pdf",
        {
          layout_name: params.layout_name,
          output_path: params.output_path,
          overwrite: params.overwrite ?? false,
          idempotency_key: params.idempotency_key,
        },
        120_000,
        { sideEffect: true }
      )
  );
}
