/**
 * Modify Tools — Tools for modifying existing Revit elements.
 *
 * Tools:
 *   revit_modify_element_parameter — Set a parameter value on an element
 *   revit_batch_modify_parameters  — Set many parameters in ONE transaction
 *   revit_delete_elements          — Delete one or more elements
 *   revit_move_elements            — Move elements by a translation vector
 *   revit_copy_elements            — Copy elements by a translation vector
 *   revit_duplicate_type           — Duplicate an ElementType under a new name
 *   revit_rename_type              — Rename an ElementType
 *   revit_change_instance_type     — Reassign instances to a different type
 */

import type { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { BATCH_TIMEOUT_MS } from "@kimminsub/mcp-cad-core";
import type { RevitWebSocketClient } from "../services/websocket-client.js";
import { sendAndFormat } from "../services/response-formatter.js";
import {
  elementIdSchema,
  idempotencyKeySchema,
  VALUE_MODE_OVERRIDE_SCHEMA,
  VALUE_MODE_SCHEMA,
} from "./shared.js";

// Shared annotations for modification tools
const MODIFY_ANNOTATIONS = {
  readOnlyHint: false,
  destructiveHint: false,
  idempotentHint: false,
  openWorldHint: false,
} as const;

const DESTRUCTIVE_ANNOTATIONS = {
  readOnlyHint: false,
  destructiveHint: true,
  idempotentHint: false,
  openWorldHint: false,
} as const;

const PARAMETER_VALUE_SCHEMA = z.union([
  z.string(),
  z.number().finite(),
  z.boolean(),
]);

const BATCH_MODIFICATION_SCHEMA = z
  .object({
    element_id: elementIdSchema("Element ID"),
    parameter_name: z.string().trim().min(1).describe("Parameter name"),
    value: PARAMETER_VALUE_SCHEMA.describe("New value"),
    is_type_param: z
      .boolean()
      .optional()
      .describe("Set on the element's type instead (default false)"),
    value_mode: VALUE_MODE_OVERRIDE_SCHEMA.describe(
      "Optional per-item override. If omitted, the top-level value_mode applies."
    ),
  })
  .strict();

const BATCH_MODIFY_INPUT_SCHEMA = z
  .object({
    modifications: z
      .array(BATCH_MODIFICATION_SCHEMA)
      .min(1)
      .max(5000)
      .optional()
      .describe("Shape A: explicit per-element modifications (max 5000)"),
    element_ids: z
      .array(elementIdSchema("Element ID"))
      .min(1)
      .max(5000)
      .optional()
      .describe("Shape B: element IDs to stamp (combine with 'parameters')"),
    parameters: z
      .record(z.string().trim().min(1), PARAMETER_VALUE_SCHEMA)
      .refine((value) => Object.keys(value).length > 0, {
        message: "parameters must contain at least one name/value pair",
      })
      .optional()
      .describe(
        'Shape B: name/value map applied to every element, e.g. {"Comments": "Reviewed"}'
      ),
    value_mode: VALUE_MODE_SCHEMA,
    only_if_empty: z
      .boolean()
      .optional()
      .default(false)
      .describe("Only set parameters that currently have no value (never overwrite)"),
    idempotency_key: idempotencyKeySchema(),
  })
  .superRefine((value, ctx) => {
    const hasA = value.modifications !== undefined;
    const hasIds = value.element_ids !== undefined;
    const hasParameters = value.parameters !== undefined;

    if (hasA && (hasIds || hasParameters)) {
      ctx.addIssue({
        code: z.ZodIssueCode.custom,
        message:
          "Use exactly one batch shape: modifications OR element_ids + parameters.",
      });
    } else if (!hasA && !(hasIds && hasParameters)) {
      ctx.addIssue({
        code: z.ZodIssueCode.custom,
        message:
          "Provide modifications, or provide both element_ids and parameters.",
      });
    }

    if (hasIds && hasParameters) {
      const totalSets =
        value.element_ids!.length * Object.keys(value.parameters!).length;
      if (totalSets > 5000) {
        ctx.addIssue({
          code: z.ZodIssueCode.custom,
          message: `Shape B expands to ${totalSets} parameter sets; maximum is 5000.`,
        });
      }
    }
  });

export function registerModifyTools(
  server: McpServer,
  wsClient: RevitWebSocketClient
): void {
  // ─── revit_modify_element_parameter ───
  server.registerTool(
    "revit_modify_element_parameter",
    {
      title: "Modify Element Parameter",
      description: `Set a parameter value on a Revit element.

Supports string, number, and boolean values. Works with both instance parameters and type parameters.

Use revit_get_element_info first to see available parameters and their current values.

Examples:
  - Set mark: modify_element_parameter(element_id=12345, parameter_name="Mark", value="A-101")
  - Set comments: modify_element_parameter(element_id=12345, parameter_name="Comments", value="Updated by MCP")
  - Set type param: modify_element_parameter(element_id=12345, parameter_name="Width", value=0.5, is_type_param=true)`,
      inputSchema: {
        element_id: elementIdSchema("The Revit element ID to modify"),
        parameter_name: z
          .string()
          .trim()
          .min(1)
          .describe("Name of the parameter to set"),
        value: PARAMETER_VALUE_SCHEMA.describe("New value for the parameter"),
        value_mode: VALUE_MODE_SCHEMA,
        is_type_param: z
          .boolean()
          .optional()
          .default(false)
          .describe("Set on the element's type instead of instance (default: false)"),
        idempotency_key: idempotencyKeySchema(),
      },
      annotations: MODIFY_ANNOTATIONS,
    },
    async (params) => {
      return sendAndFormat(wsClient, "modify_element_parameter", {
        element_id: params.element_id,
        parameter_name: params.parameter_name,
        value: params.value,
        value_mode: params.value_mode ?? "internal",
        is_type_param: params.is_type_param ?? false,
        idempotency_key: params.idempotency_key,
      });
    }
  );

  // ─── revit_delete_elements ───
  server.registerTool(
    "revit_delete_elements",
    {
      title: "Delete Elements",
      description: `Delete one or more Revit elements by their IDs.

⚠️ This is a destructive operation. Deleting an element may also remove dependent elements (e.g., deleting a wall removes hosted doors/windows).

Maximum 100 elements per call. The response shows both directly deleted elements and total affected count (including dependents).

Use revit_query_elements or revit_get_element_info to verify element IDs before deleting.`,
      inputSchema: {
        element_ids: z
          .array(elementIdSchema("Element ID"))
          .min(1)
          .max(100)
          .describe("Array of element IDs to delete (max 100)"),
        idempotency_key: idempotencyKeySchema(),
      },
      annotations: DESTRUCTIVE_ANNOTATIONS,
    },
    async (params) => {
      return sendAndFormat(
        wsClient,
        "delete_elements",
        {
          element_ids: params.element_ids,
          idempotency_key: params.idempotency_key,
        },
        BATCH_TIMEOUT_MS
      );
    }
  );

  // ─── revit_move_elements ───
  server.registerTool(
    "revit_move_elements",
    {
      title: "Move Elements",
      description: `Move one or more Revit elements by a translation vector.

All distances are in feet. Common conversions:
  - 1 foot = 304.8 mm
  - 1 meter = 3.28084 feet
  - 1000 mm = 3.28084 feet

Maximum 500 elements per call. Use revit_get_element_info to check current positions.

Example: Move a wall 10 feet in X → move_elements(element_ids=[12345], dx=10, dy=0)`,
      inputSchema: {
        element_ids: z
          .array(elementIdSchema("Element ID"))
          .min(1)
          .max(500)
          .describe("Array of element IDs to move"),
        dx: z.number().finite().describe("Translation in X direction (feet)"),
        dy: z.number().finite().describe("Translation in Y direction (feet)"),
        dz: z
          .number()
          .finite()
          .optional()
          .default(0)
          .describe("Translation in Z direction (feet, default: 0)"),
        idempotency_key: idempotencyKeySchema(),
      },
      annotations: MODIFY_ANNOTATIONS,
    },
    async (params) => {
      return sendAndFormat(
        wsClient,
        "move_elements",
        {
          element_ids: params.element_ids,
          dx: params.dx,
          dy: params.dy,
          dz: params.dz ?? 0,
          idempotency_key: params.idempotency_key,
        },
        BATCH_TIMEOUT_MS
      );
    }
  );

  // ─── revit_copy_elements ───
  server.registerTool(
    "revit_copy_elements",
    {
      title: "Copy Elements",
      description: `Copy one or more Revit elements by a translation vector.

Creates new elements at the offset position. Returns the IDs of newly created elements.

All distances are in feet. Maximum 100 elements per call.

Example: Copy a column 20 feet east → copy_elements(element_ids=[56789], dx=20, dy=0)`,
      inputSchema: {
        element_ids: z
          .array(elementIdSchema("Element ID"))
          .min(1)
          .max(100)
          .describe("Array of element IDs to copy (max 100)"),
        dx: z.number().finite().describe("Translation in X direction (feet)"),
        dy: z.number().finite().describe("Translation in Y direction (feet)"),
        dz: z
          .number()
          .finite()
          .optional()
          .default(0)
          .describe("Translation in Z direction (feet, default: 0)"),
        idempotency_key: idempotencyKeySchema(),
      },
      annotations: MODIFY_ANNOTATIONS,
    },
    async (params) => {
      return sendAndFormat(
        wsClient,
        "copy_elements",
        {
          element_ids: params.element_ids,
          dx: params.dx,
          dy: params.dy,
          dz: params.dz ?? 0,
          idempotency_key: params.idempotency_key,
        },
        BATCH_TIMEOUT_MS
      );
    }
  );

  // ─── revit_duplicate_type ───
  server.registerTool(
    "revit_duplicate_type",
    {
      title: "Duplicate Element Type",
      description: `Duplicate an existing Revit ElementType (FamilySymbol, WallType, FloorType, etc.) under a new name.

Use this when migrating beam/column types from a CAD schedule into Revit: take an existing type close to what you need, duplicate it, and adjust parameters on the duplicate.

The new name must be unique within the same family/category. Returns the new type's ID and name.

Common workflow:
  1. revit_get_family_types(family_name="...", include_types=true) → find a source type
  2. revit_duplicate_type(source_type_id=N, new_name="...") → get a new type
  3. revit_modify_element_parameter(element_id=<new>, parameter_name="b", value=600, is_type_param=true) → tweak dimensions
  4. revit_change_instance_type(instance_ids=[...], new_type_id=<new>) → reassign existing beams`,
      inputSchema: {
        source_type_id: elementIdSchema(
          "ElementId of the existing type to copy from"
        ),
        new_name: z.string().trim().min(1)
          .describe("Unique name for the new duplicated type. Avoid : { } | \\ / < > ? * etc."),
        idempotency_key: idempotencyKeySchema(),
      },
      annotations: MODIFY_ANNOTATIONS,
    },
    async (params) => sendAndFormat(wsClient, "duplicate_type", {
      source_type_id: params.source_type_id,
      new_name: params.new_name,
      idempotency_key: params.idempotency_key,
    })
  );

  // ─── revit_rename_type ───
  server.registerTool(
    "revit_rename_type",
    {
      title: "Rename Element Type",
      description: `Rename an existing Revit ElementType. Useful when a schedule splits an existing range (for example, "Levels 1-4" → "Levels 1-3").

The new name must be unique within the family/category. The change propagates to all instances using this type — they keep using it under the new name.`,
      inputSchema: {
        type_id: elementIdSchema("ElementId of the type to rename"),
        new_name: z.string().trim().min(1)
          .describe("New unique name. Idempotent: if equal to current name, no-op."),
        idempotency_key: idempotencyKeySchema(),
      },
      annotations: MODIFY_ANNOTATIONS,
    },
    async (params) => sendAndFormat(wsClient, "rename_type", {
      type_id: params.type_id,
      new_name: params.new_name,
      idempotency_key: params.idempotency_key,
    })
  );

  // ─── revit_change_instance_type ───
  server.registerTool(
    "revit_change_instance_type",
    {
      title: "Reassign Instance(s) to a Different Type",
      description: `Change the ElementType assignment for one or more existing instances. All changes happen in a single transaction (atomic).

Use this AFTER revit_duplicate_type when you need to migrate some instances of a beam/wall type to a newly-created variant. Typical CAD-→-Revit reconciliation flow:

  - Old Revit type: "Beam Type, Levels 1-4" (instances spanning four levels)
  - New schedule splits to: "Beam Type, Levels 1-3" + "Beam Type, Level 4"
  - Read each instance's project-specific grouping parameter, group by target type, then call this with the IDs to reassign.

Limits:
  - Max 1000 instances per call (batch larger sets in chunks).
  - Uses Element.ChangeTypeId — works for FamilyInstance, Wall, Floor, etc.
  - If ALL changes fail, the transaction is rolled back.`,
      inputSchema: {
        instance_ids: z.union([
          elementIdSchema("Instance ElementId"),
          z.array(elementIdSchema("Instance ElementId")).min(1).max(1000),
        ]).describe("Single instance ID or array of IDs (max 1000)."),
        new_type_id: elementIdSchema("ElementId of the target ElementType"),
        idempotency_key: idempotencyKeySchema(),
      },
      annotations: MODIFY_ANNOTATIONS,
    },
    async (params) =>
      sendAndFormat(
        wsClient,
        "change_instance_type",
        {
          instance_ids: params.instance_ids,
          new_type_id: params.new_type_id,
          idempotency_key: params.idempotency_key,
        },
        BATCH_TIMEOUT_MS
      )
  );

  // ─── revit_batch_modify_parameters ───
  server.registerTool(
    "revit_batch_modify_parameters",
    {
      title: "Batch Modify Parameters",
      description: `Set parameter values on MANY elements in a single transaction. Replaces hundreds of individual revit_modify_element_parameter calls with one batch.

Two input shapes — use exactly one:
  A) modifications: fine-grained list, one entry per set. Use when values differ per element.
     [{element_id: 123, parameter_name: "Comments", value: "Reviewed"}, {element_id: 456, parameter_name: "Comments", value: "Needs review"}]
  B) element_ids + parameters: applies every name→value pair to every element. Use for uniform stamping.
     element_ids=[1,2,3], parameters={"Comments": "Reviewed", "Mark": "QA-01"}

**only_if_empty=true** — "fill the blanks" mode: parameters that already have a value are skipped (reported as skipped_not_empty), never overwritten. Perfect for stamping standard values without clobbering intentional overrides.

Partial success: failed items are reported individually (element not found, read-only, type mismatch); successful sets commit together. Max 5000 sets per call.

Pass idempotency_key when retrying after a timeout to avoid double-application.`,
      inputSchema: BATCH_MODIFY_INPUT_SCHEMA,
      annotations: MODIFY_ANNOTATIONS,
    },
    async (params) => {
      return sendAndFormat(
        wsClient,
        "batch_modify_parameters",
        {
          modifications: params.modifications ?? null,
          element_ids: params.element_ids ?? null,
          parameters: params.parameters ?? null,
          value_mode: params.value_mode ?? "internal",
          only_if_empty: params.only_if_empty ?? false,
          idempotency_key: params.idempotency_key,
        },
        BATCH_TIMEOUT_MS
      );
    }
  );
}
