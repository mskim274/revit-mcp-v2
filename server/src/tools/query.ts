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
import { sendAndFormat } from "../services/response-formatter.js";
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

  server.registerTool(
    "revit_get_selected_elements",
    {
      title: "Get Currently Selected Revit Elements",
      description: `Returns the user's current Revit UI selection — the elements they have highlighted in Revit before invoking this tool.

Use when the user says things like "현재 선택한 ...", "선택한 요소들", "the elements I picked" — anything implying they've selected items in Revit's UI.

Each element reports id, name, category, type_name, family_name, level, and a location summary (point or curve start/end + length). Aggregates: count, by_category, by_level. Returns count=0 with a hint if nothing is selected.

Set include_parameters=true to add a small set of hot parameters (area, volume, height, mark, comments) per element — heavier, only when needed. Default limit=500, max=1000; aggregates always reflect the full selection even when truncated.`,
      inputSchema: {
        include_parameters: z.boolean().optional()
          .describe("Add a small set of hot parameters (area, volume, height, mark, comments) per element. Default false."),
        limit: z.number().int().min(1).max(1000).optional()
          .describe("Max elements in the 'elements' array. Default 500. Aggregates count the full selection regardless."),
      },
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: false, // selection state can change between calls
        openWorldHint: false,
      },
    },
    async (params) => sendAndFormat(wsClient, "get_selected_elements", {
      include_parameters: params.include_parameters,
      limit: params.limit,
    })
  );

  server.registerTool(
    "revit_get_element_geometry",
    {
      title: "Get Element Geometry Primitives",
      description: `Returns geometry primitives (bounding box, faces, edges, solids) for one or more elements. Useful when an LLM needs to reason about spatial relationships — "find walls intersecting this slab", "which beams overlap in plan", clearance checks, etc.

Default summary mode: per-element bounding_box + face_count + edge_count + solid_count + total_volume + total_surface_area. Set detail=true to add face metadata (area, normal, planar/cylindrical type, origin, axis/radius for cylindrical), edge endpoints + length + curve type.

Element ID resolution: pass element_ids:[123,456,...] explicitly, OR call with no element_ids to use the current UI selection (PICKFIRST).

All distances/areas/volumes are returned in Revit internal feet (1 ft = 304.8 mm). Caps: max_faces default 50, max_edges default 100 per element to prevent response bloat on detailed families.`,
      inputSchema: {
        element_ids: z.array(z.number().int()).optional()
          .describe("Explicit list of element IDs. If omitted, uses the current UI selection."),
        detail: z.boolean().optional()
          .describe("Include face/edge/solid detail (heavier). Default false (summary only)."),
        include_geometry_view: z.enum(["Coarse", "Medium", "Fine"]).optional()
          .describe("Geometry detail level for traversal. Default 'Coarse'."),
        max_faces: z.number().int().min(1).max(500).optional()
          .describe("Per-element face cap when detail=true. Default 50."),
        max_edges: z.number().int().min(1).max(2000).optional()
          .describe("Per-element edge cap when detail=true. Default 100."),
      },
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async (params) => sendAndFormat(wsClient, "get_element_geometry", {
      element_ids: params.element_ids,
      detail: params.detail,
      include_geometry_view: params.include_geometry_view,
      max_faces: params.max_faces,
      max_edges: params.max_edges,
    })
  );
}
