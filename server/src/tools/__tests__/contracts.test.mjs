import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { z } from "zod";
import "../../services/__tests__/session-routing.test.mjs";
import "../../services/__tests__/work-scope.test.mjs";

import { SERVER_VERSION } from "../../../dist/constants.js";
import { registerCreateTools } from "../../../dist/tools/create.js";
import { registerExportTools } from "../../../dist/tools/export.js";
import { registerModifyTools } from "../../../dist/tools/modify.js";
import { registerQueryTools } from "../../../dist/tools/query.js";
import { registerUtilityTools } from "../../../dist/tools/utility.js";
import { registerViewTools } from "../../../dist/tools/view.js";
import { registerVisualizeTools } from "../../../dist/tools/visualize.js";

function collectTools(...registrars) {
  const tools = new Map();
  const server = {
    registerTool(name, config, handler) {
      tools.set(name, { config, handler });
    },
  };
  const client = {
    async sendCommand(command, params) {
      return { id: "test", status: "success", data: { command, params } };
    },
  };
  for (const register of registrars) register(server, client);
  return tools;
}

const tools = collectTools(
  registerCreateTools,
  registerExportTools,
  registerModifyTools,
  registerQueryTools,
  registerUtilityTools,
  registerViewTools,
  registerVisualizeTools
);

test("advertised Revit MCP version matches package metadata", () => {
  const packageJson = JSON.parse(
    readFileSync(new URL("../../../package.json", import.meta.url), "utf8")
  );
  assert.equal(SERVER_VERSION, packageJson.version);
});

test("document fingerprint uses Revit's stable open-document identity", () => {
  const source = readFileSync(
    new URL(
      "../../../../plugin/RevitMCPPlugin/Services/SessionIdentity.cs",
      import.meta.url
    ),
    "utf8"
  );

  assert.match(source, /document\.GetHashCode\(\)/);
  assert.match(source, /revit-document-v2/);
  assert.doesNotMatch(source, /RuntimeHelpers\.GetHashCode/);
});

test("ping exposes a live CommandSet hot-reload readiness marker", () => {
  const source = readFileSync(
    new URL(
      "../../../../commandset/Commands/Utility/PingCommand.cs",
      import.meta.url,
    ),
    "utf8",
  );

  assert.match(source, /\["commandset_hot_reload_ready"\]\s*=\s*true/);
});

test("CommandSet hot reload tools fail closed and expose retry safety", async () => {
  const reload = tools.get("revit_reload_commandset");
  assert.ok(reload);

  for (const generation of ["", "../escape", "bad generation", "a".repeat(129)]) {
    assert.equal(
      parseInput(reload, { generation }).success,
      false,
      `expected invalid generation ${JSON.stringify(generation)}`,
    );
  }

  const parsed = parseInput(reload, {
    generation: "20260813T120000Z-deadbeef",
    idempotency_key: "reload-20260813",
  });
  assert.equal(parsed.success, true);
  assert.equal(parsed.data.allow_command_removal, false);
  assert.equal(parsed.data.persist, true);
  const result = await reload.handler(parsed.data);
  const payload = JSON.parse(result.content[0].text);
  assert.equal(payload.command, "reload_commandset");
  assert.equal(payload.params.generation, "20260813T120000Z-deadbeef");
  assert.equal(payload.params.idempotency_key, "reload-20260813");

  assert.ok(tools.has("revit_get_commandset_status"));
});

test("hot reload keeps host contracts separate from the CommandSet", () => {
  const pluginProject = readFileSync(
    new URL(
      "../../../../plugin/RevitMCPPlugin/RevitMCPPlugin.csproj",
      import.meta.url,
    ),
    "utf8",
  );
  const commandSetProject = readFileSync(
    new URL("../../../../commandset/CommandSet.csproj", import.meta.url),
    "utf8",
  );
  const dispatcher = readFileSync(
    new URL(
      "../../../../plugin/RevitMCPPlugin/CommandDispatcher.cs",
      import.meta.url,
    ),
    "utf8",
  );
  const loadContext = readFileSync(
    new URL(
      "../../../../plugin/RevitMCPPlugin/Services/CommandSetLoadContext.cs",
      import.meta.url,
    ),
    "utf8",
  );
  const runtime = readFileSync(
    new URL(
      "../../../../plugin/RevitMCPPlugin/Services/CommandSetRuntime.cs",
      import.meta.url,
    ),
    "utf8",
  );
  const sideBySideDeploy = readFileSync(
    new URL(
      "../../../../scripts/deploy-host-side-by-side.ps1",
      import.meta.url,
    ),
    "utf8",
  );

  assert.match(pluginProject, /RevitMCP\.Contracts\.csproj/);
  assert.match(commandSetProject, /RevitMCP\.Contracts\.csproj/);
  assert.match(commandSetProject, /EnableDynamicLoading/);
  assert.match(dispatcher, /ReloadCommandSetCommand/);
  assert.match(loadContext, /isCollectible:\s*true/);
  assert.match(loadContext, /typeof\(IRevitCommand\)\.Assembly/);
  assert.match(
    loadContext,
    /FileShare\.ReadWrite\s*\|\s*FileShare\.Delete/,
  );
  assert.match(runtime, /GC\.WaitForPendingFinalizers\(\)/);
  assert.match(runtime, /pending\s*>=\s*MaxPendingRetiredContexts/);
  assert.match(sideBySideDeploy, /RevitMCP\\hosts/);
  assert.match(sideBySideDeploy, /\[IO\.File\]::Replace/);
  assert.match(sideBySideDeploy, /revit-mcp\.addin\.previous\.\{0\}\.\{1\}/);
});

function schema(name) {
  const value = tools.get(name)?.config.inputSchema;
  assert.ok(value && typeof value.safeParse === "function");
  return value;
}

function parseInput(tool, input) {
  const inputSchema =
    typeof tool.config.inputSchema.safeParse === "function"
      ? tool.config.inputSchema
      : z.object(tool.config.inputSchema);
  return inputSchema.safeParse(input);
}

test("pipe points follow the selected coordinate contract", () => {
  const pipe = schema("revit_create_pipe_run");
  assert.equal(
    pipe.safeParse({
      points: [
        { e: 1, n: 2, z: 3 },
        { e: 4, n: 5, z: 6 },
      ],
    }).success,
    true
  );
  assert.equal(
    pipe.safeParse({
      coordinate_mode: "survey",
      points: [
        { x: 1, y: 2, z: 3 },
        { x: 4, y: 5, z: 6 },
      ],
    }).success,
    false
  );
  assert.equal(
    pipe.safeParse({
      coordinate_mode: "internal",
      input_unit: "mm",
      points: [
        { x: 1, y: 2, z: 3 },
        { x: 4, y: 5, z: 6 },
      ],
    }).success,
    false
  );
  assert.equal(
    pipe.safeParse({
      coordinate_mode: "internal",
      points: [
        { x: 1, y: 2, z: 3 },
        { x: 4, y: 5, z: 6 },
      ],
    }).success,
    true
  );
});

test("floor boundary accepts exactly one complete boundary mode", () => {
  const floor = schema("revit_create_floor");
  assert.equal(floor.safeParse({}).success, false);
  assert.equal(
    floor.safeParse({
      min_x: 0,
      min_y: 0,
      max_x: 10,
      max_y: 10,
    }).success,
    true
  );
  assert.equal(
    floor.safeParse({
      min_x: 0,
      min_y: 0,
      max_x: 10,
      max_y: 10,
      points: [
        { x: 0, y: 0 },
        { x: 10, y: 0 },
        { x: 0, y: 10 },
      ],
    }).success,
    false
  );
});

test("query elements rejects ambiguous filter and grouping combinations", () => {
  const query = schema("revit_query_elements");

  const defaults = query.safeParse({ category: "Walls" });
  assert.equal(defaults.success, true);
  assert.equal(defaults.data.summary_only, true);
  assert.equal(defaults.data.ids_only, false);
  assert.equal(defaults.data.include_links, false);

  for (const matchMode of ["exact", "contains", "empty"]) {
    assert.equal(
      query.safeParse({
        category: "Walls",
        match_mode: matchMode,
      }).success,
      false
    );
    assert.equal(
      query.safeParse({
        category: "Walls",
        parameter_name: "Mark",
        match_mode: matchMode,
      }).success,
      true
    );
  }

  assert.equal(
    query.safeParse({
      category: "Walls",
      parameter_value: "A",
    }).success,
    false
  );
  assert.equal(
    query.safeParse({
      category: "Walls",
      parameter_name: "Mark",
      parameter_value: "A",
    }).success,
    true
  );
  assert.equal(
    query.safeParse({
      category: "Walls",
      parameter_name: "Mark",
      parameter_value: "A",
      match_mode: "empty",
    }).success,
    false
  );

  for (const blank of ["", "   "]) {
    assert.equal(
      query.safeParse({
        category: "Walls",
        parameter_name: "Mark",
        parameter_value: blank,
      }).success,
      false
    );
    assert.equal(
      query.safeParse({
        category: "Walls",
        level_filter: blank,
      }).success,
      false
    );
    assert.equal(
      query.safeParse({
        category: "Walls",
        type_filter: blank,
      }).success,
      false
    );
  }

  const normalizedFilters = query.safeParse({
    category: "Walls",
    level_filter: " Level 1 ",
    workset_filter: " Architecture ",
    type_filter: " Basic Wall ",
    parameter_name: "Mark",
    parameter_value: " A ",
  });
  assert.equal(normalizedFilters.success, true);
  assert.equal(normalizedFilters.data.level_filter, "Level 1");
  assert.equal(normalizedFilters.data.workset_filter, "Architecture");
  assert.equal(normalizedFilters.data.type_filter, "Basic Wall");
  assert.equal(normalizedFilters.data.parameter_value, "A");

  assert.equal(
    query.safeParse({
      category: "Walls",
      group_by_parameter: "Mark",
    }).success,
    true
  );
  assert.equal(
    query.safeParse({
      category: "Walls",
      summary_only: false,
      group_by_parameter: "Mark",
    }).success,
    false
  );
  assert.equal(
    query.safeParse({
      category: "Walls",
      ids_only: true,
      group_by_parameter: "Mark",
    }).success,
    false
  );
});

test("query elements forwards linked-model and exact host-workset options", async () => {
  const tool = tools.get("revit_query_elements");
  assert.ok(tool);
  const query = schema("revit_query_elements");

  assert.equal(
    query.safeParse({ category: "Walls", workset_filter: "   " }).success,
    false
  );
  assert.equal(
    query.safeParse({ category: "Walls", include_links: "true" }).success,
    false
  );

  const parsed = query.safeParse({
    category: "Walls",
    include_links: true,
    workset_filter: " Core ",
  });
  assert.equal(parsed.success, true);
  const result = await tool.handler(parsed.data);
  const payload = JSON.parse(result.content[0].text);
  assert.equal(payload.params.include_links, true);
  assert.equal(payload.params.workset_filter, "Core");
});

test("room workflows stay query/export-only", () => {
  const query = tools.get("revit_query_elements");
  assert.ok(query);
  assert.match(query.config.description, /category="Rooms"/);
  assert.match(query.config.description, /revit_export_schedule/);
  assert.equal(tools.has("revit_create_room"), false);
});

test("linked-model discovery is registered read-only", () => {
  const linkedModels = tools.get("revit_get_linked_models");
  assert.ok(linkedModels);
  assert.equal(parseInput(linkedModels, {}).success, true);
  assert.equal(linkedModels.config.annotations.readOnlyHint, true);
  assert.equal(linkedModels.config.annotations.destructiveHint, false);
});

test("raw query element value filters fail closed on blank strings", () => {
  const source = readFileSync(
    new URL(
      "../../../../commandset/Commands/Query/QueryElementsCommand.cs",
      import.meta.url
    ),
    "utf8"
  );

  for (const key of [
    "level_filter",
    "workset_filter",
    "type_filter",
    "parameter_value",
  ]) {
    assert.match(
      source,
      new RegExp(
        `TryGetOptionalNonBlankString\\(\\s*parameters,\\s*"${key}"`
      )
    );
  }
  assert.match(source, /string\.IsNullOrWhiteSpace\(text\)/);
  assert.match(source, /TryGetOptionalStrictBool\([\s\S]*?"include_links"/);
  assert.match(source, /use match_mode='empty' to find unfilled values/i);
});

test("query element pagination is mode-specific and never silently clamped", async () => {
  const tool = tools.get("revit_query_elements");
  assert.ok(tool);
  const query = schema("revit_query_elements");

  for (const input of [
    { category: "Walls", limit: 50 },
    { category: "Walls", cursor: "50" },
    { category: "Walls", summary_only: false, limit: 0 },
    { category: "Walls", summary_only: false, limit: 201 },
    { category: "Walls", ids_only: true, limit: 0 },
    { category: "Walls", ids_only: true, limit: 10_001 },
    { category: "Walls", ids_only: true, cursor: "   " },
  ]) {
    assert.equal(
      query.safeParse(input).success,
      false,
      `expected rejection for ${JSON.stringify(input)}`
    );
  }

  for (const input of [
    { category: "Walls", summary_only: false, limit: 1 },
    { category: "Walls", summary_only: false, limit: 200 },
    { category: "Walls", ids_only: true, limit: 1 },
    { category: "Walls", ids_only: true, limit: 10_000 },
  ]) {
    assert.equal(
      query.safeParse(input).success,
      true,
      `expected acceptance for ${JSON.stringify(input)}`
    );
  }

  const cursor = query.safeParse({
    category: "Walls",
    summary_only: false,
    cursor: " 200 ",
  });
  assert.equal(cursor.success, true);
  assert.equal(cursor.data.cursor, "200");

  for (const input of [
    { category: "Walls" },
    { category: "Walls", summary_only: false },
    { category: "Walls", ids_only: true },
  ]) {
    const parsed = query.safeParse(input);
    assert.equal(parsed.success, true);
    const result = await tool.handler(parsed.data);
    const payload = JSON.parse(result.content[0].text);
    assert.equal("limit" in payload.params, false);
    assert.equal("cursor" in payload.params, false);
  }
});

test("geometry IDs and type names reject explicit empty values", () => {
  const geometry = tools.get("revit_get_element_geometry");
  assert.ok(geometry);
  assert.equal(parseInput(geometry, {}).success, true);
  assert.equal(parseInput(geometry, { element_ids: [] }).success, false);
  assert.equal(parseInput(geometry, { element_ids: [1] }).success, true);

  for (const name of ["revit_duplicate_type", "revit_rename_type"]) {
    const tool = tools.get(name);
    assert.ok(tool);
    const idField =
      name === "revit_duplicate_type"
        ? { source_type_id: 1 }
        : { type_id: 1 };
    assert.equal(
      parseInput(tool, { ...idField, new_name: "   " }).success,
      false
    );
    const parsed = parseInput(tool, {
      ...idField,
      new_name: "  Reviewed Type  ",
    });
    assert.equal(parsed.success, true);
    assert.equal(parsed.data.new_name, "Reviewed Type");
  }
});

test("move and copy reject non-finite translation vectors", () => {
  for (const name of ["revit_move_elements", "revit_copy_elements"]) {
    const tool = tools.get(name);
    assert.ok(tool);
    assert.equal(
      parseInput(tool, {
        element_ids: [1],
        dx: Number.POSITIVE_INFINITY,
        dy: 0,
      }).success,
      false
    );
    assert.equal(
      parseInput(tool, {
        element_ids: [1],
        dx: 0,
        dy: 0,
        dz: Number.NEGATIVE_INFINITY,
      }).success,
      false
    );
  }
});

test("visual selector rejects empty and incomplete filters", () => {
  for (const name of [
    "revit_apply_color_filter",
    "revit_tag_by_filter",
  ]) {
    const selector = schema(name);
    assert.equal(selector.safeParse({}).success, false);
    assert.equal(selector.safeParse({ element_ids: [] }).success, false);
    assert.equal(
      selector.safeParse({ parameter_name: "Mark" }).success,
      false
    );
    assert.equal(
      selector.safeParse({
        parameter_name: "Mark",
        parameter_value_contains: "A",
      }).success,
      true
    );
    assert.equal(selector.safeParse({ category: "Walls" }).success, true);
  }
});

test("tag-by-filter enforces its 500-element and tag-mode contracts", () => {
  const tag = schema("revit_tag_by_filter");
  assert.equal(
    tag.safeParse({
      element_ids: Array.from({ length: 500 }, (_, index) => index + 1),
    }).success,
    true
  );
  assert.equal(
    tag.safeParse({
      element_ids: Array.from({ length: 501 }, (_, index) => index + 1),
    }).success,
    false
  );
  assert.equal(
    tag.safeParse({
      category: "Walls",
      max_elements: 501,
    }).success,
    false
  );
  assert.equal(
    tag.safeParse({
      element_ids: [1],
      tag_type_id: 2,
      tag_mode: "ByCategory",
    }).success,
    true
  );
  for (const tagMode of ["Multicategory", "Material"]) {
    assert.equal(
      tag.safeParse({
        element_ids: [1],
        tag_type_id: 2,
        tag_mode: tagMode,
      }).success,
      false
    );
  }
});

test("duplicate views requires 1-100 combined strict targets", () => {
  const duplicate = schema("revit_duplicate_views");
  assert.equal(duplicate.safeParse({}).success, false);
  assert.equal(duplicate.safeParse({ view_ids: [] }).success, false);
  assert.equal(duplicate.safeParse({ view_names: [""] }).success, false);
  assert.equal(duplicate.safeParse({ view_ids: [1] }).success, true);
  assert.equal(
    duplicate.safeParse({
      view_ids: Array.from({ length: 100 }, (_, index) => index + 1),
      view_names: ["extra"],
    }).success,
    false
  );
});

test("family placement is exact, batched, unit-explicit, and retry-safe", async () => {
  const tool = tools.get("revit_place_family");
  assert.ok(tool);
  const input = schema("revit_place_family");

  assert.equal(input.safeParse({}).success, false);
  assert.equal(input.safeParse({ placements: [] }).success, false);
  assert.equal(
    input.safeParse({
      placements: [{ point: [0, 0, 0] }],
    }).success,
    false
  );
  assert.equal(
    input.safeParse({
      placements: [{ type_id: 1, family_name: "Desk", type_name: "A", point: [0, 0, 0] }],
    }).success,
    false
  );
  assert.equal(
    input.safeParse({
      placements: [{ family_name: "Desk", type_name: "A", point: [0, 0, 0], level_id: 2, level_name: "L1" }],
    }).success,
    false
  );
  assert.equal(
    input.safeParse({
      placements: [{ type_id: 1, point: [0, Number.NaN, 0] }],
    }).success,
    false
  );
  assert.equal(
    input.safeParse({
      placements: Array.from({ length: 51 }, (_, index) => ({
        type_id: 1,
        point: [index, 0, 0],
      })),
    }).success,
    false
  );

  const parsed = input.safeParse({
    placements: [{
      family_name: " Desk ",
      type_name: " 1200 ",
      point: [1000, 2000, 3000],
      level_name: " L1 ",
    }],
    input_unit: "mm",
    idempotency_key: "family-placement-key",
  });
  assert.equal(parsed.success, true);
  assert.equal(parsed.data.placements[0].family_name, "Desk");
  assert.equal(parsed.data.placements[0].type_name, "1200");
  assert.equal(parsed.data.placements[0].level_name, "L1");
  const result = await tool.handler(parsed.data);
  const payload = JSON.parse(result.content[0].text);
  assert.equal(payload.command, "place_family");
  assert.equal(payload.params.input_unit, "mm");
  assert.equal(payload.params.idempotency_key, "family-placement-key");
});

test("sheet discovery is read-only and viewport placement is a bounded batch", async () => {
  const sheets = tools.get("revit_get_sheets");
  assert.ok(sheets);
  assert.equal(parseInput(sheets, {}).success, true);
  assert.equal(sheets.config.annotations.readOnlyHint, true);
  assert.equal(sheets.config.annotations.destructiveHint, false);

  const tool = tools.get("revit_place_views_on_sheet");
  assert.ok(tool);
  const input = schema("revit_place_views_on_sheet");
  assert.equal(input.safeParse({}).success, false);
  assert.equal(input.safeParse({ sheet_id: 1, placements: [] }).success, false);
  assert.equal(
    input.safeParse({
      sheet_id: 1,
      placements: [{ view_id: 2, point: [0, Number.POSITIVE_INFINITY] }],
    }).success,
    false
  );
  assert.equal(
    input.safeParse({
      sheet_id: 1,
      placements: Array.from({ length: 51 }, (_, index) => ({
        view_id: index + 2,
        point: [index, 0],
      })),
    }).success,
    false
  );

  const parsed = input.safeParse({
    sheet_id: "9007199254740993",
    placements: [{ view_id: 2, point: [100, 200] }],
    input_unit: "mm",
    idempotency_key: "viewport-placement-key",
  });
  assert.equal(parsed.success, true);
  const result = await tool.handler(parsed.data);
  const payload = JSON.parse(result.content[0].text);
  assert.equal(payload.command, "place_views_on_sheet");
  assert.equal(payload.params.sheet_id, "9007199254740993");
  assert.equal(payload.params.input_unit, "mm");
  assert.equal(payload.params.idempotency_key, "viewport-placement-key");
});

test("selection zoom defaults false and is forwarded to the UI action", async () => {
  const tool = tools.get("revit_select_elements");
  assert.ok(tool);
  const defaults = parseInput(tool, { element_ids: [1] });
  assert.equal(defaults.success, true);
  assert.equal(defaults.data.zoom, false);
  assert.equal(
    parseInput(tool, { element_ids: [1], zoom: "true" }).success,
    false
  );

  const parsed = parseInput(tool, {
    element_ids: [1, 2],
    zoom: true,
    idempotency_key: "select-zoom-key",
  });
  assert.equal(parsed.success, true);
  const result = await tool.handler(parsed.data);
  const payload = JSON.parse(result.content[0].text);
  assert.equal(payload.params.zoom, true);
  assert.equal(payload.params.idempotency_key, "select-zoom-key");

  const hostSource = readFileSync(
    new URL(
      "../../../../plugin/RevitMCPPlugin/WebSocketServer.cs",
      import.meta.url
    ),
    "utf8"
  );
  assert.match(hostSource, /uiDocument\.ShowElements\(selectionIds\)/);
});

test("linked-soffit wall modification defaults to dry-run and fails closed", async () => {
  const tool = tools.get("revit_modify_wall_height_to_linked_soffit");
  assert.ok(tool);
  const input = schema("revit_modify_wall_height_to_linked_soffit");
  const defaults = input.safeParse({ element_ids: [1] });
  assert.equal(defaults.success, true);
  assert.equal(defaults.data.dry_run, true);
  assert.equal(defaults.data.link_name_contains, "ST_");
  assert.equal(defaults.data.step_tolerance_mm, 20);
  assert.equal(input.safeParse({ element_ids: [] }).success, false);
  assert.equal(input.safeParse({ element_ids: [1], dry_run: "true" }).success, false);
  assert.equal(input.safeParse({ element_ids: [1], link_name_contains: "   " }).success, false);
  assert.equal(input.safeParse({ element_ids: [1], step_tolerance_mm: -1 }).success, false);
  assert.equal(
    input.safeParse({
      element_ids: Array.from({ length: 51 }, (_, index) => index + 1),
    }).success,
    false
  );

  const parsed = input.safeParse({
    element_ids: [1],
    dry_run: false,
    idempotency_key: "linked-soffit-key",
  });
  assert.equal(parsed.success, true);
  const result = await tool.handler(parsed.data);
  const payload = JSON.parse(result.content[0].text);
  assert.equal(payload.command, "modify_wall_height_to_linked_soffit");
  assert.equal(payload.params.dry_run, false);
  assert.equal(payload.params.idempotency_key, "linked-soffit-key");

  const source = readFileSync(
    new URL(
      "../../../../commandset/Commands/Modify/ModifyWallHeightToLinkedSoffitCommand.cs",
      import.meta.url
    ),
    "utf8"
  );
  assert.match(source, /"dry_run",\s*defaultValue:\s*true/);
  assert.match(source, /TryGetOptionalStrictBool/);
  assert.match(source, /new SubTransaction\(doc\)/);
});

test("schedule export requires a target and defaults to no overwrite", () => {
  const scheduleExport = schema("revit_export_schedule");
  assert.equal(scheduleExport.safeParse({}).success, false);
  assert.equal(
    scheduleExport.safeParse({ schedule_name: "   " }).success,
    false
  );
  const parsed = scheduleExport.safeParse({ schedule_id: 123 });
  assert.equal(parsed.success, true);
  assert.equal(parsed.data.overwrite, false);
  assert.equal(parsed.data.max_rows, 50_000);
  assert.equal(
    scheduleExport.safeParse({
      schedule_id: 123,
      max_rows: 200_001,
    }).success,
    false
  );
});

test("view export defaults safely and forwards idempotency", async () => {
  const viewExport = tools.get("revit_export_view");
  assert.ok(viewExport);
  assert.equal(viewExport.config.annotations.readOnlyHint, false);
  assert.equal(viewExport.config.annotations.idempotentHint, true);
  const input = schema("revit_export_view");

  const defaults = input.safeParse({});
  assert.equal(defaults.success, true);
  assert.equal(defaults.data.format, "png");
  assert.equal(defaults.data.overwrite, false);
  assert.equal(input.safeParse({ view_name: "   " }).success, false);
  assert.equal(input.safeParse({ format: "pdf" }).success, false);
  assert.equal(input.safeParse({ overwrite: "false" }).success, false);

  const parsed = input.safeParse({
    view_name: " Level 1 ",
    format: "jpg",
    idempotency_key: "view-export-key",
  });
  assert.equal(parsed.success, true);
  assert.equal(parsed.data.view_name, "Level 1");
  const result = await viewExport.handler(parsed.data);
  const payload = JSON.parse(result.content[0].text);
  assert.equal(payload.command, "export_view");
  assert.equal(payload.params.overwrite, false);
  assert.equal(payload.params.idempotency_key, "view-export-key");
});

test("view selection and temporary visibility share the C# 500-ID cap", () => {
  for (const name of ["revit_isolate_elements", "revit_select_elements"]) {
    const viewTool = tools.get(name);
    assert.ok(viewTool);
    assert.equal(
      parseInput(viewTool, {
        element_ids: Array.from({ length: 500 }, (_, index) => index + 1),
      }).success,
      true
    );
    assert.equal(
      parseInput(viewTool, {
        element_ids: Array.from({ length: 501 }, (_, index) => index + 1),
      }).success,
      false
    );
  }
});

test("ElementId inputs preserve 64-bit values as decimal strings", async () => {
  const setView = tools.get("revit_set_active_view");
  assert.ok(setView);
  const unsafeNumericId = 9_007_199_254_740_992;
  assert.equal(
    parseInput(setView, { view_id: unsafeNumericId }).success,
    false
  );

  const parsed = parseInput(setView, {
    view_id: "9007199254740993",
    idempotency_key: "large-view-id",
  });
  assert.equal(parsed.success, true);
  const result = await setView.handler(parsed.data);
  const payload = JSON.parse(result.content[0].text);
  assert.equal(payload.params.view_id, "9007199254740993");
});

test("batch parameter shapes are exclusive and enforce value modes", () => {
  const batch = schema("revit_batch_modify_parameters");
  assert.equal(batch.safeParse({}).success, false);
  assert.equal(
    batch.safeParse({
      modifications: [
        {
          element_id: 1,
          parameter_name: "Length",
          value: 2,
          value_mode: "display",
        },
      ],
      value_mode: "internal",
    }).success,
    true
  );
  assert.equal(
    batch.safeParse({
      modifications: [
        { element_id: 1, parameter_name: "Mark", value: "A" },
      ],
      element_ids: [1],
      parameters: { Mark: "B" },
    }).success,
    false
  );
  assert.equal(
    batch.safeParse({
      element_ids: [1, 2],
      parameters: { Length: 250 },
      value_mode: "display",
    }).success,
    true
  );
  assert.equal(
    batch.safeParse({
      element_ids: [1],
      parameters: { Length: 250 },
      value_mode: "metric",
    }).success,
    false
  );
});

test("create and visualize mutation inputs reject raw non-finite and non-boolean values", async () => {
  const wall = tools.get("revit_create_wall");
  assert.ok(wall);
  const validWall = {
    start_x: 0,
    start_y: 0,
    end_x: 10,
    end_y: 0,
  };
  for (const input of [
    { ...validWall, start_x: Number.NaN },
    { ...validWall, end_y: Number.POSITIVE_INFINITY },
    { ...validWall, height: Number.NEGATIVE_INFINITY },
    { ...validWall, height: null },
    { ...validWall, structural: "false" },
    { ...validWall, structural: null },
  ]) {
    assert.equal(parseInput(wall, input).success, false);
  }

  const floor = schema("revit_create_floor");
  for (const input of [
    { min_x: 0, min_y: 0, max_x: Number.NaN, max_y: 10 },
    { min_x: 0, min_y: 0, max_x: 10 },
    {
      points: [
        { x: 0, y: 0 },
        { x: Number.POSITIVE_INFINITY, y: 0 },
        { x: 0, y: 10 },
      ],
    },
    {
      points: [
        { x: 0, y: 0 },
        { x: 10, y: 0 },
        { x: 0, y: 10 },
      ],
      structural: "true",
    },
  ]) {
    assert.equal(floor.safeParse(input).success, false);
  }

  const floorTool = tools.get("revit_create_floor");
  for (const [input, absentKeys] of [
    [
      { min_x: 0, min_y: 0, max_x: 10, max_y: 10 },
      ["points"],
    ],
    [
      {
        points: [
          { x: 0, y: 0 },
          { x: 10, y: 0 },
          { x: 0, y: 10 },
        ],
      },
      ["min_x", "min_y", "max_x", "max_y"],
    ],
  ]) {
    const parsed = floor.safeParse(input);
    assert.equal(parsed.success, true);
    const result = await floorTool.handler(parsed.data);
    const payload = JSON.parse(result.content[0].text);
    for (const key of absentKeys) {
      assert.equal(key in payload.params, false);
    }
  }

  const tag = schema("revit_tag_by_filter");
  for (const input of [
    { category: "Walls", offset_x_feet: Number.NaN },
    { category: "Walls", offset_y_feet: Number.POSITIVE_INFINITY },
    { category: "Walls", offset_x_feet: null },
    { category: "Walls", has_leader: "false" },
  ]) {
    assert.equal(tag.safeParse(input).success, false);
  }

  const color = schema("revit_apply_color_filter");
  assert.equal(
    color.safeParse({ category: "Walls", surface_fill: "false" }).success,
    false
  );
  assert.equal(
    color.safeParse({ category: "Walls", halftone: null }).success,
    false
  );

  const pipe = schema("revit_create_pipe_run");
  const pipePoints = [
    { e: 1, n: 2, z: 3 },
    { e: 4, n: 5, z: 6 },
  ];
  assert.equal(
    pipe.safeParse({ points: pipePoints, connect_elbows: "false" }).success,
    false
  );
  assert.equal(
    pipe.safeParse({
      points: pipePoints,
      allow_identity_transform: null,
    }).success,
    false
  );
});

test("parameter mutation schemas reject non-finite values and loose booleans", () => {
  const modify = tools.get("revit_modify_element_parameter");
  assert.ok(modify);
  const base = {
    element_id: 1,
    parameter_name: "Length",
  };
  for (const input of [
    { ...base, value: Number.NaN },
    { ...base, value: Number.POSITIVE_INFINITY },
    { ...base, value: null },
    { ...base, value: 1, is_type_param: "false" },
  ]) {
    assert.equal(parseInput(modify, input).success, false);
  }

  const batch = schema("revit_batch_modify_parameters");
  for (const input of [
    {
      element_ids: [1],
      parameters: { Length: Number.NaN },
    },
    {
      element_ids: [1],
      parameters: { Length: 1 },
      only_if_empty: "false",
    },
    {
      modifications: [
        {
          element_id: 1,
          parameter_name: "Length",
          value: 1,
          is_type_param: null,
        },
      ],
    },
  ]) {
    assert.equal(batch.safeParse(input).success, false);
  }
});

test("raw C# mutation commands use fail-closed numeric and boolean validators", () => {
  const helper = readFileSync(
    new URL(
      "../../../../commandset/Helpers/RawParameterValidation.cs",
      import.meta.url
    ),
    "utf8"
  );
  assert.match(helper, /TryGetRequiredFiniteDouble/);
  assert.match(helper, /TryGetOptionalFiniteDouble/);
  assert.match(helper, /TryGetOptionalStrictBool/);
  assert.match(helper, /TryConvertFiniteParameterDouble/);
  assert.match(helper, /double\.IsNaN/);
  assert.match(helper, /double\.IsInfinity/);

  const sources = new Map(
    [
      ["CreateWall", "Create/CreateWallCommand.cs"],
      ["CreateFloor", "Create/CreateFloorCommand.cs"],
      ["TagByFilter", "Create/TagByFilterCommand.cs"],
      ["CreatePipeRun", "Create/CreatePipeRunCommand.cs"],
      ["ModifyElementParameter", "Modify/ModifyElementParameterCommand.cs"],
      ["BatchModifyParameters", "Modify/BatchModifyParametersCommand.cs"],
      ["ApplyColorFilter", "View/ApplyColorFilterCommand.cs"],
    ].map(([name, path]) => [
      name,
      readFileSync(
        new URL(`../../../../commandset/Commands/${path}`, import.meta.url),
        "utf8"
      ),
    ])
  );

  assert.match(
    sources.get("CreateWall"),
    /TryGetRequiredFiniteDouble\(\s*parameters,\s*"start_x"/
  );
  assert.match(
    sources.get("CreateWall"),
    /TryGetOptionalFiniteDouble\(\s*parameters,\s*"height"/
  );
  assert.match(sources.get("CreateFloor"), /TryBuildPolygonBoundary/);
  assert.match(
    sources.get("CreateFloor"),
    /rectangleCount != rectangleKeys\.Length/
  );
  for (const sourceName of [
    "CreateWall",
    "CreateFloor",
    "TagByFilter",
    "CreatePipeRun",
    "ModifyElementParameter",
    "BatchModifyParameters",
    "ApplyColorFilter",
  ]) {
    assert.match(
      sources.get(sourceName),
      /TryGetOptionalStrictBool/,
      `${sourceName} should use strict raw boolean validation`
    );
  }
  assert.match(
    sources.get("TagByFilter"),
    /TryGetOptionalFiniteDouble\(\s*parameters,\s*"offset_x_feet"/
  );
  assert.match(
    sources.get("ModifyElementParameter"),
    /TryConvertFiniteParameterDouble/
  );
  assert.match(sources.get("BatchModifyParameters"), /GetValueValidationError/);
});

test("side-effect handlers forward stable idempotency keys", async () => {
  const cases = [
    ["revit_create_wall", {
      start_x: 0,
      start_y: 0,
      end_x: 10,
      end_y: 0,
      idempotency_key: "wall-key",
    }],
    ["revit_set_active_view", {
      view_id: 1,
      idempotency_key: "view-key",
    }],
    ["revit_apply_color_filter", {
      category: "Walls",
      idempotency_key: "color-key",
    }],
    ["revit_export_schedule", {
      schedule_id: 1,
      idempotency_key: "export-key",
    }],
    ["revit_export_view", {
      view_id: 1,
      idempotency_key: "view-export-key",
    }],
    ["revit_place_family", {
      placements: [{ type_id: 1, point: [0, 0, 0] }],
      idempotency_key: "family-key",
    }],
    ["revit_place_views_on_sheet", {
      sheet_id: 1,
      placements: [{ view_id: 2, point: [0, 0] }],
      idempotency_key: "sheet-key",
    }],
    ["revit_modify_wall_height_to_linked_soffit", {
      element_ids: [1],
      idempotency_key: "soffit-key",
    }],
  ];

  for (const [name, input] of cases) {
    const tool = tools.get(name);
    const parsed = parseInput(tool, input);
    assert.equal(parsed.success, true);
    const result = await tool.handler(parsed.data);
    const payload = JSON.parse(result.content[0].text);
    assert.equal(
      payload.params.idempotency_key,
      input.idempotency_key,
      `${name} should forward idempotency_key`
    );
  }
});
