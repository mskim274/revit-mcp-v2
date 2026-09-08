import type { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import type { AcadWebSocketClient } from "../services/websocket-client.js";
import { sendAndFormat } from "../services/response-formatter.js";

const pointSchema = z.array(z.number().finite()).min(2).max(3);
const vectorSchema = z.array(z.number().finite()).min(2).max(3);
const idempotencyKeySchema = z.string().trim().min(1).max(512).optional();

const modifyEntitiesSchema = z
  .object({
    op: z.enum(["move", "copy", "erase", "rotate"]),
    handles: z.array(z.string().trim().min(1)).min(1).max(500)
      .describe("One to 500 decimal handle strings exactly as returned in cad_query_entities.handle (hex with A-F or a 0x prefix is also accepted)."),
    vector: vectorSchema.optional()
      .describe("Required for move/copy: [dx, dy] or [dx, dy, dz] in drawing units."),
    origin: pointSchema.optional()
      .describe("Required for rotate: [x, y] or [x, y, z] in drawing units."),
    angle_deg: z.number().finite().optional()
      .describe("Required for rotate. Counter-clockwise degrees about WCS +Z."),
    idempotency_key: idempotencyKeySchema.describe(
      "Stable deduplication key for an identical retry after an uncertain result."
    ),
  })
  .superRefine((value, ctx) => {
    if ((value.op === "move" || value.op === "copy") && value.vector === undefined) {
      ctx.addIssue({
        code: z.ZodIssueCode.custom,
        path: ["vector"],
        message: `vector is required when op='${value.op}'.`,
      });
    }
    if (value.op === "rotate") {
      if (value.origin === undefined) {
        ctx.addIssue({
          code: z.ZodIssueCode.custom,
          path: ["origin"],
          message: "origin is required when op='rotate'.",
        });
      }
      if (value.angle_deg === undefined) {
        ctx.addIssue({
          code: z.ZodIssueCode.custom,
          path: ["angle_deg"],
          message: "angle_deg is required when op='rotate'.",
        });
      }
    }
  });

export function registerModifyTools(
  server: McpServer,
  wsClient: AcadWebSocketClient
): void {
  server.registerTool(
    "cad_modify_entities",
    {
      title: "Modify AutoCAD Entities in One Batch",
      description: `Move, copy, erase, or rotate up to 500 entities in one AutoCAD transaction. Pass decimal handle strings in the same format returned as handle (and id) by cad_query_entities; hexadecimal strings containing A-F or using a 0x prefix are also accepted. Duplicate, missing, erased, locked-layer, or otherwise invalid entities are never silently dropped: each input receives a success or skipped result with a reason.

move/copy require vector=[dx,dy] or [dx,dy,dz] in drawing units. rotate requires origin plus angle_deg; angles are counter-clockwise DEGREES about WCS +Z. erase permanently erases the selected database entities when the transaction commits. Successful items commit even when other items fail.

The response reports mutated_count and post-commit verification. For move/rotate it reopens a mutated source handle, for copy it reopens a new handle, and for erase it verifies that a sample erased handle no longer exists. Reuse the same idempotency_key only for an identical uncertain retry.`,
      inputSchema: modifyEntitiesSchema,
      annotations: {
        readOnlyHint: false,
        destructiveHint: true,
        idempotentHint: false,
        openWorldHint: false,
      },
    },
    async (params) => sendAndFormat(wsClient, "modify_entities", {
      op: params.op,
      handles: params.handles,
      vector: params.vector,
      origin: params.origin,
      angle_deg: params.angle_deg,
      idempotency_key: params.idempotency_key,
    })
  );
}
