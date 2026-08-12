import test from "node:test";
import assert from "node:assert/strict";
import { mkdtemp, mkdir, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { WebSocketServer } from "ws";
import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";

import { RevitSessionRegistry } from "../../../dist/services/session-registry.js";
import { RevitWebSocketClient } from "../../../dist/services/websocket-client.js";
import { registerSessionTools } from "../../../dist/tools/sessions.js";

const NOW = Date.parse("2026-08-12T12:00:00.000Z");
const FINGERPRINT_A = "a".repeat(64);
const FINGERPRINT_B = "b".repeat(64);
const FINGERPRINT_C = "c".repeat(64);

test("session tools expose list/set/get with strict target selection input", async () => {
  const tools = new Map();
  const server = {
    registerTool(name, config, handler) {
      tools.set(name, { config, handler });
    },
  };
  const client = {
    async getLiveSessions() {
      return { sessions: [], ignored_entries: [] };
    },
    getSelectedTarget() {
      return null;
    },
    clearTarget() {},
    async selectTarget() {
      throw new Error("not used");
    },
  };

  registerSessionTools(server, client);
  assert.deepEqual(
    [...tools.keys()],
    ["revit_list_sessions", "revit_set_target", "revit_get_target"],
  );

  const schema = z.object(tools.get("revit_set_target").config.inputSchema);
  assert.equal(schema.safeParse({ session_id: "session-a" }).success, true);
  assert.equal(schema.safeParse({ clear: true }).success, true);
  assert.equal(schema.safeParse({}).success, true);
  assert.equal(
    schema.safeParse({ session_id: "session-a", clear: true }).success,
    true,
  );
  assert.equal((await tools.get("revit_set_target").handler({})).isError, true);
  assert.equal(
    (
      await tools
        .get("revit_set_target")
        .handler({ session_id: "session-a", clear: true })
    ).isError,
    true,
  );
  assert.equal(
    tools.get("revit_list_sessions").config.annotations.readOnlyHint,
    true,
  );
  assert.equal(
    tools.get("revit_set_target").config.annotations.destructiveHint,
    false,
  );
});

test("MCP advertises set-target fields and enforces wire input", async (t) => {
  const server = new McpServer({
    name: "session-schema-test",
    version: "1.0.0",
  });
  const client = new Client({
    name: "session-schema-client",
    version: "1.0.0",
  });
  registerSessionTools(server, {
    async getLiveSessions() {
      return { sessions: [], ignored_entry_count: 0, ignored_entries: [] };
    },
    getSelectedTarget() {
      return null;
    },
    clearTarget() {},
    async selectTarget(sessionId) {
      return {
        session_id: sessionId,
        document_fingerprint: FINGERPRINT_A,
        document_title: "A.rvt",
        document_path: "C:\\Models\\A.rvt",
        pid: 1,
        selected_at_utc: new Date().toISOString(),
      };
    },
  });
  const [clientTransport, serverTransport] =
    InMemoryTransport.createLinkedPair();
  await Promise.all([
    server.connect(serverTransport),
    client.connect(clientTransport),
  ]);
  t.after(async () => {
    await Promise.all([client.close(), server.close()]);
  });

  const listed = await client.listTools();
  const setTarget = listed.tools.find(
    (tool) => tool.name === "revit_set_target",
  );
  assert.ok(setTarget?.inputSchema?.properties?.session_id);
  assert.ok(setTarget?.inputSchema?.properties?.clear);

  const valid = await client.callTool({
    name: "revit_set_target",
    arguments: { session_id: "session-a" },
  });
  assert.equal(valid.isError, undefined);
  const invalidType = await client.callTool({
    name: "revit_set_target",
    arguments: { clear: "yes" },
  });
  assert.equal(invalidType.isError, true);
  const invalidSemantic = await client.callTool({
    name: "revit_set_target",
    arguments: {},
  });
  assert.equal(invalidSemantic.isError, true);
});

test("registry routing pins an explicit session and fails on fingerprint drift", async (t) => {
  const registryDirectory = await mkdtemp(
    join(tmpdir(), "revit-mcp-session-test-"),
  );
  const first = await startMockPlugin("first");
  const second = await startMockPlugin("second");

  t.after(async () => {
    router.disconnect();
    await Promise.all([first.close(), second.close()]);
    await rm(registryDirectory, { recursive: true, force: true });
  });

  await writeRecord(
    registryDirectory,
    sessionRecord(101, first.port, "session-a", FINGERPRINT_A, "A.rvt"),
  );
  await writeRecord(
    registryDirectory,
    sessionRecord(202, second.port, "session-b", FINGERPRINT_B, "B.rvt"),
  );
  await writeRecord(
    registryDirectory,
    sessionRecord(
      303,
      second.port,
      "stale-session",
      FINGERPRINT_C,
      "Stale.rvt",
      "2026-08-12T11:58:00.000Z",
    ),
  );
  await writeFile(join(registryDirectory, "bad.json"), "not-json", "utf8");
  await writeFile(join(registryDirectory, "606.json"), "not-json", "utf8");

  const registry = new RevitSessionRegistry({
    directory: registryDirectory,
    now: () => NOW,
    isPidAlive: () => true,
  });
  const router = new RevitWebSocketClient({ registry });

  const discovery = await router.getLiveSessions();
  assert.equal(discovery.sessions.length, 2);
  assert.deepEqual(
    discovery.sessions.map((session) => session.session_id),
    ["session-a", "session-b"],
  );
  assert.equal(discovery.ignored_entries.length, 3);

  const ambiguous = await router.sendCommand("create_wall", {
    idempotency_key: "ambiguous-write",
  });
  assert.equal(ambiguous.status, "error");
  assert.equal(ambiguous.error?.code, "TARGET_SELECTION_REQUIRED");
  assert.equal(first.requests.length, 0);
  assert.equal(second.requests.length, 0);

  const selected = await router.selectTarget("session-b");
  assert.equal(selected.document_fingerprint, FINGERPRINT_B);
  assert.equal(second.requests.length, 1);
  assert.equal(second.requests[0].command, "ping");
  assert.equal(second.requests[0].target_session_id, "session-b");
  assert.equal(second.requests[0].expected_document_fingerprint, FINGERPRINT_B);
  const routed = await router.sendCommand("get_levels");
  assert.equal(routed.status, "success");
  assert.equal(routed.data.plugin, "second");
  assert.equal(first.requests.length, 0);
  assert.equal(second.requests.length, 2);
  assert.equal(second.requests[1].target_session_id, "session-b");
  assert.equal(second.requests[1].expected_document_fingerprint, FINGERPRINT_B);
  assert.deepEqual(second.requests[1].params, {});

  await writeRecord(
    registryDirectory,
    sessionRecord(202, second.port, "session-b", FINGERPRINT_C, "Changed.rvt"),
  );
  const drift = await router.sendCommand("get_levels");
  assert.equal(drift.status, "error");
  assert.equal(drift.error?.code, "TARGET_DOCUMENT_MISMATCH");
  assert.equal(second.requests.length, 2);
});

test("one live registered session auto-routes with both wire guards", async (t) => {
  const registryDirectory = await mkdtemp(
    join(tmpdir(), "revit-mcp-single-session-test-"),
  );
  const plugin = await startMockPlugin("single");
  const registry = new RevitSessionRegistry({
    directory: registryDirectory,
    now: () => NOW,
    isPidAlive: () => true,
  });
  const router = new RevitWebSocketClient({ registry });

  t.after(async () => {
    router.disconnect();
    await plugin.close();
    await rm(registryDirectory, { recursive: true, force: true });
  });

  await writeRecord(
    registryDirectory,
    sessionRecord(404, plugin.port, "only-session", FINGERPRINT_A, "Only.rvt"),
  );
  const response = await router.sendCommand("get_project_info");
  assert.equal(response.status, "success");
  assert.equal(plugin.requests.length, 2);
  assert.equal(plugin.requests[0].target_session_id, "only-session");
  assert.equal(plugin.requests[0].expected_document_fingerprint, FINGERPRINT_A);
  assert.equal(router.getSelectedTarget()?.session_id, "only-session");
  assert.equal(router.getSelectedTarget()?.document_fingerprint, FINGERPRINT_A);

  await writeRecord(
    registryDirectory,
    sessionRecord(
      404,
      plugin.port,
      "only-session",
      FINGERPRINT_B,
      "Changed.rvt",
    ),
  );
  const drift = await router.sendCommand("get_project_info");
  assert.equal(drift.status, "error");
  assert.equal(drift.error?.code, "TARGET_DOCUMENT_MISMATCH");
  assert.equal(plugin.requests.length, 2);
});

test("single-session auto-route proves identity before dispatch", async (t) => {
  const registryDirectory = await mkdtemp(
    join(tmpdir(), "revit-mcp-single-proof-test-"),
  );
  const plugin = await startMockPlugin("non-echoing", (request) => ({
    id: request.id,
    status: "success",
    data: { plugin: "non-echoing", command: request.command },
  }));
  const registry = new RevitSessionRegistry({
    directory: registryDirectory,
    now: () => NOW,
    isPidAlive: () => true,
  });
  const router = new RevitWebSocketClient({ registry });

  t.after(async () => {
    router.disconnect();
    await plugin.close();
    await rm(registryDirectory, { recursive: true, force: true });
  });
  await writeRecord(
    registryDirectory,
    sessionRecord(
      606,
      plugin.port,
      "unproven-session",
      FINGERPRINT_A,
      "Unproven.rvt",
    ),
  );

  const response = await router.sendCommand("create_wall", {
    idempotency_key: "must-not-dispatch",
  });
  assert.equal(response.status, "error");
  assert.equal(response.error?.code, "TARGET_SESSION_MISMATCH");
  assert.equal(plugin.requests.length, 1);
  assert.equal(plugin.requests[0].command, "ping");
});

test("set target commits only after the guarded plugin ping succeeds", async (t) => {
  const registryDirectory = await mkdtemp(
    join(tmpdir(), "revit-mcp-target-verification-test-"),
  );
  const plugin = await startMockPlugin("rejecting", (request) => ({
    id: request.id,
    status: "error",
    error: {
      code: "TARGET_DOCUMENT_MISMATCH",
      message: "Document changed before selection.",
      recoverable: true,
      suggestion: "List sessions again.",
    },
  }));
  const registry = new RevitSessionRegistry({
    directory: registryDirectory,
    now: () => NOW,
    isPidAlive: () => true,
  });
  const router = new RevitWebSocketClient({ registry });

  t.after(async () => {
    router.disconnect();
    await plugin.close();
    await rm(registryDirectory, { recursive: true, force: true });
  });
  await writeRecord(
    registryDirectory,
    sessionRecord(
      505,
      plugin.port,
      "reject-session",
      FINGERPRINT_A,
      "Reject.rvt",
    ),
  );

  await assert.rejects(
    router.selectTarget("reject-session"),
    (error) => error?.code === "TARGET_DOCUMENT_MISMATCH",
  );
  assert.equal(router.getSelectedTarget(), null);
  assert.equal(plugin.requests.length, 1);
  assert.equal(plugin.requests[0].command, "ping");
  assert.equal(plugin.requests[0].target_session_id, "reject-session");
  assert.equal(plugin.requests[0].expected_document_fingerprint, FINGERPRINT_A);
});

test("no live registry entries retain the legacy single-port route", async (t) => {
  const registryDirectory = await mkdtemp(
    join(tmpdir(), "revit-mcp-legacy-test-"),
  );
  const legacy = await startMockPlugin("legacy");
  const registry = new RevitSessionRegistry({
    directory: registryDirectory,
    now: () => NOW,
    isPidAlive: () => true,
  });
  const router = new RevitWebSocketClient({
    registry,
    legacyUrl: `ws://127.0.0.1:${legacy.port}`,
  });

  t.after(async () => {
    router.disconnect();
    await legacy.close();
    await rm(registryDirectory, { recursive: true, force: true });
  });

  const response = await router.sendCommand("ping");
  assert.equal(response.status, "success");
  assert.equal(response.data.plugin, "legacy");
  assert.equal(legacy.requests.length, 1);
  assert.equal("target_session_id" in legacy.requests[0], false);
  assert.equal("expected_document_fingerprint" in legacy.requests[0], false);
});

test("missing registry fails closed for a registry-aware legacy endpoint", async (t) => {
  const registryDirectory = await mkdtemp(
    join(tmpdir(), "revit-mcp-missing-registry-test-"),
  );
  const plugin = await startMockPlugin("new-without-registry", (request) => ({
    id: request.id,
    status: "success",
    data: {
      plugin: "new-without-registry",
      command: request.command,
      session_id: "hidden-session",
      document_fingerprint: FINGERPRINT_A,
    },
  }));
  const registry = new RevitSessionRegistry({
    directory: registryDirectory,
    now: () => NOW,
    isPidAlive: () => true,
  });
  const router = new RevitWebSocketClient({
    registry,
    legacyUrl: `ws://127.0.0.1:${plugin.port}`,
  });

  t.after(async () => {
    router.disconnect();
    await plugin.close();
    await rm(registryDirectory, { recursive: true, force: true });
  });

  const response = await router.sendCommand("create_wall", {
    idempotency_key: "blocked-no-registry",
  });
  assert.equal(response.status, "error");
  assert.equal(response.error?.code, "TARGET_SELECTION_REQUIRED");
  assert.equal(plugin.requests.length, 1);
  assert.equal(plugin.requests[0].command, "ping");
});

function sessionRecord(
  pid,
  port,
  sessionId,
  fingerprint,
  title,
  lastSeen = "2026-08-12T12:00:00.000Z",
) {
  return {
    schema_version: 1,
    session_id: sessionId,
    pid,
    port,
    revit_version: "2025",
    revit_build: "25.3.0.46",
    active_document_title: title,
    active_document_path: `C:\\Models\\${title}`,
    document_fingerprint: fingerprint,
    started_at_utc: "2026-08-12T11:00:00.000Z",
    last_seen_utc: lastSeen,
  };
}

async function writeRecord(directory, record) {
  await mkdir(directory, { recursive: true });
  await writeFile(
    join(directory, `${record.pid}.json`),
    JSON.stringify(record),
    "utf8",
  );
}

async function startMockPlugin(name, responseFor) {
  const requests = [];
  const server = new WebSocketServer({ host: "127.0.0.1", port: 0 });
  await new Promise((resolve, reject) => {
    server.once("listening", resolve);
    server.once("error", reject);
  });
  server.on("connection", (socket) => {
    socket.on("message", (raw) => {
      const request = JSON.parse(raw.toString());
      requests.push(request);
      const response = responseFor
        ? responseFor(request)
        : {
            id: request.id,
            status: "success",
            data: {
              plugin: name,
              command: request.command,
              ...(request.command === "ping" && request.target_session_id
                ? {
                    session_id: request.target_session_id,
                    document_fingerprint: request.expected_document_fingerprint,
                  }
                : {}),
            },
          };
      socket.send(JSON.stringify(response));
    });
  });
  const address = server.address();
  assert.equal(typeof address, "object");
  return {
    port: address.port,
    requests,
    close: () =>
      new Promise((resolve, reject) => {
        for (const client of server.clients) client.terminate();
        server.close((error) => (error ? reject(error) : resolve()));
      }),
  };
}
