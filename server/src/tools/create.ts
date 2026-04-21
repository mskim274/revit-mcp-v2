/**
 * Create Tools — Tools for creating new Revit elements.
 *
 * Tools:
 *   revit_create_wall  — Create a straight wall between two points
 *   revit_create_floor — Create a floor from a rectangle or polygon
 */

import type { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import type { RevitWebSocketClient } from "../services/websocket-client.js";
import { sendAndFormat } from "../services/response-formatter.js";

// Shared annotations for creation tools
const CREATE_ANNOTATIONS = {
  readOnlyHint: false,
  destructiveHint: false,
  idempotentHint: false,
  openWorldHint: false,
} as const;

export function registerCreateTools(
  server: McpServer,
  wsClient: RevitWebSocketClient
): void {
  // ─── revit_create_wall ───
  server.registerTool(
    "revit_create_wall",
    {
      title: "Create Wall",
      description: `Create a straight wall between two points in the Revit model.

All coordinates are in feet. The wall is placed on the specified level with the given height.

Use revit_get_levels to find available levels and revit_get_types_by_category(category="Walls") to find wall types.

If level_name is omitted, uses the lowest level. If wall_type is omitted, uses the first available type.

Example: Create a 20ft wall on Level 1:
  create_wall(start_x=0, start_y=0, end_x=20, end_y=0, level_name="Level 1", height=10)`,
      inputSchema: {
        start_x: z.number().describe("Start point X coordinate (feet)"),
        start_y: z.number().describe("Start point Y coordinate (feet)"),
        end_x: z.number().describe("End point X coordinate (feet)"),
        end_y: z.number().describe("End point Y coordinate (feet)"),
        level_name: z
          .string()
          .optional()
          .describe("Level name (default: lowest level)"),
        wall_type: z
          .string()
          .optional()
          .describe("Wall type name (default: first available)"),
        height: z
          .number()
          .optional()
          .default(10)
          .describe("Wall height in feet (default: 10)"),
        structural: z
          .boolean()
          .optional()
          .default(false)
          .describe("Is structural wall (default: false)"),
      },
      annotations: CREATE_ANNOTATIONS,
    },
    async (params) => {
      return sendAndFormat(wsClient, "create_wall", {
        start_x: params.start_x,
        start_y: params.start_y,
        end_x: params.end_x,
        end_y: params.end_y,
        level_name: params.level_name ?? null,
        wall_type: params.wall_type ?? null,
        height: params.height ?? 10,
        structural: params.structural ?? false,
      });
    }
  );

  // ─── revit_create_floor ───
  server.registerTool(
    "revit_create_floor",
    {
      title: "Create Floor",
      description: `Create a floor in the Revit model from a rectangular boundary or polygon points.

**Rectangle mode:** Provide min_x, min_y, max_x, max_y to create a rectangular floor.
**Polygon mode:** Provide points array with {x, y} objects to create an arbitrary polygon floor.

All coordinates are in feet. Use revit_get_levels for level names, revit_get_types_by_category(category="Floors") for floor types.

Example (rectangle):
  create_floor(min_x=0, min_y=0, max_x=30, max_y=20, level_name="Level 1")

Example (polygon):
  create_floor(points=[{x:0,y:0}, {x:30,y:0}, {x:30,y:20}, {x:0,y:20}], level_name="Level 1")`,
      inputSchema: {
        min_x: z.number().optional().describe("Rectangle minimum X (feet)"),
        min_y: z.number().optional().describe("Rectangle minimum Y (feet)"),
        max_x: z.number().optional().describe("Rectangle maximum X (feet)"),
        max_y: z.number().optional().describe("Rectangle maximum Y (feet)"),
        points: z
          .array(z.object({ x: z.number(), y: z.number() }))
          .optional()
          .describe("Polygon points array [{x, y}, ...] (feet) — use instead of min/max for non-rectangular floors"),
        level_name: z
          .string()
          .optional()
          .describe("Level name (default: lowest level)"),
        floor_type: z
          .string()
          .optional()
          .describe("Floor type name (default: first available)"),
        structural: z
          .boolean()
          .optional()
          .default(false)
          .describe("Is structural floor (default: false)"),
      },
      annotations: CREATE_ANNOTATIONS,
    },
    async (params) => {
      return sendAndFormat(wsClient, "create_floor", {
        min_x: params.min_x ?? null,
        min_y: params.min_y ?? null,
        max_x: params.max_x ?? null,
        max_y: params.max_y ?? null,
        points: params.points ?? null,
        level_name: params.level_name ?? null,
        floor_type: params.floor_type ?? null,
        structural: params.structural ?? false,
      });
    }
  );
}
