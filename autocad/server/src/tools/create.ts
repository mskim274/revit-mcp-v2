import type { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import type { AcadWebSocketClient } from "../services/websocket-client.js";
import { sendAndFormat } from "../services/response-formatter.js";

export function registerCreateTools(
  server: McpServer,
  wsClient: AcadWebSocketClient
): void {
  server.registerTool(
    "cad_create_line",
    {
      title: "Create AutoCAD Line",
      description: `Add a Line entity to model space between two points.

Coordinates are in the drawing's units (check cad_get_drawing_info → units.insertion to know what unit you're working in). Z coordinate is optional and defaults to 0.

If 'layer' is specified, that layer must already exist — this tool does not create layers. Use cad_get_layers to verify, or omit 'layer' to draw on the current layer.

Response includes a 'verification' block confirming the actual start/end points and length match the request (Tier 1 harness pattern).`,
      inputSchema: {
        start: z.array(z.number()).min(2).max(3)
          .describe("Start point as [x, y] or [x, y, z]"),
        end: z.array(z.number()).min(2).max(3)
          .describe("End point as [x, y] or [x, y, z]"),
        layer: z.string().optional()
          .describe("Target layer name. Must exist. Defaults to current layer."),
      },
      annotations: {
        readOnlyHint: false,
        destructiveHint: false,  // adding a line isn't destructive
        idempotentHint: false,
        openWorldHint: false,
      },
    },
    async (params) => sendAndFormat(wsClient, "create_line", {
      start: params.start,
      end: params.end,
      layer: params.layer,
    })
  );
}
