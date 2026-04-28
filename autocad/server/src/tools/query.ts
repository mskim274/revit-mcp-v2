import type { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import type { AcadWebSocketClient } from "../services/websocket-client.js";
import { sendAndFormat } from "../services/response-formatter.js";

export function registerQueryTools(
  server: McpServer,
  wsClient: AcadWebSocketClient
): void {
  server.registerTool(
    "cad_get_drawing_info",
    {
      title: "Get AutoCAD Drawing Info",
      description: `Drawing-level metadata: AutoCAD version, document path, units (insertion units, angle base), model-space extents (or null on empty drawings), layout names.

Cheap read — no per-entity iteration. Run this at the start of a session to confirm what drawing the user is in.`,
      inputSchema: {},
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async () => sendAndFormat(wsClient, "get_drawing_info")
  );

  server.registerTool(
    "cad_get_layers",
    {
      title: "List AutoCAD Layers",
      description: `Enumerate the drawing's layer table. Each layer reports name, color (ACI index or RGB), linetype, frozen/locked/off/plottable flags, and whether it's the current layer.

Use before cad_create_line / cad_create_* to verify the target layer exists.`,
      inputSchema: {},
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async () => sendAndFormat(wsClient, "get_layers")
  );

  server.registerTool(
    "cad_extract_table",
    {
      title: "Extract AutoCAD Table to Structured Data",
      description: `Read AutoCAD Table entities (the dedicated 'Table' class — created via INSERT TABLE or imported as OLE Excel → Convert to AutoCAD Table) into header + row-of-dict shape suitable for JSON / xlsx export.

For Korean structural drawings (보 일람표 / 기둥 일람표): use this when the schedule was drawn with INSERT TABLE. If the schedule is drawn with Line+Text (a hand-built grid), this returns 'tables_found: 0' with a hint — use the future extract_grid_schedule for that case.

Returns one entry per Table entity with:
- handle: hex entity handle (use this with cad_query_entities to cross-reference)
- headers: array of column names from header_row
- data: array of {column_name: cell_value} dicts, one per data row
- rows / columns: total counts
- position / layer: where the table sits

To extract a specific table, pass its handle (from cad_query_entities entity_type='Table'). Otherwise all tables in model space are returned.`,
      inputSchema: {
        handle: z.string().optional()
          .describe("Specific table entity handle (hex string, e.g. '2A4'). Omit for all tables in model space."),
        header_row: z.number().int().min(0).optional()
          .describe("Row index (0-based) containing column headers. Default 0."),
        limit: z.number().int().min(1).max(20).optional()
          .describe("Max number of tables to return. Default 5."),
      },
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async (params) => sendAndFormat(wsClient, "extract_table", {
      handle: params.handle,
      header_row: params.header_row,
      limit: params.limit,
    })
  );

  server.registerTool(
    "cad_query_entities",
    {
      title: "Search AutoCAD Entities",
      description: `Search model-space entities by type and/or layer. Defaults to summary mode (counts grouped by type and by layer) — use this first on unfamiliar drawings.

Set summary_only=false for paginated detail (default 50 per page, max 200). Each detail row includes id, type, layer, color, plus type-specific extras (start/end for lines, center/radius for circles, text for text, position+name for block references).

For large drawings (10K+ entities), always start with summary_only=true and a specific entity_type filter.`,
      inputSchema: {
        entity_type: z.string().optional()
          .describe("DXF class name to filter (e.g. 'Line', 'Circle', 'BlockReference', 'MText'). Case-insensitive. Omit for all."),
        layer: z.string().optional()
          .describe("Exact layer name. Case-insensitive. Omit for all layers."),
        summary_only: z.boolean().optional().default(true)
          .describe("True (default): counts only. False: paginated detail."),
        limit: z.number().int().min(1).max(200).optional()
          .describe("Page size when summary_only=false. Default 50, max 200."),
        offset: z.number().int().min(0).optional()
          .describe("Pagination offset. Default 0."),
      },
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async (params) => sendAndFormat(wsClient, "query_entities", params)
  );

  server.registerTool(
    "cad_get_selected_entities",
    {
      title: "Get Currently Selected AutoCAD Entities",
      description: `Returns the user's PICKFIRST selection — the entities they had selected in AutoCAD before invoking this tool. Each entity reports handle, type, layer, color, linetype, plus type-specific extras (text/position for DBText/MText, start/end/length for Line, center/radius for Circle, vertex_count for Polyline, block_name/position/rotation/scale for BlockReference).

Use this when the user says things like "현재 선택한 ...", "선택한 요소들", "now look at what I picked" — anything implying they've selected entities in the AutoCAD UI.

Returns count=0 with a hint if no PICKFIRST selection exists. Set include_geometry=true to add bounding-box extents (heavier — only when needed). Default limit=500, max=1000.`,
      inputSchema: {
        include_geometry: z.boolean().optional()
          .describe("Add bounding-box extents to each entity. Default false (lighter)."),
        limit: z.number().int().min(1).max(1000).optional()
          .describe("Max entities to return. Default 500. Total selection count is always returned in 'count' even when truncated."),
      },
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: false, // selection state can change between calls
        openWorldHint: false,
      },
    },
    async (params) => sendAndFormat(wsClient, "get_selected_entities", {
      include_geometry: params.include_geometry,
      limit: params.limit,
    })
  );

  server.registerTool(
    "cad_get_selection_texts",
    {
      title: "Get Text from Current Selection",
      description: `Lighter-weight variant of cad_get_selected_entities — returns only DBText and MText from the user's PICKFIRST selection. Each entry has text content (plain), layer, height, rotation, and insertion-point [x,y,z]. Non-text entities are counted in skipped_non_text but not returned.

Use when you only need text content for downstream parsing (e.g. extracting beam symbols, dimensions from a schedule selection). For a full structured grid extraction, prefer cad_parse_grid_schedule which clusters lines + texts into a table.`,
      inputSchema: {},
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: false,
        openWorldHint: false,
      },
    },
    async () => sendAndFormat(wsClient, "get_selection_texts")
  );

  server.registerTool(
    "cad_get_selection_dimensions",
    {
      title: "Get Dimension Entities from Current Selection",
      description: `Returns Dimension entities (RotatedDimension / AlignedDimension / Radial / Diametric / Arc / Angular / Ordinate) from the user's PICKFIRST selection — including the actual numeric measurement value.

Use this when a Korean structural schedule's section sizes (B×D) or column dimensions are drawn as DIM entities rather than text labels. Each dimension reports:
- measurement (numeric value, the actual distance/angle measured)
- dim_text (override text, empty if AutoCAD just shows the measurement)
- formatted_text (string AutoCAD would render, with units/tolerances applied)
- text_position [x,y,z]
- For RotatedDimension/AlignedDimension: xline1, xline2 (extension line origins), dim_line_point, span_length, orientation ("horizontal" / "vertical" / "angled")
- For radial/diametric: center, chord_point, leader_length

Pair horizontal+vertical dimensions sharing a region to recover B×D for each beam section. Spatial association: a beam section drawing's B is the horizontal dimension whose extension lines straddle it; D is the vertical one.`,
      inputSchema: {},
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: false,
        openWorldHint: false,
      },
    },
    async () => sendAndFormat(wsClient, "get_selection_dimensions")
  );

  server.registerTool(
    "cad_parse_grid_schedule",
    {
      title: "Parse Hand-drawn Grid Schedule into Table",
      description: `Reconstructs a hand-drawn AutoCAD schedule (Line + DBText/MText forming a grid — common in Korean structural drawings: 보 일람표 / PC보 일람표 / 기둥 일람표) into headers + row dicts.

Algorithm: clusters horizontal-line Y-coords and vertical-line X-coords into row/column bands using auto-detected tolerance (≈ median text height ÷ 2). Each text's insertion point is binary-searched into a cell. Header row identified by Korean structural token whitelist (부재기호, 단면, 상부근, …); falls back to top row.

**Default scope='selection'**: requires the user to have selected the schedule region in AutoCAD first. For 31K+ entity drawings, never use scope='all' without a layer filter. Use scope='layer' with a specific schedule layer name when no selection is available.

Returns:
- headers: list of column names (header row, normalized)
- rows: array of {header_name: cell_value} dicts
- preview_markdown: first 8 rows as markdown table (LLM-friendly summary)
- header_row_index, header_confidence ("high" if ≥2 known tokens matched, else "low")
- diagnostics: entity counts, grid dimensions, tolerance used, placed/unplaced text counts

Tune tolerance manually if rows merge unexpectedly (try 0.5 to 5.0 in drawing units).`,
      inputSchema: {
        scope: z.enum(["selection", "layer", "all"]).optional()
          .describe("Where to look for the schedule. Default 'selection' (requires PICKFIRST set). 'all' iterates the whole model space — slow on large drawings."),
        layer: z.string().optional()
          .describe("Layer name when scope='layer'. Case-insensitive exact match."),
        tolerance: z.number().optional()
          .describe("Manual cluster tolerance in drawing units. Default: auto (median text height ÷ 2). Increase if rows split incorrectly, decrease if rows merge."),
        header_tokens: z.array(z.string()).optional()
          .describe("Token whitelist for header detection. Defaults to Korean structural keywords (부재기호, 단면, 상부근, 하부근, 늑근, 비고, …). Override only when working with non-standard schedules."),
        preview_rows: z.number().int().min(1).max(20).optional()
          .describe("How many rows to include in preview_markdown. Default 8."),
      },
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: false, // selection-dependent
        openWorldHint: false,
      },
    },
    async (params) => sendAndFormat(wsClient, "parse_grid_schedule", {
      scope: params.scope,
      layer: params.layer,
      tolerance: params.tolerance,
      header_tokens: params.header_tokens,
      preview_rows: params.preview_rows,
    })
  );
}
