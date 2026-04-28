import type { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import type { AcadWebSocketClient } from "../services/websocket-client.js";
import { sendAndFormat } from "../services/response-formatter.js";

export function registerQueryTools(
  server: McpServer,
  wsClient: AcadWebSocketClient
): void {
  server.registerTool(
    "cad_get_drawing_info",
    {
      title: "Get AutoCAD Drawing Info",
      description: `Drawing-level metadata: AutoCAD version, document path, units (insertion units, angle base), model-space extents (or null on empty drawings), layout names.

Cheap read — no per-entity iteration. Run this at the start of a session to confirm what drawing the user is in.`,
      inputSchema: {},
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async () => sendAndFormat(wsClient, "get_drawing_info")
  );

  server.registerTool(
    "cad_get_layers",
    {
      title: "List AutoCAD Layers",
      description: `Enumerate the drawing's layer table. Each layer reports name, color (ACI index or RGB), linetype, frozen/locked/off/plottable flags, and whether it's the current layer.

Use before cad_create_line / cad_create_* to verify the target layer exists.`,
      inputSchema: {},
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async () => sendAndFormat(wsClient, "get_layers")
  );

  server.registerTool(
    "cad_query_entities",
    {
      title: "Search AutoCAD Entities",
      description: `Search model-space entities by type and/or layer. Defaults to summary mode (counts grouped by type and by layer) — use this first on unfamiliar drawings.

Set summary_only=false for paginated detail (default 50 per page, max 200). Each detail row includes id, type, layer, color, plus type-specific extras (start/end for lines, center/radius for circles, text for text, position+name for block references).

For large drawings (10K+ entities), always start with summary_only=true and a specific entity_type filter.`,
      inputSchema: {
        entity_type: z.string().optional()
          .describe("DXF class name to filter (e.g. 'Line', 'Circle', 'BlockReference', 'MText'). Case-insensitive. Omit for all."),
        layer: z.string().optional()
          .describe("Exact layer name. Case-insensitive. Omit for all layers."),
        summary_only: z.boolean().optional().default(true)
          .describe("True (default): counts only. False: paginated detail."),
        limit: z.number().int().min(1).max(200).optional()
          .describe("Page size when summary_only=false. Default 50, max 200."),
        offset: z.number().int().min(0).optional()
          .describe("Pagination offset. Default 0."),
      },
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async (params) => sendAndFormat(wsClient, "query_entities", params)
  );
}
