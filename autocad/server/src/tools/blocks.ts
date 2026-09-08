import type { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import type { AcadWebSocketClient } from "../services/websocket-client.js";
import { sendAndFormat } from "../services/response-formatter.js";

const pointSchema = z
  .array(z.number().finite())
  .min(2)
  .max(3)
  .describe("WCS insertion point [x,y] or [x,y,z] in drawing units.");

const insertionSchema = z
  .object({
    point: pointSchema,
    scale: z
      .number()
      .finite()
      .positive()
      .optional()
      .default(1)
      .describe("Positive uniform scale; default 1."),
    rotation_deg: z
      .number()
      .finite()
      .optional()
      .default(0)
      .describe("Counter-clockwise WCS rotation in degrees; default 0."),
  })
  .strict();

const idempotencyKeySchema = z
  .string()
  .trim()
  .min(1)
  .max(512)
  .optional();

const blocksSchema = z
  .object({
    op: z.enum(["list", "insert"]).optional().default("list"),
    block_name: z
      .string()
      .trim()
      .min(1)
      .optional()
      .describe("Loaded block definition name; case-insensitive exact match."),
    insertions: z
      .array(insertionSchema)
      .min(1)
      .max(50)
      .optional()
      .describe("One to 50 placements of the same loaded block definition."),
    idempotency_key: idempotencyKeySchema.describe(
      "Stable deduplication key for an identical insert retry after an uncertain outcome."
    ),
  })
  .strict()
  .superRefine((value, ctx) => {
    if (value.op === "insert") {
      if (value.block_name === undefined) {
        ctx.addIssue({
          code: z.ZodIssueCode.custom,
          path: ["block_name"],
          message: "block_name is required when op='insert'.",
        });
      }
      if (value.insertions === undefined) {
        ctx.addIssue({
          code: z.ZodIssueCode.custom,
          path: ["insertions"],
          message: "insertions is required when op='insert'.",
        });
      }
    } else if (
      value.block_name !== undefined ||
      value.insertions !== undefined ||
      value.idempotency_key !== undefined
    ) {
      ctx.addIssue({
        code: z.ZodIssueCode.custom,
        message:
          "op='list' accepts no block_name, insertions, or idempotency_key.",
      });
    }
  });

export function registerBlockTools(
  server: McpServer,
  wsClient: AcadWebSocketClient
): void {
  server.registerTool(
    "cad_blocks",
    {
      title: "List or Insert AutoCAD Blocks",
      description: `List insertable block definitions or batch-insert one already-loaded definition.

op="list" (default) returns each non-layout, non-anonymous, non-xref definition name and its top-level reference count across model space and all paper layouts; nested references are intentionally excluded. It never loads a DWG.

op="insert" requires block_name plus 1-50 insertions. block_name uses case-insensitive EXACT matching. Each point is WCS [x,y] or [x,y,z] in drawing units; scale is a positive uniform factor (default 1), and rotation_deg is counter-clockwise about WCS +Z (default 0). Inserts go into AutoCAD's current model/paper space in one dispatcher-owned transaction, report per-item failures, instantiate default non-constant attributes, and verify committed references. Attribute value editing is outside this tool; use the script escape hatch for advanced cases.

Pass idempotency_key for insert and reuse it only with an identical payload after an uncertain timeout.`,
      inputSchema: blocksSchema,
      annotations: {
        readOnlyHint: false,
        destructiveHint: false,
        idempotentHint: false,
        openWorldHint: false,
      },
    },
    async (params) => {
      if (params.op === "list") {
        return sendAndFormat(wsClient, "blocks", { op: "list" });
      }
      return sendAndFormat(
        wsClient,
        "blocks",
        {
          op: "insert",
          block_name: params.block_name,
          insertions: params.insertions,
          idempotency_key: params.idempotency_key,
        },
        60_000,
        { sideEffect: true }
      );
    }
  );
}
