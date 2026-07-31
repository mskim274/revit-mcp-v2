/**
 * Review-aid tools for view overrides and bulk tag creation.
 */

import type { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { BATCH_TIMEOUT_MS } from "@kimminsub/mcp-cad-core";
import type { RevitWebSocketClient } from "../services/websocket-client.js";
import { sendAndFormat } from "../services/response-formatter.js";
import {
  elementIdSchema,
  idempotencyKeySchema,
  type ElementIdInput,
} from "./shared.js";

const SELECTOR_FIELDS = {
  element_ids: z
    .array(elementIdSchema("Element ID"))
    .min(1)
    .max(5000)
    .optional()
    .describe(
      "Explicit ElementIds. When supplied, they take priority over filter fields."
    ),
  category: z
    .string()
    .trim()
    .min(1)
    .optional()
    .describe('Category name, for example "Walls" or "StructuralColumns".'),
  type_name_contains: z
    .string()
    .trim()
    .min(1)
    .optional()
    .describe("Case-insensitive substring match on type name."),
  type_name_starts_with: z
    .string()
    .trim()
    .min(1)
    .optional()
    .describe("Case-insensitive prefix match on type name."),
  mark_contains: z
    .string()
    .trim()
    .min(1)
    .optional()
    .describe("Case-insensitive substring match on the instance Mark."),
  parameter_name: z
    .string()
    .trim()
    .min(1)
    .optional()
    .describe("Parameter name. Must be paired with parameter_value_contains."),
  parameter_value_contains: z
    .string()
    .trim()
    .min(1)
    .optional()
    .describe(
      "Case-insensitive substring match on the parameter display value. Must be paired with parameter_name."
    ),
  level_name: z
    .string()
    .trim()
    .min(1)
    .optional()
    .describe("Exact level-name match."),
} as const;

type SelectorInput = {
  element_ids?: ElementIdInput[];
  category?: string;
  type_name_contains?: string;
  type_name_starts_with?: string;
  mark_contains?: string;
  parameter_name?: string;
  parameter_value_contains?: string;
  level_name?: string;
};

function validateSelector(
  value: SelectorInput,
  ctx: z.RefinementCtx
): void {
  const hasParameterName = value.parameter_name !== undefined;
  const hasParameterValue = value.parameter_value_contains !== undefined;
  if (hasParameterName !== hasParameterValue) {
    ctx.addIssue({
      code: z.ZodIssueCode.custom,
      message:
        "parameter_name and parameter_value_contains must be provided together.",
      path: hasParameterName
        ? ["parameter_value_contains"]
        : ["parameter_name"],
    });
  }

  const hasSelector =
    value.element_ids !== undefined ||
    value.category !== undefined ||
    value.type_name_contains !== undefined ||
    value.type_name_starts_with !== undefined ||
    value.mark_contains !== undefined ||
    (hasParameterName && hasParameterValue) ||
    value.level_name !== undefined;
  if (!hasSelector) {
    ctx.addIssue({
      code: z.ZodIssueCode.custom,
      message:
        "Provide at least one selector: element_ids, category, type name, mark, parameter pair, or level_name.",
    });
  }
}

const APPLY_COLOR_INPUT_SCHEMA = z
  .object({
    view_id: elementIdSchema("Target view ElementId")
      .optional()
      .describe("Target view ElementId. Default is the active view."),
    mode: z.enum(["apply", "clear"]).optional().default("apply"),
    ...SELECTOR_FIELDS,
    max_elements: z
      .number()
      .int()
      .min(1)
      .max(50_000)
      .optional()
      .default(5000)
      .describe("Maximum matched elements; truncation is reported."),
    color: z
      .string()
      .trim()
      .min(1)
      .max(40)
      .optional()
      .describe(
        'Preset red/orange/yellow/green/blue/magenta/cyan/gray or an "r,g,b" triple.'
      ),
    surface_fill: z.boolean().optional().default(true),
    transparency: z
      .number()
      .int()
      .min(0)
      .max(100)
      .optional()
      .default(0),
    halftone: z.boolean().optional().default(false),
    idempotency_key: idempotencyKeySchema(),
  })
  .strict()
  .superRefine(validateSelector);

const TAG_BY_FILTER_INPUT_SCHEMA = z
  .object({
    view_id: elementIdSchema("Target graphical view ElementId")
      .optional()
      .describe("Target graphical view ElementId. Default is the active view."),
    ...SELECTOR_FIELDS,
    element_ids: z
      .array(elementIdSchema("Element ID"))
      .min(1)
      .max(500)
      .optional()
      .describe(
        "Explicit ElementIds (max 500). When supplied, they take priority over filter fields."
      ),
    max_elements: z
      .number()
      .int()
      .min(1)
      .max(500)
      .optional()
      .default(500)
      .describe("Maximum matched elements; truncation is reported."),
    tag_type_id: elementIdSchema("Optional tag FamilySymbol ElementId")
      .optional()
      .describe(
        'Optional tag FamilySymbol ElementId. Compatible only with tag_mode="ByCategory".'
      ),
    has_leader: z.boolean().optional().default(false),
    orientation: z
      .enum(["Horizontal", "Vertical"])
      .optional()
      .default("Horizontal"),
    offset_x_feet: z.number().finite().optional().default(0),
    offset_y_feet: z.number().finite().optional().default(0),
    tag_mode: z
      .enum(["ByCategory", "Multicategory", "Material"])
      .optional()
      .default("ByCategory"),
    idempotency_key: idempotencyKeySchema(),
  })
  .strict()
  .superRefine(validateSelector)
  .superRefine((value, ctx) => {
    if (
      value.tag_type_id !== undefined &&
      value.tag_mode !== "ByCategory"
    ) {
      ctx.addIssue({
        code: z.ZodIssueCode.custom,
        path: ["tag_mode"],
        message:
          'tag_type_id can only be combined with tag_mode="ByCategory". Omit tag_type_id for Multicategory or Material tags.',
      });
    }
  });

const APPLY_OVERRIDE_ANNOTATIONS = {
  readOnlyHint: false,
  destructiveHint: false,
  idempotentHint: true,
  openWorldHint: false,
} as const;

const TAG_ANNOTATIONS = {
  readOnlyHint: false,
  destructiveHint: false,
  idempotentHint: false,
  openWorldHint: false,
} as const;

export function registerVisualizeTools(
  server: McpServer,
  wsClient: RevitWebSocketClient
): void {
  server.registerTool(
    "revit_apply_color_filter",
    {
      title: "Apply Color Filter (View Override)",
      description: `Apply or clear view-specific graphics overrides for a validated selector.

Provide at least one selector. parameter_name and parameter_value_contains must
be supplied together. Explicit element_ids take priority over other filters.
Color accepts a documented preset or an RGB triple such as "255,128,0".
The response verifies the first affected element after the transaction.`,
      inputSchema: APPLY_COLOR_INPUT_SCHEMA,
      annotations: APPLY_OVERRIDE_ANNOTATIONS,
    },
    async (params) =>
      sendAndFormat(
        wsClient,
        "apply_color_filter",
        {
          view_id: params.view_id ?? null,
          mode: params.mode ?? "apply",
          element_ids: params.element_ids ?? null,
          category: params.category ?? null,
          type_name_contains: params.type_name_contains ?? null,
          type_name_starts_with: params.type_name_starts_with ?? null,
          mark_contains: params.mark_contains ?? null,
          parameter_name: params.parameter_name ?? null,
          parameter_value_contains:
            params.parameter_value_contains ?? null,
          level_name: params.level_name ?? null,
          max_elements: params.max_elements ?? 5000,
          color: params.color ?? "red",
          surface_fill: params.surface_fill ?? true,
          transparency: params.transparency ?? 0,
          halftone: params.halftone ?? false,
          idempotency_key: params.idempotency_key,
        },
        BATCH_TIMEOUT_MS
      )
  );

  server.registerTool(
    "revit_tag_by_filter",
    {
      title: "Tag Elements by Filter",
      description: `Create IndependentTag elements for a validated selector in one transaction.

Provide at least one selector. parameter_name and parameter_value_contains must
be supplied together. Explicit element_ids take priority. If tag_type_id is
omitted, Revit resolves the default type for the requested tag_mode. An explicit
tag_type_id is compatible only with tag_mode="ByCategory". Re-running can create
duplicate tags, so pass and reuse idempotency_key for timeout-safe retries.`,
      inputSchema: TAG_BY_FILTER_INPUT_SCHEMA,
      annotations: TAG_ANNOTATIONS,
    },
    async (params) =>
      sendAndFormat(
        wsClient,
        "tag_by_filter",
        {
          view_id: params.view_id ?? null,
          element_ids: params.element_ids ?? null,
          category: params.category ?? null,
          type_name_contains: params.type_name_contains ?? null,
          type_name_starts_with: params.type_name_starts_with ?? null,
          mark_contains: params.mark_contains ?? null,
          parameter_name: params.parameter_name ?? null,
          parameter_value_contains:
            params.parameter_value_contains ?? null,
          level_name: params.level_name ?? null,
          max_elements: params.max_elements ?? 500,
          tag_type_id: params.tag_type_id ?? null,
          has_leader: params.has_leader ?? false,
          orientation: params.orientation ?? "Horizontal",
          offset_x_feet: params.offset_x_feet ?? 0,
          offset_y_feet: params.offset_y_feet ?? 0,
          tag_mode: params.tag_mode ?? "ByCategory",
          idempotency_key: params.idempotency_key,
        },
        BATCH_TIMEOUT_MS
      )
  );
}
