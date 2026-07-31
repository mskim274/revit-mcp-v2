/**
 * Tools for controlling Revit view state and element visibility.
 */

import type { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { BATCH_TIMEOUT_MS } from "@kimminsub/mcp-cad-core";
import type { RevitWebSocketClient } from "../services/websocket-client.js";
import { sendAndFormat } from "../services/response-formatter.js";
import { elementIdSchema, idempotencyKeySchema } from "./shared.js";

const VIEW_ANNOTATIONS = {
  readOnlyHint: false,
  destructiveHint: false,
  idempotentHint: true,
  openWorldHint: false,
} as const;

const DUPLICATE_ANNOTATIONS = {
  readOnlyHint: false,
  destructiveHint: false,
  idempotentHint: false,
  openWorldHint: false,
} as const;

const SET_ACTIVE_VIEW_INPUT_SCHEMA = z
  .object({
    view_name: z
      .string()
      .trim()
      .min(1)
      .optional()
      .describe("View name (exact match first, then partial match)."),
    view_id: elementIdSchema("View ElementId")
      .optional()
      .describe("View ElementId. Takes precedence over view_name."),
    idempotency_key: idempotencyKeySchema(),
  })
  .refine(
    (value) => value.view_id !== undefined || value.view_name !== undefined,
    { message: "Provide view_id or a non-empty view_name." }
  );

const DUPLICATE_VIEWS_INPUT_SCHEMA = z
  .object({
    view_ids: z
      .array(elementIdSchema("View ElementId"))
      .min(1)
      .max(100)
      .optional()
      .describe("Target view ElementIds. Use with or instead of view_names."),
    view_names: z
      .array(z.string().trim().min(1))
      .min(1)
      .max(100)
      .optional()
      .describe(
        "Target names (exact match first, then contains). Templates are excluded."
      ),
    option: z
      .enum(["duplicate", "with_detailing", "as_dependent"])
      .optional()
      .default("duplicate")
      .describe("Duplication mode."),
    name_suffix: z
      .string()
      .max(200)
      .optional()
      .describe(
        "Suffix for each new view name. Name collisions are auto-incremented."
      ),
    activate: z
      .boolean()
      .optional()
      .default(false)
      .describe("Activate the first newly created view."),
    idempotency_key: idempotencyKeySchema(),
  })
  .superRefine((value, ctx) => {
    const targetCount =
      (value.view_ids?.length ?? 0) + (value.view_names?.length ?? 0);
    if (targetCount === 0) {
      ctx.addIssue({
        code: z.ZodIssueCode.custom,
        message: "Provide at least one view_id or view_name.",
      });
    } else if (targetCount > 100) {
      ctx.addIssue({
        code: z.ZodIssueCode.custom,
        message:
          "The combined number of view_ids and view_names cannot exceed 100.",
      });
    }
  });

export function registerViewTools(
  server: McpServer,
  wsClient: RevitWebSocketClient
): void {
  server.registerTool(
    "revit_set_active_view",
    {
      title: "Set Active View",
      description: `Switch Revit to a view by ElementId or name.

Name resolution tries a case-insensitive exact match first, then a partial
match. Use revit_get_views to discover unambiguous names or IDs.`,
      inputSchema: SET_ACTIVE_VIEW_INPUT_SCHEMA,
      annotations: VIEW_ANNOTATIONS,
    },
    async (params) =>
      sendAndFormat(wsClient, "set_active_view", {
        view_name: params.view_name ?? null,
        view_id: params.view_id ?? null,
        idempotency_key: params.idempotency_key,
      })
  );

  server.registerTool(
    "revit_isolate_elements",
    {
      title: "Isolate/Hide Elements in View",
      description: `Temporarily isolate or hide elements in a view.

"isolate" shows only the supplied elements; "hide" hides the supplied
elements. Use revit_reset_view_isolation to restore normal visibility.`,
      inputSchema: {
        element_ids: z
          .array(elementIdSchema("Element ID"))
          .min(1)
          .max(500)
          .describe("ElementIds to isolate or hide (1-500)."),
        mode: z
          .enum(["isolate", "hide"])
          .optional()
          .default("isolate"),
        view_id: elementIdSchema("Target view ElementId")
          .optional()
          .describe("Target view ElementId. Default is the active view."),
        idempotency_key: idempotencyKeySchema(),
      },
      annotations: VIEW_ANNOTATIONS,
    },
    async (params) =>
      sendAndFormat(
        wsClient,
        "isolate_elements",
        {
          element_ids: params.element_ids,
          mode: params.mode ?? "isolate",
          view_id: params.view_id ?? null,
          idempotency_key: params.idempotency_key,
        },
        BATCH_TIMEOUT_MS
      )
  );

  server.registerTool(
    "revit_reset_view_isolation",
    {
      title: "Reset View Isolation",
      description:
        "Reset temporary isolation or hiding in the target view and restore normal visibility.",
      inputSchema: {
        view_id: elementIdSchema("Target view ElementId")
          .optional()
          .describe("Target view ElementId. Default is the active view."),
        idempotency_key: idempotencyKeySchema(),
      },
      annotations: VIEW_ANNOTATIONS,
    },
    async (params) =>
      sendAndFormat(wsClient, "reset_view_isolation", {
        view_id: params.view_id ?? null,
        idempotency_key: params.idempotency_key,
      })
  );

  server.registerTool(
    "revit_select_elements",
    {
      title: "Select Elements",
      description:
        "Select and highlight elements in the Revit UI after a query or review.",
      inputSchema: {
        element_ids: z
          .array(elementIdSchema("Element ID"))
          .min(1)
          .max(500)
          .describe("ElementIds to select (1-500)."),
        idempotency_key: idempotencyKeySchema(),
      },
      annotations: VIEW_ANNOTATIONS,
    },
    async (params) =>
      sendAndFormat(
        wsClient,
        "select_elements",
        {
          element_ids: params.element_ids,
          idempotency_key: params.idempotency_key,
        },
        BATCH_TIMEOUT_MS
      )
  );

  server.registerTool(
    "revit_duplicate_views",
    {
      title: "Duplicate Views (batch)",
      description: `Duplicate up to 100 views in one transaction.

Targets may be IDs and/or names. Name resolution tries exact match first,
then contains, and excludes templates. "with_detailing" includes
view-specific annotations. "as_dependent" is limited to Revit view types that
support dependent duplication; unsupported targets are returned with a reason.

Pass idempotency_key and reuse it if a timeout makes the result uncertain.`,
      inputSchema: DUPLICATE_VIEWS_INPUT_SCHEMA,
      annotations: DUPLICATE_ANNOTATIONS,
    },
    async (params) =>
      sendAndFormat(
        wsClient,
        "duplicate_views",
        {
          view_ids: params.view_ids ?? null,
          view_names: params.view_names ?? null,
          option: params.option ?? "duplicate",
          name_suffix: params.name_suffix ?? null,
          activate: params.activate ?? false,
          idempotency_key: params.idempotency_key,
        },
        BATCH_TIMEOUT_MS
      )
  );
}
