/**
 * Visualize Tools — review-aid tools that override graphics or place tags in bulk.
 *
 * Tools:
 *   revit_apply_color_filter — Color-highlight elements matched by a selector.
 *                              View-specific override (RC reconciliation 검토 시각화).
 *   revit_tag_by_filter      — Place IndependentTag on every matched element.
 *
 * Both tools share the same selector shape (category / type-name / mark / parameter /
 * level / element_ids). The C# side resolves the selector once via the shared
 * `ElementSelector` helper.
 *
 * Design notes:
 *   - apply_color_filter is mapped to the C# "View" category (view-specific override)
 *     but exposed in this TS module alongside tag_by_filter because users reach for
 *     them together — "highlight + tag the same set".
 *   - tag_by_filter is a "Create" command on the C# side (creates IndependentTag
 *     instances). It runs in a single transaction; failure mid-flight rolls back.
 *   - Both are side-effecting and ride the plugin's idempotency cache (15min TTL,
 *     keyed by idempotency_key parameter or request id).
 */

import type { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import type { RevitWebSocketClient } from "../services/websocket-client.js";
import { sendAndFormat } from "../services/response-formatter.js";

// Shared selector schema — keep the two tools in sync.
const SELECTOR_FIELDS = {
  element_ids: z
    .array(z.number().int())
    .optional()
    .describe(
      "Explicit element IDs. Takes priority over the filters below — pass these when you already have a list from revit_query_elements."
    ),
  category: z
    .string()
    .optional()
    .describe('Category name, e.g. "Walls", "StructuralFraming", "StructuralColumns".'),
  type_name_contains: z
    .string()
    .optional()
    .describe("Element type name contains (case-insensitive)."),
  type_name_starts_with: z
    .string()
    .optional()
    .describe("Element type name starts-with (case-insensitive). Cheaper than contains for prefix-based marks like 'Column_RC, AC30, ...'."),
  mark_contains: z
    .string()
    .optional()
    .describe("Mark (ALL_MODEL_MARK / 'Mark' instance parameter) contains (case-insensitive)."),
  parameter_name: z
    .string()
    .optional()
    .describe("Parameter name to filter on. Pair with parameter_value_contains."),
  parameter_value_contains: z
    .string()
    .optional()
    .describe("Required when parameter_name is set — case-insensitive substring match on the parameter's display value."),
  level_name: z
    .string()
    .optional()
    .describe("Restrict to elements on this level (exact name match). For structural framing, falls back to '참조 레벨' / 'Reference Level'."),
} as const;

// Side-effect annotations — both tools mutate the document
const APPLY_OVERRIDE_ANNOTATIONS = {
  readOnlyHint: false,
  destructiveHint: false, // graphic override is non-destructive (visual only)
  idempotentHint: true,   // same input → same view state
  openWorldHint: false,
} as const;

const TAG_ANNOTATIONS = {
  readOnlyHint: false,
  destructiveHint: false, // creates tags; not destructive but creates new elements
  idempotentHint: false,  // re-running creates duplicate tags
  openWorldHint: false,
} as const;

export function registerVisualizeTools(
  server: McpServer,
  wsClient: RevitWebSocketClient
): void {
  // ─── revit_apply_color_filter ───
  server.registerTool(
    "revit_apply_color_filter",
    {
      title: "Apply Color Filter (View Override)",
      description: `Color-highlight elements in a view using per-element graphic overrides — useful for review tasks like "어떤 type이 미반영인지 보여줘" or "SK_MATE 타입만 빨강으로 칠해줘".

**Mode:**
  - \`mode="apply"\` (default) — override projection/cut line color + optional solid surface fill.
  - \`mode="clear"\` — reset the override (back to "by category").

**Color spec:**
  - Preset names: \`red\` (default), \`orange\`, \`yellow\`, \`green\`, \`blue\`, \`magenta\`, \`cyan\`, \`gray\`
  - RGB triple: \`"255,128,0"\` (each 0-255)

**Selector** (any combination; \`element_ids\` is highest priority):
  - \`category\`, \`type_name_contains\`, \`type_name_starts_with\`, \`mark_contains\`,
    \`parameter_name\` + \`parameter_value_contains\`, \`level_name\`
  - Default max 5000 elements per call.

**Verification (Harness Tier 1):** After commit, re-reads the first overridden element's color and reports a \`verification.color_match\` flag.

Examples:
  - \`apply_color_filter(category="StructuralColumns", type_name_starts_with="Column_RC, AC", color="orange")\` — highlight every AC* RC column
  - \`apply_color_filter(parameter_name="SK_ITEM", parameter_value_contains="PSRC", color="cyan")\` — highlight migrated PSRC instances
  - \`apply_color_filter(category="Walls", mode="clear")\` — clear all wall overrides on the active view`,
      inputSchema: {
        view_id: z
          .number()
          .int()
          .optional()
          .describe("Target view ElementId. Default = active view. Schedule/SystemBrowser/ProjectBrowser views are rejected."),
        mode: z
          .enum(["apply", "clear"])
          .optional()
          .default("apply")
          .describe('"apply" sets the override, "clear" resets it.'),
        ...SELECTOR_FIELDS,
        max_elements: z
          .number()
          .int()
          .min(1)
          .max(50000)
          .optional()
          .default(5000)
          .describe("Cap on matched elements per call. Truncation is reported in the response."),
        color: z
          .string()
          .optional()
          .describe('Color spec — preset name (red/orange/yellow/green/blue/magenta/cyan/gray) or "r,g,b" triple. Default "red".'),
        surface_fill: z
          .boolean()
          .optional()
          .default(true)
          .describe("Also apply solid foreground fill on surface/cut (helps spot elements in 3D + plan)."),
        transparency: z
          .number()
          .int()
          .min(0)
          .max(100)
          .optional()
          .default(0)
          .describe("Surface transparency, 0-100 (0 = opaque). Useful when overlay obscures geometry behind."),
        halftone: z
          .boolean()
          .optional()
          .default(false)
          .describe("Apply halftone — pairs well with grey color to push 'irrelevant' elements into the background."),
      },
      annotations: APPLY_OVERRIDE_ANNOTATIONS,
    },
    async (params) => {
      return sendAndFormat(wsClient, "apply_color_filter", {
        view_id: params.view_id ?? null,
        mode: params.mode ?? "apply",
        element_ids: params.element_ids ?? null,
        category: params.category ?? null,
        type_name_contains: params.type_name_contains ?? null,
        type_name_starts_with: params.type_name_starts_with ?? null,
        mark_contains: params.mark_contains ?? null,
        parameter_name: params.parameter_name ?? null,
        parameter_value_contains: params.parameter_value_contains ?? null,
        level_name: params.level_name ?? null,
        max_elements: params.max_elements ?? 5000,
        color: params.color ?? "red",
        surface_fill: params.surface_fill ?? true,
        transparency: params.transparency ?? 0,
        halftone: params.halftone ?? false,
      });
    }
  );

  // ─── revit_tag_by_filter ───
  server.registerTool(
    "revit_tag_by_filter",
    {
      title: "Tag Elements by Filter",
      description: `Place an IndependentTag on every element matched by the selector — bulk tagging for reviewable models.

**Anchor heuristics:**
  - LocationPoint   → tag at the point
  - LocationCurve   → tag at the curve's mid-point
  - Otherwise       → tag at the element's bounding-box centre in the view

**Tag family selection:**
  - \`tag_type_id\` (preferred) — exact FamilySymbol ElementId.
  - Otherwise the view's default tag for the category is used.
  - Use \`revit_get_family_types(category="...Tags")\` to discover loaded tag families first — missing tag families are the most common cause of \`skipped_count > 0\`.

**Tag mode** (only when \`tag_type_id\` is unspecified):
  - \`"ByCategory"\` (default) — straightforward category tag
  - \`"Multicategory"\` — multi-category tag family
  - \`"Material"\` — material tag

**Cap:** default 500 elements per call (tag creation is heavier than overrides).

**Verification (Harness Tier 1):** After commit, re-queries each created tag id; reports \`verification.count_match\` and a per-element \`skipped_sample\` (top 10) so you can see why some tags didn't land.

**Idempotency:** This command is NOT idempotent — re-running creates duplicate tags. Pass \`idempotency_key\` to deduplicate when retrying a flaky network call.

Examples:
  - \`tag_by_filter(category="StructuralColumns", level_name="3F")\` — tag every column on 3F using the view's default column tag
  - \`tag_by_filter(element_ids=[123,456], tag_type_id=987, has_leader=true)\` — tag specific elements with a chosen tag family + leader line
  - \`tag_by_filter(mark_contains="S1-", offset_x_feet=0.5)\` — bulk tag all "S1-*" marked elements, nudge 0.5ft to the right`,
      inputSchema: {
        view_id: z
          .number()
          .int()
          .optional()
          .describe("Target view ElementId. Default = active view. Must be a graphical view (plan/section/elevation/3D)."),
        ...SELECTOR_FIELDS,
        max_elements: z
          .number()
          .int()
          .min(1)
          .max(5000)
          .optional()
          .default(500)
          .describe("Cap on matched elements per call. Truncation is reported in the response."),
        tag_type_id: z
          .number()
          .int()
          .optional()
          .describe("FamilySymbol ElementId of the tag family-type to use. If omitted, the view's default tag for the category is used."),
        has_leader: z
          .boolean()
          .optional()
          .default(false)
          .describe("Whether the placed tag should have a leader line."),
        orientation: z
          .enum(["Horizontal", "Vertical"])
          .optional()
          .default("Horizontal")
          .describe("Tag text orientation."),
        offset_x_feet: z
          .number()
          .optional()
          .default(0)
          .describe("Tag location X offset from the anchor point (in feet)."),
        offset_y_feet: z
          .number()
          .optional()
          .default(0)
          .describe("Tag location Y offset from the anchor point (in feet)."),
        tag_mode: z
          .enum(["ByCategory", "Multicategory", "Material"])
          .optional()
          .default("ByCategory")
          .describe("Tag mode when tag_type_id is unspecified."),
        idempotency_key: z
          .string()
          .optional()
          .describe("Optional dedup key for retries — same key within 15 min returns cached result instead of re-tagging."),
      },
      annotations: TAG_ANNOTATIONS,
    },
    async (params) => {
      return sendAndFormat(wsClient, "tag_by_filter", {
        view_id: params.view_id ?? null,
        element_ids: params.element_ids ?? null,
        category: params.category ?? null,
        type_name_contains: params.type_name_contains ?? null,
        type_name_starts_with: params.type_name_starts_with ?? null,
        mark_contains: params.mark_contains ?? null,
        parameter_name: params.parameter_name ?? null,
        parameter_value_contains: params.parameter_value_contains ?? null,
        level_name: params.level_name ?? null,
        max_elements: params.max_elements ?? 500,
        tag_type_id: params.tag_type_id ?? null,
        has_leader: params.has_leader ?? false,
        orientation: params.orientation ?? "Horizontal",
        offset_x_feet: params.offset_x_feet ?? 0,
        offset_y_feet: params.offset_y_feet ?? 0,
        tag_mode: params.tag_mode ?? "ByCategory",
        idempotency_key: params.idempotency_key ?? null,
      });
    }
  );
}
