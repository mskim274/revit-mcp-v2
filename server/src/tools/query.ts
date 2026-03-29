/**
 * Query Tools — Tier 1 read-only tools for querying Revit model data.
 *
 * Tools:
 *   revit_query_elements     — Search elements by category with 3-tier pagination
 *   revit_get_element_info   — Get detailed info for a single element
 *   revit_get_levels         — List all levels
 *   revit_get_grids          — List all grids
 *   revit_get_views          — List all views (optionally by type)
 *   revit_get_family_types   — List loaded families and their types
 *   revit_get_types_by_category — List element types for a category
 *   revit_get_all_categories — List all populated categories in the model
 */

import type { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import type { RevitWebSocketClient } from "../services/websocket-client.js";
import { DEFAULT_TIMEOUT_MS } from "../constants.js";
import {
  clampPageSize,
  parseCursor,
  createCursor,
  buildPaginatedResult,
  formatSummary,
  formatPaginatedResult,
} from "../services/pagination.js";

// Shared annotations for read-only tools
const READ_ONLY_ANNOTATIONS = {
  readOnlyHint: true,
  destructiveHint: false,
  idempotentHint: true,
  openWorldHint: false,
} as const;

/**
 * Helper: send command and return formatted MCP text response.
 */
async function sendAndFormat(
  wsClient: RevitWebSocketClient,
  command: string,
  params: Record<string, unknown> = {},
  timeoutMs: number = DEFAULT_TIMEOUT_MS
): Promise<{ content: Array<{ type: "text"; text: string }> }> {
  const response = await wsClient.sendCommand(command, params, timeoutMs);

  if (response.status === "error") {
    return {
      content: [
        {
          type: "text" as const,
          text: `Error: ${response.error?.message ?? "Unknown error"}${
            response.error?.suggestion ? `\nSuggestion: ${response.error.suggestion}` : ""
          }`,
        },
      ],
    };
  }

  return {
    content: [
      {
        type: "text" as const,
        text: JSON.stringify(response.data, null, 2),
      },
    ],
  };
}

export function registerQueryTools(
  server: McpServer,
  wsClient: RevitWebSocketClient
): void {
  // ─── revit_query_elements ───
  server.registerTool(
    "revit_query_elements",
    {
      title: "Query Elements",
      description: `Search for Revit elements by category with smart pagination.

**Default behavior (summary mode):** Returns element counts grouped by type and level — safe for any model size.
**Detail mode (summary_only=false):** Returns paginated element details with cursor-based navigation.

Common categories: Walls, Floors, Roofs, Doors, Windows, Columns, StructuralFraming (beams), StructuralColumns, Rooms, Furniture, Pipes, Ducts.

Use revit_get_all_categories first to discover what categories exist in the model.

Examples:
  - Summary: query_elements(category="Walls") → "523 walls: 3 types across 5 levels"
  - Detail: query_elements(category="Walls", summary_only=false, limit=20) → first 20 walls
  - Filter: query_elements(category="Walls", level_filter="Level 1") → walls on Level 1`,
      inputSchema: {
        category: z
          .string()
          .describe('Category name: "Walls", "Floors", "StructuralFraming", etc.'),
        summary_only: z
          .boolean()
          .optional()
          .default(true)
          .describe("true = counts only (safe for large models), false = element details"),
        limit: z
          .number()
          .optional()
          .default(50)
          .describe("Page size for detail mode (1-200, default 50)"),
        cursor: z
          .string()
          .optional()
          .describe("Pagination cursor from previous response"),
        level_filter: z
          .string()
          .optional()
          .describe("Filter by level name (exact match)"),
        type_filter: z
          .string()
          .optional()
          .describe("Filter by type name (contains match)"),
        parameter_name: z
          .string()
          .optional()
          .describe("Filter by parameter name"),
        parameter_value: z
          .string()
          .optional()
          .describe("Filter by parameter value (requires parameter_name)"),
      },
      annotations: READ_ONLY_ANNOTATIONS,
    },
    async (params) => {
      return sendAndFormat(wsClient, "query_elements", {
        category: params.category,
        summary_only: params.summary_only ?? true,
        limit: clampPageSize(params.limit),
        cursor: params.cursor ?? null,
        level_filter: params.level_filter ?? null,
        type_filter: params.type_filter ?? null,
        parameter_name: params.parameter_name ?? null,
        parameter_value: params.parameter_value ?? null,
      });
    }
  );

  // ─── revit_get_element_info ───
  server.registerTool(
    "revit_get_element_info",
    {
      title: "Get Element Info",
      description: `Get detailed information about a specific Revit element by its ID.

Returns all instance parameters, type parameters, location, bounding box, and metadata.

Use this after revit_query_elements to drill into a specific element.`,
      inputSchema: {
        element_id: z.number().describe("The Revit element ID (integer)"),
      },
      annotations: READ_ONLY_ANNOTATIONS,
    },
    async (params) => {
      return sendAndFormat(wsClient, "get_element_info", {
        element_id: params.element_id,
      });
    }
  );

  // ─── revit_get_levels ───
  server.registerTool(
    "revit_get_levels",
    {
      title: "Get Levels",
      description: `Get all levels in the Revit project, sorted by elevation.

Returns level name, ID, elevation in feet and millimeters.
Use this to understand the building's vertical structure.`,
      inputSchema: {},
      annotations: READ_ONLY_ANNOTATIONS,
    },
    async () => {
      return sendAndFormat(wsClient, "get_levels");
    }
  );

  // ─── revit_get_grids ───
  server.registerTool(
    "revit_get_grids",
    {
      title: "Get Grids",
      description: `Get all grid lines in the Revit project.

Returns grid name, start/end coordinates, length, and whether the grid is curved.
Use this to understand the structural grid layout.`,
      inputSchema: {},
      annotations: READ_ONLY_ANNOTATIONS,
    },
    async () => {
      return sendAndFormat(wsClient, "get_grids");
    }
  );

  // ─── revit_get_views ───
  server.registerTool(
    "revit_get_views",
    {
      title: "Get Views",
      description: `Get all views in the Revit project, optionally filtered by type.

View types: FloorPlan, CeilingPlan, Section, Elevation, ThreeD, Drafting, Legend, Schedule, Sheet.

Returns view name, type, scale, detail level, and associated level (for plans).`,
      inputSchema: {
        view_type: z
          .string()
          .optional()
          .describe('Filter by view type: "FloorPlan", "Section", "ThreeD", etc.'),
      },
      annotations: READ_ONLY_ANNOTATIONS,
    },
    async (params) => {
      return sendAndFormat(wsClient, "get_views", {
        view_type: params.view_type ?? null,
      });
    }
  );

  // ─── revit_get_family_types ───
  server.registerTool(
    "revit_get_family_types",
    {
      title: "Get Family Types",
      description: `Get loaded families and their types from the Revit project.

By default returns a summary (family names and type counts). Use include_types=true to get individual type details.

Use category or family_name filters to narrow results.`,
      inputSchema: {
        category: z
          .string()
          .optional()
          .describe("Filter by category name"),
        family_name: z
          .string()
          .optional()
          .describe("Filter by family name (contains match)"),
        include_types: z
          .boolean()
          .optional()
          .default(false)
          .describe("Include individual type names and IDs"),
      },
      annotations: READ_ONLY_ANNOTATIONS,
    },
    async (params) => {
      return sendAndFormat(wsClient, "get_family_types", {
        category: params.category ?? null,
        family_name: params.family_name ?? null,
        include_types: params.include_types ?? false,
      });
    }
  );

  // ─── revit_get_types_by_category ───
  server.registerTool(
    "revit_get_types_by_category",
    {
      title: "Get Types by Category",
      description: `Get all element types (system + loadable) available for a specific category.

Example: "What wall types are available?" → revit_get_types_by_category(category="Walls")

Returns type name, family name, and how many instances of each type exist in the model.`,
      inputSchema: {
        category: z
          .string()
          .describe('Category name: "Walls", "Floors", "StructuralFraming", etc.'),
      },
      annotations: READ_ONLY_ANNOTATIONS,
    },
    async (params) => {
      return sendAndFormat(wsClient, "get_types_by_category", {
        category: params.category,
      });
    }
  );

  // ─── revit_get_all_categories ───
  server.registerTool(
    "revit_get_all_categories",
    {
      title: "Get All Categories",
      description: `Get all model categories in the current Revit document.

By default only shows categories that contain at least one element. Sorted by element count (descending).

Use this as the first query to understand what's in the model before using revit_query_elements.`,
      inputSchema: {
        include_empty: z
          .boolean()
          .optional()
          .default(false)
          .describe("Include categories with zero elements"),
      },
      annotations: READ_ONLY_ANNOTATIONS,
    },
    async (params) => {
      return sendAndFormat(wsClient, "get_all_categories", {
        include_empty: params.include_empty ?? false,
      });
    }
  );
}
