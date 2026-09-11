import type { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import type { AcadWebSocketClient } from "../services/websocket-client.js";
import { sendAndFormat } from "../services/response-formatter.js";

const pointSchema = z
  .array(z.number().finite())
  .min(2)
  .max(3);

const layerSchema = z.string().trim().min(1).optional()
  .describe("Target layer name. Must already exist. Defaults to the current layer.");

const lineEntitySchema = z.object({
  type: z.literal("line"),
  start: pointSchema.describe("Start point as [x, y] or [x, y, z]."),
  end: pointSchema.describe("End point as [x, y] or [x, y, z]."),
  layer: layerSchema,
});

const polylineEntitySchema = z.object({
  type: z.literal("polyline"),
  points: z.array(pointSchema).min(2).max(10_000)
    .describe("Polyline vertices in drawing units. Two-dimensional and three-dimensional point lists are supported."),
  closed: z.boolean().optional().default(false),
  layer: layerSchema,
});

const circleEntitySchema = z.object({
  type: z.literal("circle"),
  center: pointSchema.describe("Circle center as [x, y] or [x, y, z]."),
  radius: z.number().finite().positive().describe("Positive radius in drawing units."),
  layer: layerSchema,
});

const arcEntitySchema = z.object({
  type: z.literal("arc"),
  center: pointSchema.describe("Arc center as [x, y] or [x, y, z]."),
  radius: z.number().finite().positive().describe("Positive radius in drawing units."),
  start_angle_deg: z.number().finite().describe("Start angle in degrees."),
  end_angle_deg: z.number().finite().describe("End angle in degrees."),
  layer: layerSchema,
});

const textEntitySchema = z.object({
  type: z.literal("text"),
  position: pointSchema.describe("Insertion point as [x, y] or [x, y, z]."),
  contents: z.string().min(1).describe("Plain single-line DBText contents."),
  height: z.number().finite().positive().optional()
    .describe("Positive text height in drawing units. Defaults to the drawing TEXTSIZE."),
  layer: layerSchema,
});

const entitySchema = z.discriminatedUnion("type", [
  lineEntitySchema,
  polylineEntitySchema,
  circleEntitySchema,
  arcEntitySchema,
  textEntitySchema,
]);

const idempotencyKeySchema = z
  .string()
  .trim()
  .min(1)
  .max(512)
  .optional()
  .describe(
    "Stable deduplication key. Reuse the exact same key and payload when retrying an uncertain result."
  );

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
        layer: layerSchema,
        idempotency_key: idempotencyKeySchema,
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

  server.registerTool(
    "cad_create_entities",
    {
      title: "Create AutoCAD Entities in One Batch",
      description: `Create up to 200 model-space entities in one AutoCAD transaction. Supported item types are line, polyline, circle, arc, and text. Invalid items are reported individually while valid items still commit.

All coordinates, radii, and text heights use the drawing's own units; call cad_get_drawing_info and inspect units.insertion before creating geometry. Arc angles are DEGREES, measured counter-clockwise in the WCS XY plane from +X and converted to AutoCAD radians internally. Polyline vertices may be 2D or 3D; mixed-Z vertices create a 3D polyline. Text creates plain single-line DBText and defaults height to the drawing TEXTSIZE.

Each optional layer must already exist. Omit layer to use the drawing's current layer. The response reports every item, created handles, failures, and a post-commit verification that reopens the first successful entity and counts resolvable committed handles. Use this batch tool instead of looping over cad_create_line; cad_create_line remains available for compatibility.`,
      inputSchema: {
        entities: z.array(entitySchema).min(1).max(200)
          .describe("One to 200 entity specifications, processed in array order."),
        idempotency_key: idempotencyKeySchema,
      },
      annotations: {
        readOnlyHint: false,
        destructiveHint: false,
        idempotentHint: false,
        openWorldHint: false,
      },
    },
    async (params) => sendAndFormat(wsClient, "create_entities", {
      entities: params.entities,
      idempotency_key: params.idempotency_key,
    })
  );
}
