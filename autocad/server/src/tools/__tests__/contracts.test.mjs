import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { z } from "zod";

import { SERVER_VERSION } from "../../../dist/constants.js";
import { registerCreateTools } from "../../../dist/tools/create.js";
import { registerBlockTools } from "../../../dist/tools/blocks.js";
import { registerExportTools } from "../../../dist/tools/export.js";
import { registerModifyTools } from "../../../dist/tools/modify.js";
import { registerQueryTools } from "../../../dist/tools/query.js";
import { registerScriptTools } from "../../../dist/tools/script.js";
import { registerUtilityTools } from "../../../dist/tools/utility.js";

test("advertised AutoCAD MCP version matches package metadata", () => {
  const packageJson = JSON.parse(
    readFileSync(new URL("../../../package.json", import.meta.url), "utf8")
  );
  assert.equal(SERVER_VERSION, packageJson.version);
});

function collectTools(registrar) {
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
  registrar(server, client);
  return tools;
}

function collectCreateTool() {
  return collectTools(registerCreateTools).get("cad_create_line");
}

function inputSchema(tool) {
  return typeof tool.config.inputSchema.safeParse === "function"
    ? tool.config.inputSchema
    : z.object(tool.config.inputSchema);
}

test("cad_create_line exposes and forwards a bounded idempotency key", async () => {
  const tool = collectCreateTool();
  assert.ok(tool);
  const schema = inputSchema(tool);

  assert.equal(
    schema.safeParse({
      start: [0, 0],
      end: [1, 1],
      idempotency_key: "",
    }).success,
    false,
  );
  assert.equal(
    schema.safeParse({
      start: [0, 0],
      end: [1, 1],
      idempotency_key: "x".repeat(513),
    }).success,
    false,
  );

  const parsed = schema.safeParse({
    start: [0, 0],
    end: [1, 1],
    idempotency_key: "line-retry-key",
  });
  assert.equal(parsed.success, true);

  const result = await tool.handler(parsed.data);
  const payload = JSON.parse(result.content[0].text);
  assert.equal(payload.params.idempotency_key, "line-retry-key");
  assert.equal(tool.config.annotations.idempotentHint, false);
});

test("cad_create_line coordinates and optional layer fail closed", async () => {
  const tool = collectCreateTool();
  assert.ok(tool);
  const schema = inputSchema(tool);

  for (const input of [
    { start: [0], end: [1, 1] },
    { start: [0, 0, 0, 0], end: [1, 1] },
    { start: [0, Number.POSITIVE_INFINITY], end: [1, 1] },
    { start: [0, Number.NaN], end: [1, 1] },
    { start: [0, 0], end: [1, Number.NEGATIVE_INFINITY] },
    { start: [0, 0], end: [1, 1], layer: "" },
    { start: [0, 0], end: [1, 1], layer: "   " },
    { start: [0, 0], end: [1, 1], layer: null },
    { start: [0, 0], end: [1, 1], layer: 42 },
  ]) {
    assert.equal(
      schema.safeParse(input).success,
      false,
      "expected create_line input rejection",
    );
  }

  const parsed = schema.safeParse({
    start: [0, 0],
    end: [1, 1, 2],
    layer: "  STRUCTURE  ",
  });
  assert.equal(parsed.success, true);
  assert.equal(parsed.data.layer, "STRUCTURE");

  const result = await tool.handler(parsed.data);
  const payload = JSON.parse(result.content[0].text);
  assert.equal(payload.params.layer, "STRUCTURE");

  const commandSource = readFileSync(
    new URL(
      "../../../../commandset/Commands/CreateLineCommand.cs",
      import.meta.url,
    ),
    "utf8",
  );
  assert.match(commandSource, /list\.Count > 3/);
  assert.match(commandSource, /double\.IsNaN\(number\)/);
  assert.match(commandSource, /double\.IsInfinity\(number\)/);
  assert.match(
    commandSource,
    /'layer' must be a non-empty string when supplied/,
  );
});

test("cad_create_entities exposes all batch shapes and enforces caps", async () => {
  const tool = collectTools(registerCreateTools).get("cad_create_entities");
  assert.ok(tool);
  const schema = inputSchema(tool);

  const validEntities = [
    { type: "line", start: [0, 0], end: [1, 1] },
    { type: "polyline", points: [[0, 0], [1, 0], [1, 1]], closed: true },
    { type: "circle", center: [5, 5], radius: 2 },
    { type: "arc", center: [5, 5, 0], radius: 2, start_angle_deg: 0, end_angle_deg: 90 },
    { type: "text", position: [0, 0], contents: "A", height: 2.5 },
  ];

  assert.equal(schema.safeParse({ entities: validEntities }).success, true);
  assert.equal(schema.safeParse({ entities: [] }).success, false);
  assert.equal(
    schema.safeParse({
      entities: Array.from({ length: 201 }, () => ({
        type: "line",
        start: [0, 0],
        end: [1, 1],
      })),
    }).success,
    false,
  );
  assert.equal(
    schema.safeParse({
      entities: [{ type: "spline", points: [[0, 0], [1, 1]] }],
    }).success,
    false,
  );
  for (const idempotency_key of ["", "x".repeat(513)]) {
    assert.equal(
      schema.safeParse({ entities: validEntities, idempotency_key }).success,
      false,
    );
  }

  const parsed = schema.safeParse({
    entities: validEntities,
    idempotency_key: "entity-batch-retry",
  });
  assert.equal(parsed.success, true);
  const result = await tool.handler(parsed.data);
  const payload = JSON.parse(result.content[0].text);
  assert.equal(payload.command, "create_entities");
  assert.equal(payload.params.entities.length, 5);
  assert.equal(payload.params.idempotency_key, "entity-batch-retry");
});

test("cad_modify_entities enforces op-specific inputs, caps, and retry key", async () => {
  const tool = collectTools(registerModifyTools).get("cad_modify_entities");
  assert.ok(tool);
  const schema = inputSchema(tool);

  for (const input of [
    { op: "scale", handles: ["10"] },
    { op: "move", handles: ["10"] },
    { op: "copy", handles: ["10"] },
    { op: "rotate", handles: ["10"], origin: [0, 0] },
    { op: "rotate", handles: ["10"], angle_deg: 45 },
    { op: "erase", handles: [] },
    { op: "erase", handles: Array.from({ length: 501 }, (_, i) => `${i + 1}`) },
    { op: "erase", handles: ["10"], idempotency_key: "" },
    { op: "erase", handles: ["10"], idempotency_key: "x".repeat(513) },
  ]) {
    assert.equal(
      schema.safeParse(input).success,
      false,
      `expected rejection for ${input.op}`,
    );
  }

  const parsed = schema.safeParse({
    op: "rotate",
    handles: ["10", "2A4"],
    origin: [0, 0, 0],
    angle_deg: 90,
    idempotency_key: "modify-batch-retry",
  });
  assert.equal(parsed.success, true);
  const result = await tool.handler(parsed.data);
  const payload = JSON.parse(result.content[0].text);
  assert.equal(payload.command, "modify_entities");
  assert.deepEqual(payload.params.handles, ["10", "2A4"]);
  assert.equal(payload.params.angle_deg, 90);
});

test("cad_execute_script bounds code, mode, timeout, and idempotency key", async () => {
  const tool = collectTools(registerScriptTools).get("cad_execute_script");
  assert.ok(tool);
  const schema = inputSchema(tool);

  for (const input of [
    { code: "" },
    { code: "x".repeat(50_001) },
    { code: "1", mode: "unsafe" },
    { code: "1", timeout_ms: 4_999 },
    { code: "1", timeout_ms: 300_001 },
    { code: "1", idempotency_key: "" },
    { code: "1", idempotency_key: "x".repeat(513) },
  ]) {
    assert.equal(schema.safeParse(input).success, false);
  }

  const parsed = schema.safeParse({
    code: "print(db.Filename); 42",
    mode: "query",
    timeout_ms: 60_000,
    idempotency_key: "script-retry",
  });
  assert.equal(parsed.success, true);
  const result = await tool.handler(parsed.data);
  const payload = JSON.parse(result.content[0].text);
  assert.equal(payload.command, "execute_script");
  assert.equal(payload.params.mode, "query");
  assert.equal(payload.params.idempotency_key, "script-retry");
});

test("cad_blocks separates read-only listing from bounded retry-safe inserts", async () => {
  const tool = collectTools(registerBlockTools).get("cad_blocks");
  assert.ok(tool);
  const schema = inputSchema(tool);

  const listDefaults = schema.safeParse({});
  assert.equal(listDefaults.success, true);
  assert.equal(listDefaults.data.op, "list");
  assert.equal(schema.safeParse({ op: "list", block_name: "Door" }).success, false);
  assert.equal(schema.safeParse({ op: "list", idempotency_key: "unused" }).success, false);

  for (const input of [
    { op: "insert", insertions: [{ point: [0, 0] }] },
    { op: "insert", block_name: "Door" },
    { op: "insert", block_name: "Door", insertions: [] },
    {
      op: "insert",
      block_name: "Door",
      insertions: Array.from({ length: 51 }, () => ({ point: [0, 0] })),
    },
    { op: "insert", block_name: "Door", insertions: [{ point: [0] }] },
    { op: "insert", block_name: "Door", insertions: [{ point: [0, Infinity] }] },
    { op: "insert", block_name: "Door", insertions: [{ point: [0, 0], scale: 0 }] },
    { op: "insert", block_name: "Door", insertions: [{ point: [0, 0], rotation_deg: NaN }] },
  ]) {
    assert.equal(schema.safeParse(input).success, false);
  }

  const parsed = schema.safeParse({
    op: "insert",
    block_name: "  Door Tag  ",
    insertions: [{ point: [1, 2, 3] }, { point: [4, 5], scale: 2, rotation_deg: 90 }],
    idempotency_key: "block-insert-retry",
  });
  assert.equal(parsed.success, true);
  assert.equal(parsed.data.block_name, "Door Tag");
  assert.equal(parsed.data.insertions[0].scale, 1);
  assert.equal(parsed.data.insertions[0].rotation_deg, 0);

  const result = await tool.handler(parsed.data);
  const payload = JSON.parse(result.content[0].text);
  assert.equal(payload.command, "blocks");
  assert.equal(payload.params.op, "insert");
  assert.equal(payload.params.insertions.length, 2);
  assert.equal(payload.params.idempotency_key, "block-insert-retry");
  assert.match(tool.config.description, /case-insensitive EXACT matching/);
});

test("cad_plot_pdf defaults safely and forwards a stable retry key", async () => {
  const tool = collectTools(registerExportTools).get("cad_plot_pdf");
  assert.ok(tool);
  const schema = inputSchema(tool);

  const defaults = schema.safeParse({});
  assert.equal(defaults.success, true);
  assert.equal(defaults.data.overwrite, false);
  for (const input of [
    { layout_name: "   " },
    { output_path: "C:\\temp\\sheet.png" },
    { overwrite: "false" },
    { idempotency_key: "" },
    { idempotency_key: "x".repeat(513) },
  ]) {
    assert.equal(schema.safeParse(input).success, false);
  }

  const parsed = schema.safeParse({
    layout_name: "  A101  ",
    output_path: "C:\\temp\\A101.pdf",
    idempotency_key: "plot-retry",
  });
  assert.equal(parsed.success, true);
  const result = await tool.handler(parsed.data);
  const payload = JSON.parse(result.content[0].text);
  assert.equal(payload.command, "plot_pdf");
  assert.equal(payload.params.layout_name, "A101");
  assert.equal(payload.params.overwrite, false);
  assert.equal(payload.params.idempotency_key, "plot-retry");
});

test("cad_query_entities defaults to current space and forwards explicit scopes", async () => {
  const tool = collectTools(registerQueryTools).get("cad_query_entities");
  assert.ok(tool);
  const schema = inputSchema(tool);

  const defaults = schema.safeParse({});
  assert.equal(defaults.success, true);
  assert.equal(defaults.data.space, "current");
  assert.equal(schema.safeParse({ space: "all" }).success, false);
  assert.equal(schema.safeParse({ space: null }).success, false);

  const parsed = schema.safeParse({ space: "paper", summary_only: false });
  assert.equal(parsed.success, true);
  const result = await tool.handler(parsed.data);
  const payload = JSON.parse(result.content[0].text);
  assert.equal(payload.params.space, "paper");

  const source = readFileSync(
    new URL(
      "../../../../commandset/Commands/QueryEntitiesCommand.cs",
      import.meta.url,
    ),
    "utf8",
  );
  assert.match(source, /db\.CurrentSpaceId/);
  assert.match(source, /LayoutDictionaryId/);
  assert.match(source, /\["space"\] = searchSpace\.Kind/);
  assert.match(source, /cancellationToken\.ThrowIfCancellationRequested\(\)/);
});

test("CAD P1 commands expose post-commit/file verification and stay in scope", () => {
  const blocksSource = readFileSync(
    new URL(
      "../../../../commandset/Commands/BlocksCommand.cs",
      import.meta.url,
    ),
    "utf8",
  );
  const plotSource = readFileSync(
    new URL(
      "../../../../commandset/Commands/PlotPdfCommand.cs",
      import.meta.url,
    ),
    "utf8",
  );
  const pluginSource = readFileSync(
    new URL(
      "../../../../plugin/AutoCADMCPPlugin/AcadWebSocketServer.cs",
      import.meta.url,
    ),
    "utf8",
  );
  const indexSource = readFileSync(
    new URL("../../../src/index.ts", import.meta.url),
    "utf8",
  );

  assert.match(blocksSource, /Name => "blocks"/);
  assert.match(blocksSource, /MaxInsertions = 50/);
  assert.match(blocksSource, /db\.CurrentSpaceId/);
  assert.match(blocksSource, /AddDefaultAttributes/);
  assert.match(plotSource, /Name => "plot_pdf"/);
  assert.match(plotSource, /Path\.GetTempPath\(\)/);
  assert.match(plotSource, /File\.Move\(stagingPath, outputPath, overwrite\)/);
  assert.match(plotSource, /\["size_gt_zero"\] = finalInfo\.Length > 0/);
  assert.match(plotSource, /commitTransaction: false/);
  assert.match(pluginSource, /"plot_"/);
  assert.match(pluginSource, /FinalizeBlocksVerification/);
  assert.match(indexSource, /registerBlockTools\(server, wsClient\)/);
  assert.match(indexSource, /registerExportTools\(server, wsClient\)/);

  const allTools = new Map([
    ...collectTools(registerUtilityTools),
    ...collectTools(registerCreateTools),
    ...collectTools(registerModifyTools),
    ...collectTools(registerQueryTools),
    ...collectTools(registerScriptTools),
    ...collectTools(registerBlockTools),
    ...collectTools(registerExportTools),
  ]);
  assert.equal(allTools.size, 15);
  for (const forbidden of [
    "cad_list_sessions",
    "cad_create_room",
    "cad_draw_circle",
    "cad_create_civil_object",
    "cad_set_ucs",
  ]) {
    assert.equal(allTools.has(forbidden), false);
  }
});

test("grid schedule scope and tolerance fail closed", async () => {
  const tool = collectTools(registerQueryTools).get("cad_parse_grid_schedule");
  assert.ok(tool);
  const schema = inputSchema(tool);

  const defaults = schema.safeParse({});
  assert.equal(defaults.success, true);
  assert.equal(defaults.data.scope, "selection");

  for (const input of [
    { scope: "layer" },
    { scope: "layer", layer: "   " },
    { tolerance: null },
    { tolerance: 0 },
    { tolerance: -0.5 },
    { preview_rows: null },
    { preview_rows: 0 },
    { preview_rows: 21 },
  ]) {
    assert.equal(
      schema.safeParse(input).success,
      false,
      `expected rejection for ${JSON.stringify(input)}`,
    );
  }

  const parsed = schema.safeParse({
    scope: "layer",
    layer: "  SCHEDULE-GRID  ",
    tolerance: 0.5,
    preview_rows: 20,
  });
  assert.equal(parsed.success, true);
  assert.equal(parsed.data.layer, "SCHEDULE-GRID");
  const result = await tool.handler(parsed.data);
  const payload = JSON.parse(result.content[0].text);
  assert.equal(payload.params.scope, "layer");
  assert.equal(payload.params.layer, "SCHEDULE-GRID");
  assert.equal(payload.params.tolerance, 0.5);
});

test("table extraction bounds optional numeric inputs", () => {
  const tool = collectTools(registerQueryTools).get("cad_extract_table");
  assert.ok(tool);
  const schema = inputSchema(tool);

  for (const input of [
    { header_row: null },
    { header_row: -1 },
    { header_row: 2_147_483_648 },
    { limit: null },
    { limit: 0 },
    { limit: 21 },
  ]) {
    assert.equal(
      schema.safeParse(input).success,
      false,
      `expected rejection for ${JSON.stringify(input)}`,
    );
  }

  assert.equal(
    schema.safeParse({
      header_row: 0,
      limit: 20,
    }).success,
    true,
  );
});

test("AutoCAD listener shutdown tracks handlers and active sockets", () => {
  const source = readFileSync(
    new URL(
      "../../../../plugin/AutoCADMCPPlugin/AcadWebSocketServer.cs",
      import.meta.url,
    ),
    "utf8",
  );

  assert.match(source, /HashSet<WebSocket> Connections/);
  assert.match(source, /HashSet<Task> ConnectionTasks/);
  assert.match(source, /TrackConnectionTask\(run, task\)/);
  assert.match(source, /socket\.Abort\(\)/);
  assert.match(source, /ObserveListenTask\(run\)/);
  assert.match(
    source,
    /server state was reset for a safe restart/,
  );
});

test("create_line verification is provisional until post-commit reopen", () => {
  const commandSource = readFileSync(
    new URL(
      "../../../../commandset/Commands/CreateLineCommand.cs",
      import.meta.url,
    ),
    "utf8",
  );
  const pluginSource = readFileSync(
    new URL(
      "../../../../plugin/AutoCADMCPPlugin/AcadWebSocketServer.cs",
      import.meta.url,
    ),
    "utf8",
  );

  assert.match(commandSource, /\["phase"\] = "pre_commit"/);
  assert.match(commandSource, /\["commit_verified"\] = false/);
  assert.match(commandSource, /\["performed"\] = false/);

  const commitIndex = pluginSource.indexOf("tr.Commit();");
  const finalVerificationIndex = pluginSource.indexOf(
    "FinalizePostCommitVerification(",
    commitIndex,
  );
  assert.ok(commitIndex >= 0);
  assert.ok(finalVerificationIndex > commitIndex);
  assert.match(pluginSource, /StartOpenCloseTransaction\(\)/);
  assert.match(pluginSource, /\["phase"\] = "post_commit"/);
  assert.match(pluginSource, /\["commit_verified"\] = true/);
});

test("batch commands and xrefs have C# registrations and post-commit verification", () => {
  const createSource = readFileSync(
    new URL(
      "../../../../commandset/Commands/CreateEntitiesCommand.cs",
      import.meta.url,
    ),
    "utf8",
  );
  const modifySource = readFileSync(
    new URL(
      "../../../../commandset/Commands/ModifyEntitiesCommand.cs",
      import.meta.url,
    ),
    "utf8",
  );
  const scriptSource = readFileSync(
    new URL(
      "../../../../commandset/Commands/ExecuteScriptCommand.cs",
      import.meta.url,
    ),
    "utf8",
  );
  const infoSource = readFileSync(
    new URL(
      "../../../../commandset/Commands/GetDrawingInfoCommand.cs",
      import.meta.url,
    ),
    "utf8",
  );
  const pluginSource = readFileSync(
    new URL(
      "../../../../plugin/AutoCADMCPPlugin/AcadWebSocketServer.cs",
      import.meta.url,
    ),
    "utf8",
  );
  const indexSource = readFileSync(
    new URL("../../../src/index.ts", import.meta.url),
    "utf8",
  );
  const querySource = readFileSync(
    new URL(
      "../../../../commandset/Commands/QueryEntitiesCommand.cs",
      import.meta.url,
    ),
    "utf8",
  );

  assert.match(createSource, /Name => "create_entities"/);
  assert.match(createSource, /cancellationToken\.ThrowIfCancellationRequested\(\)/);
  assert.match(modifySource, /Name => "modify_entities"/);
  assert.match(modifySource, /MaxHandles = 500/);
  assert.match(scriptSource, /Name => "execute_script"/);
  assert.match(scriptSource, /AUTOCAD_MCP_ENABLE_SCRIPT/);
  assert.match(scriptSource, /MaxCodeLength = 50_000/);
  assert.match(scriptSource, /commitTransaction: mode == "modify"/);
  assert.match(infoSource, /\["xrefs"\] = xrefs/);
  assert.match(infoSource, /\["is_unresolved"\] = isUnresolved/);
  assert.match(pluginSource, /FinalizeCreateEntitiesVerification/);
  assert.match(pluginSource, /FinalizeModifyEntitiesVerification/);
  assert.match(pluginSource, /if \(result\.CommitTransaction\)/);
  assert.match(querySource, /\["handle"\] = handle/);
  assert.match(indexSource, /registerModifyTools\(server, wsClient\)/);
  assert.match(indexSource, /registerScriptTools\(server, wsClient\)/);
});
