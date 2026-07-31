import type { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import type { AcadWebSocketClient } from "../services/websocket-client.js";
import { sendAndFormat } from "../services/response-formatter.js";

const pointSchema = z
  .array(z.number().finite())
  .min(2)
  .max(3);

export function registerCreateTools(
  server: McpServer,
  wsClient: AcadWebSocketClient
): void {
  server.registerTool(
    "cad_create_line",
    {
      title: "Create AutoCAD Line",
      description: `Add a Line entity to model space between two points.

Coordinates are in the drawing's units (check cad_get_drawing_info → units.insertion to know what unit you're working in). Each point must contain exactly two or three finite numbers; Z is optional and defaults to 0.

If 'layer' is specified, that layer must already exist — this tool does not create layers. Use cad_get_layers to verify, or omit 'layer' to draw on the current layer.

Response includes a post-commit 'verification' block. The plugin reopens the committed ObjectId in a new read transaction and reports commit_verified, geometry/layer matches, actual coordinates, and length.`,
      inputSchema: {
        start: pointSchema
          .describe("Start point as [x, y] or [x, y, z]"),
        end: pointSchema
          .describe("End point as [x, y] or [x, y, z]"),
        layer: z.string().trim().min(1).optional()
          .describe("Target layer name. Must exist. Defaults to current layer."),
        idempotency_key: z
          .string()
          .trim()
          .min(1)
          .max(512)
          .optional()
          .describe(
            "Stable deduplication key. Reuse the exact same key and payload when retrying an uncertain result."
          ),
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
      idempotency_key: params.idempotency_key,
    })
  );
}
