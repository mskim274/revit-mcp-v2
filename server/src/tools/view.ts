/**
 * View Tools — Tools for controlling Revit view state and element visibility.
 *
 * Tools:
 *   revit_set_active_view      — Switch to a specific view
 *   revit_isolate_elements     — Isolate or hide elements in a view
 *   revit_reset_view_isolation — Reset temporary isolation/hiding
 *   revit_select_elements      — Select elements in the Revit UI
 */

import type { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import type { RevitWebSocketClient } from "../services/websocket-client.js";
import { sendAndFormat } from "../services/response-formatter.js";

const VIEW_ANNOTATIONS = {
  readOnlyHint: false,
  destructiveHint: false,
  idempotentHint: true,
  openWorldHint: false,
} as const;

export function registerViewTools(
  server: McpServer,
  wsClient: RevitWebSocketClient
): void {
  // ─── revit_set_active_view ───
  server.registerTool(
    "revit_set_active_view",
    {
      title: "Set Active View",
      description: `Switch to a specific view in Revit by name or ID.

Supports exact and partial name matching. Use revit_get_views to find available views.

Examples:
  - By name: set_active_view(view_name="Level 1")
  - By ID: set_active_view(view_id=12345)
  - Partial: set_active_view(view_name="3D") → matches "3D View" or "{3D}"`,
      inputSchema: {
        view_name: z
          .string()
          .optional()
          .describe("View name to activate (exact or partial match)"),
        view_id: z
          .number()
          .optional()
          .describe("View ID to activate (takes precedence over name)"),
      },
      annotations: VIEW_ANNOTATIONS,
    },
    async (params) => {
      return sendAndFormat(wsClient, "set_active_view", {
        view_name: params.view_name ?? null,
        view_id: params.view_id ?? null,
      });
    }
  );

  // ─── revit_isolate_elements ───
  server.registerTool(
    "revit_isolate_elements",
    {
      title: "Isolate/Hide Elements in View",
      description: `Isolate or hide specific elements in a Revit view.

**Isolate mode (default):** Only the specified elements are visible; everything else is hidden.
**Hide mode:** The specified elements are hidden; everything else remains visible.

Use revit_reset_view_isolation to undo.

This was requested for "현재 뷰에서 특정 요소만 보여줘" (show only specific elements in current view).

Example: Show only 2 beams → isolate_elements(element_ids=[12345, 67890])`,
      inputSchema: {
        element_ids: z
          .array(z.number())
          .describe("Array of element IDs to isolate or hide"),
        mode: z
          .enum(["isolate", "hide"])
          .optional()
          .default("isolate")
          .describe('"isolate" = show only these, "hide" = hide these (default: isolate)'),
        view_id: z
          .number()
          .optional()
          .describe("Target view ID (default: active view)"),
      },
      annotations: VIEW_ANNOTATIONS,
    },
    async (params) => {
      return sendAndFormat(wsClient, "isolate_elements", {
        element_ids: params.element_ids,
        mode: params.mode ?? "isolate",
        view_id: params.view_id ?? null,
      });
    }
  );

  // ─── revit_reset_view_isolation ───
  server.registerTool(
    "revit_reset_view_isolation",
    {
      title: "Reset View Isolation",
      description: `Reset temporary element isolation/hiding in a view — makes all elements visible again.

Use this after revit_isolate_elements to restore normal view.`,
      inputSchema: {
        view_id: z
          .number()
          .optional()
          .describe("Target view ID (default: active view)"),
      },
      annotations: VIEW_ANNOTATIONS,
    },
    async (params) => {
      return sendAndFormat(wsClient, "reset_view_isolation", {
        view_id: params.view_id ?? null,
      });
    }
  );

  // ─── revit_select_elements ───
  server.registerTool(
    "revit_select_elements",
    {
      title: "Select Elements",
      description: `Select elements in the Revit UI, highlighting them in the current view.

Use this to draw the user's attention to specific elements after a query.

Example: Select 3 walls → select_elements(element_ids=[111, 222, 333])`,
      inputSchema: {
        element_ids: z
          .array(z.number())
          .describe("Array of element IDs to select"),
      },
      annotations: VIEW_ANNOTATIONS,
    },
    async (params) => {
      return sendAndFormat(wsClient, "select_elements", {
        element_ids: params.element_ids,
      });
    }
  );
}
