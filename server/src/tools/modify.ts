/**
 * Modify Tools — Tools for modifying existing Revit elements.
 *
 * Tools:
 *   revit_modify_element_parameter — Set a parameter value on an element
 *   revit_delete_elements          — Delete one or more elements
 *   revit_move_elements            — Move elements by a translation vector
 *   revit_copy_elements            — Copy elements by a translation vector
 */

import type { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import type { RevitWebSocketClient } from "../services/websocket-client.js";
import { sendAndFormat } from "../services/response-formatter.js";

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
        element_id: z.number().describe("The Revit element ID to modify"),
        parameter_name: z.string().describe("Name of the parameter to set"),
        value: z.union([z.string(), z.number(), z.boolean()]).describe("New value for the parameter"),
        is_type_param: z
          .boolean()
          .optional()
          .default(false)
          .describe("Set on the element's type instead of instance (default: false)"),
      },
      annotations: MODIFY_ANNOTATIONS,
    },
    async (params) => {
      return sendAndFormat(wsClient, "modify_element_parameter", {
        element_id: params.element_id,
        parameter_name: params.parameter_name,
        value: params.value,
        is_type_param: params.is_type_param ?? false,
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
          .array(z.number())
          .describe("Array of element IDs to delete (max 100)"),
      },
      annotations: DESTRUCTIVE_ANNOTATIONS,
    },
    async (params) => {
      return sendAndFormat(wsClient, "delete_elements", {
        element_ids: params.element_ids,
      });
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
          .array(z.number())
          .describe("Array of element IDs to move"),
        dx: z.number().describe("Translation in X direction (feet)"),
        dy: z.number().describe("Translation in Y direction (feet)"),
        dz: z
          .number()
          .optional()
          .default(0)
          .describe("Translation in Z direction (feet, default: 0)"),
      },
      annotations: MODIFY_ANNOTATIONS,
    },
    async (params) => {
      return sendAndFormat(wsClient, "move_elements", {
        element_ids: params.element_ids,
        dx: params.dx,
        dy: params.dy,
        dz: params.dz ?? 0,
      });
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
          .array(z.number())
          .describe("Array of element IDs to copy (max 100)"),
        dx: z.number().describe("Translation in X direction (feet)"),
        dy: z.number().describe("Translation in Y direction (feet)"),
        dz: z
          .number()
          .optional()
          .default(0)
          .describe("Translation in Z direction (feet, default: 0)"),
      },
      annotations: MODIFY_ANNOTATIONS,
    },
    async (params) => {
      return sendAndFormat(wsClient, "copy_elements", {
        element_ids: params.element_ids,
        dx: params.dx,
        dy: params.dy,
        dz: params.dz ?? 0,
      });
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
        source_type_id: z.number().int()
          .describe("ElementId of the existing type to copy from."),
        new_name: z.string().min(1)
          .describe("Unique name for the new duplicated type. Avoid : { } | \\ / < > ? * etc."),
      },
      annotations: MODIFY_ANNOTATIONS,
    },
    async (params) => sendAndFormat(wsClient, "duplicate_type", {
      source_type_id: params.source_type_id,
      new_name: params.new_name,
    })
  );

  // ─── revit_rename_type ───
  server.registerTool(
    "revit_rename_type",
    {
      title: "Rename Element Type",
      description: `Rename an existing Revit ElementType. Useful when CAD schedule type names change (e.g. floor range "B1F-4F" → "B1F-3F" because a new schedule splits the original range).

The new name must be unique within the family/category. The change propagates to all instances using this type — they keep using it under the new name.`,
      inputSchema: {
        type_id: z.number().int()
          .describe("ElementId of the type to rename."),
        new_name: z.string().min(1)
          .describe("New unique name. Idempotent: if equal to current name, no-op."),
      },
      annotations: MODIFY_ANNOTATIONS,
    },
    async (params) => sendAndFormat(wsClient, "rename_type", {
      type_id: params.type_id,
      new_name: params.new_name,
    })
  );

  // ─── revit_change_instance_type ───
  server.registerTool(
    "revit_change_instance_type",
    {
      title: "Reassign Instance(s) to a Different Type",
      description: `Change the ElementType assignment for one or more existing instances. All changes happen in a single transaction (atomic).

Use this AFTER revit_duplicate_type when you need to migrate some instances of a beam/wall type to a newly-created variant. Typical CAD-→-Revit reconciliation flow:

  - Old Revit type: "ACG2, B1F-4F" (50 instances spanning floors B1F~4F)
  - New schedule splits to: "ACG2, B1F-3F" + "ACG2, 4F"
  - Read each instance's SK_FL parameter, group by target type, then call this with the IDs to reassign.

Limits:
  - Max 1000 instances per call (batch larger sets in chunks).
  - Uses Element.ChangeTypeId — works for FamilyInstance, Wall, Floor, etc.
  - If ALL changes fail, the transaction is rolled back.`,
      inputSchema: {
        instance_ids: z.union([
          z.number().int(),
          z.array(z.number().int()).min(1).max(1000),
        ]).describe("Single instance ID or array of IDs (max 1000)."),
        new_type_id: z.number().int()
          .describe("ElementId of the target ElementType."),
      },
      annotations: MODIFY_ANNOTATIONS,
    },
    async (params) => sendAndFormat(wsClient, "change_instance_type", {
      instance_ids: params.instance_ids,
      new_type_id: params.new_type_id,
    })
  );
}
