/**
 * Create Tools — Tools for creating new Revit elements.
 *
 * Tools:
 *   revit_create_wall      — Create a straight wall between two points
 *   revit_create_floor     — Create a floor from a rectangle or polygon
 *   revit_create_pipe_run  — Create a connected pipe run (survey coords) + elbows
 */

import type { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { BATCH_TIMEOUT_MS } from "@kimminsub/mcp-cad-core";
import type { RevitWebSocketClient } from "../services/websocket-client.js";
import { sendAndFormat } from "../services/response-formatter.js";
import { elementIdSchema, idempotencyKeySchema } from "./shared.js";

// Shared annotations for creation tools
const CREATE_ANNOTATIONS = {
  readOnlyHint: false,
  destructiveHint: false,
  idempotentHint: false,
  openWorldHint: false,
} as const;

const FINITE_NUMBER = z.number().finite();
const FLOOR_POINT = z
  .object({ x: FINITE_NUMBER, y: FINITE_NUMBER })
  .strict();
const SURVEY_PIPE_POINT = z
  .object({ e: FINITE_NUMBER, n: FINITE_NUMBER, z: FINITE_NUMBER })
  .strict();
const INTERNAL_PIPE_POINT = z
  .object({ x: FINITE_NUMBER, y: FINITE_NUMBER, z: FINITE_NUMBER })
  .strict();

const FLOOR_INPUT_SCHEMA = z
  .object({
    min_x: FINITE_NUMBER.optional().describe("Rectangle minimum X (feet)"),
    min_y: FINITE_NUMBER.optional().describe("Rectangle minimum Y (feet)"),
    max_x: FINITE_NUMBER.optional().describe("Rectangle maximum X (feet)"),
    max_y: FINITE_NUMBER.optional().describe("Rectangle maximum Y (feet)"),
    points: z
      .array(FLOOR_POINT)
      .min(3)
      .max(2000)
      .optional()
      .describe(
        "Polygon points [{x, y}, ...] in feet. Use instead of rectangle min/max."
      ),
    level_name: z
      .string()
      .trim()
      .min(1)
      .optional()
      .describe("Level name (default: lowest level)"),
    floor_type: z
      .string()
      .trim()
      .min(1)
      .optional()
      .describe(
        "Floor type name (exact match first, then case-insensitive contains; default: first available)"
      ),
    structural: z
      .boolean()
      .optional()
      .default(false)
      .describe("Is structural floor (default: false)"),
    idempotency_key: idempotencyKeySchema(),
  })
  .superRefine((value, ctx) => {
    const rectangle = [value.min_x, value.min_y, value.max_x, value.max_y];
    const rectangleCount = rectangle.filter((item) => item !== undefined).length;
    const hasPolygon = value.points !== undefined;

    if (!hasPolygon && rectangleCount !== 4) {
      ctx.addIssue({
        code: z.ZodIssueCode.custom,
        message:
          "Provide either points (3-2000 polygon vertices) or all four rectangle bounds: min_x, min_y, max_x, max_y.",
      });
    }
    if (hasPolygon && rectangleCount > 0) {
      ctx.addIssue({
        code: z.ZodIssueCode.custom,
        message:
          "Choose exactly one floor boundary mode: points OR rectangle min/max, not both.",
      });
    }
    if (
      rectangleCount === 4 &&
      (value.min_x! >= value.max_x! || value.min_y! >= value.max_y!)
    ) {
      ctx.addIssue({
        code: z.ZodIssueCode.custom,
        message: "Rectangle max_x/max_y must be greater than min_x/min_y.",
      });
    }
  });

const PIPE_RUN_INPUT_SCHEMA = z
  .object({
    points: z
      .array(z.union([SURVEY_PIPE_POINT, INTERNAL_PIPE_POINT]))
      .min(2)
      .max(500)
      .describe(
        "Ordered vertices. Survey mode: [{e,n,z}, ...]. Internal mode: [{x,y,z}, ...]."
      ),
    coordinate_mode: z
      .enum(["survey", "internal"])
      .optional()
      .default("survey")
      .describe(
        '"survey" (default) uses project shared coordinates; "internal" uses raw Revit feet.'
      ),
    input_unit: z
      .enum(["m", "mm", "ft"])
      .optional()
      .describe(
        'Survey coordinates accept "m" (default) or "mm". Internal coordinates are raw Revit feet and accept only "ft".'
      ),
    pipe_type: z
      .union([
        z.string().trim().min(1),
        z.number().int().positive().refine(Number.isSafeInteger),
      ])
      .optional()
      .describe(
        "PipeType ElementId or name. Names resolve by case-insensitive exact match first, then only by a unique contains match; ambiguous names are rejected. Default: first available type."
      ),
    system_type_id: elementIdSchema("PipingSystemType ElementId")
      .optional()
      .describe("PipingSystemType ElementId. Default: first found."),
    diameter_mm: z
      .number()
      .finite()
      .positive()
      .optional()
      .describe("Pipe diameter in mm (e.g. 250). Default: type default."),
    level_name: z
      .string()
      .trim()
      .min(1)
      .optional()
      .describe("Reference level name. Default: nearest level by average elevation."),
    connect_elbows: z
      .boolean()
      .optional()
      .default(true)
      .describe("Insert elbow fittings at interior vertices (default true)."),
    allow_identity_transform: z
      .boolean()
      .optional()
      .default(false)
      .describe(
        "Survey mode only: allow an all-zero project transform after verifying that survey and internal coordinates intentionally coincide."
      ),
    idempotency_key: idempotencyKeySchema(),
  })
  .superRefine((value, ctx) => {
    const mode = value.coordinate_mode ?? "survey";
    value.points.forEach((point, index) => {
      if (mode === "survey" && !("e" in point && "n" in point)) {
        ctx.addIssue({
          code: z.ZodIssueCode.custom,
          path: ["points", index],
          message: "Survey-mode points must have exactly {e, n, z}.",
        });
      }
      if (mode === "internal" && !("x" in point && "y" in point)) {
        ctx.addIssue({
          code: z.ZodIssueCode.custom,
          path: ["points", index],
          message: "Internal-mode points must have exactly {x, y, z}.",
        });
      }
    });

    if (mode === "internal" && value.input_unit && value.input_unit !== "ft") {
      ctx.addIssue({
        code: z.ZodIssueCode.custom,
        path: ["input_unit"],
        message:
          'coordinate_mode="internal" is raw Revit feet; omit input_unit or use "ft".',
      });
    }
    if (mode === "survey" && value.input_unit === "ft") {
      ctx.addIssue({
        code: z.ZodIssueCode.custom,
        path: ["input_unit"],
        message: 'Survey mode accepts input_unit "m" or "mm", not "ft".',
      });
    }
  });

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
        start_x: FINITE_NUMBER.describe("Start point X coordinate (feet)"),
        start_y: FINITE_NUMBER.describe("Start point Y coordinate (feet)"),
        end_x: FINITE_NUMBER.describe("End point X coordinate (feet)"),
        end_y: FINITE_NUMBER.describe("End point Y coordinate (feet)"),
        level_name: z
          .string()
          .trim()
          .min(1)
          .optional()
          .describe("Level name (default: lowest level)"),
        wall_type: z
          .string()
          .trim()
          .min(1)
          .optional()
          .describe(
            "Wall type name (exact match first, then case-insensitive contains; default: first available)"
          ),
        height: z
          .number()
          .finite()
          .positive()
          .optional()
          .default(10)
          .describe("Wall height in feet (default: 10)"),
        structural: z
          .boolean()
          .optional()
          .default(false)
          .describe("Is structural wall (default: false)"),
        idempotency_key: idempotencyKeySchema(),
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
        idempotency_key: params.idempotency_key,
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
      inputSchema: FLOOR_INPUT_SCHEMA,
      annotations: CREATE_ANNOTATIONS,
    },
    async (params) => {
      return sendAndFormat(wsClient, "create_floor", {
        min_x: params.min_x,
        min_y: params.min_y,
        max_x: params.max_x,
        max_y: params.max_y,
        points: params.points,
        level_name: params.level_name ?? null,
        floor_type: params.floor_type ?? null,
        structural: params.structural ?? false,
        idempotency_key: params.idempotency_key,
      });
    }
  );

  // ─── revit_create_pipe_run ───
  server.registerTool(
    "revit_create_pipe_run",
    {
      title: "Create Pipe Run (survey coords + elbows)",
      description: `Create a connected run of pipes through a list of points, with elbow fittings auto-inserted at each vertex. Built for the CAD→Revit workflow: feed survey/spot-elevation coordinates straight from a drawing.

**Project-portable coordinates.** With coordinate_mode="survey" (default), points are shared/survey coordinates. The tool reads THIS document's project location at runtime and converts them — so the same survey points land correctly in ANY project that has Shared Coordinates set up. The rotation sign is auto-detected per project (no hard-coded transform). Switch to coordinate_mode="internal" to pass raw Revit feet.

**Safety:** an all-zero project transform is ambiguous, so survey mode fails closed unless allow_identity_transform=true is explicitly supplied after verification. Elevation-only transforms are supported. Post-transaction verification checks the first point's 3D round-trip error (<1 cm), the requested diameter, and requested elbow creation in verification.match.

Examples:
  - From CAD spot elevations (meters):
    create_pipe_run(points=[{e:500000,n:200000,z:100},{e:500010,n:200000,z:100}], pipe_type="Domestic Water", diameter_mm=250)
  - Internal feet, no elbows:
    create_pipe_run(points=[{x:0,y:0,z:10},{x:20,y:0,z:10}], coordinate_mode="internal", connect_elbows=false)`,
      inputSchema: PIPE_RUN_INPUT_SCHEMA,
      annotations: CREATE_ANNOTATIONS,
    },
    async (params) => {
      const coordinateMode = params.coordinate_mode ?? "survey";
      return sendAndFormat(
        wsClient,
        "create_pipe_run",
        {
          points: params.points,
          coordinate_mode: coordinateMode,
          input_unit:
            coordinateMode === "internal"
              ? "ft"
              : (params.input_unit ?? "m"),
          pipe_type: params.pipe_type ?? null,
          system_type_id: params.system_type_id ?? null,
          diameter_mm: params.diameter_mm ?? null,
          level_name: params.level_name ?? null,
          connect_elbows: params.connect_elbows ?? true,
          allow_identity_transform: params.allow_identity_transform ?? false,
          idempotency_key: params.idempotency_key,
        },
        BATCH_TIMEOUT_MS
      );
    }
  );
}
