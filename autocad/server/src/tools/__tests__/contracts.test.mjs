import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { z } from "zod";

import { SERVER_VERSION } from "../../../dist/constants.js";
import { registerCreateTools } from "../../../dist/tools/create.js";
import { registerQueryTools } from "../../../dist/tools/query.js";

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
