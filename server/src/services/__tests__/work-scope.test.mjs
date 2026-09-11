import test from "node:test";
import assert from "node:assert/strict";
import { WebSocketServer } from "ws";
import { z } from "zod";
import { RevitWebSocketClient } from "../../../dist/services/websocket-client.js";
import { registerWorkScopeTools, WORK_SCOPE_INPUT } from "../../../dist/tools/work-scope.js";

test("work scope tool validates operation-specific inputs before dispatch", async () => {
  let handler;
  const sent = [];
  registerWorkScopeTools({ registerTool(_name, _config, fn) { handler = fn; } }, {
    async sendCommand(...args) { sent.push(args); return { status: "success", data: {} }; },
  });
  for (const params of [
    { op: "acquire", scope: "elements", label: "walls", element_ids: [1] },
    { op: "acquire", scope: "elements", label: "walls", idempotency_key: "a" },
    { op: "acquire", scope: "document", label: "all", idempotency_key: "a", element_ids: [1] },
    { op: "status", scope: "document" }, { op: "release", ttl_seconds: 300 },
    { op: "acquire", scope: "document", label: "all", idempotency_key: "a", lease_token: "a".repeat(32) },
  ]) assert.equal((await handler(params)).isError, true);
  assert.equal(sent.length, 0);
  await handler({ op: "status" });
  assert.equal(sent[0][3].sideEffect, false);
  await handler({ op: "acquire", scope: "elements", label: "walls", element_ids: [1], idempotency_key: "a" });
  assert.equal(sent[1][3].sideEffect, true);
  const schema = z.object(WORK_SCOPE_INPUT);
  for (const input of [{ op: "acquire", element_ids: [0] }, { op: "renew", ttl_seconds: 29 },
    { op: "renew", ttl_seconds: 1801 }, { op: "acquire", element_ids: [Number.MAX_SAFE_INTEGER + 1] },
    { op: "acquire", element_ids: Array(5001).fill(1) }])
    assert.equal(schema.safeParse(input).success, false);
});

async function setup(t, scopeFailure = () => undefined) {
  const server = new WebSocketServer({ host: "127.0.0.1", port: 0 });
  await new Promise((resolve) => server.once("listening", resolve));
  const requests = [];
  let ordinal = 1;
  server.on("connection", (socket) => socket.on("message", (raw) => {
    const req = JSON.parse(raw.toString()); requests.push(req);
    let data = {};
    let error;
    if (req.command === "ping") data = { session_id: req.target_session_id, document_fingerprint: req.expected_document_fingerprint };
  if (req.command === "work_scope" && req.params.op === "acquire") {
      if (req.params.idempotency_key === "conflict") error = { code: "WORK_SCOPE_CONFLICT", message: "busy", suggestion: "Choose another scope." };
      else data = { assignment: { lease_token: String(ordinal++).padStart(32, "0") } };
      if (req.params.idempotency_key === "malformed") data = null;
    }
    if (req.command === "modify_element_parameter" && req.params.value === "stale")
      error = { code: "WORK_SCOPE_STALE", message: "changed", suggestion: "Reacquire and query again." };
    if (req.command === "work_scope") {
      if (req.params.op === "status") data = { coordination_enabled: false, active_count: 0, assignments: [] };
      error = scopeFailure(req) ?? error;
    }
    socket.send(JSON.stringify({ id: req.id, status: error ? "error" : "success", ...(error ? { error } : { data }) }));
  }));
  const record = { session_id: "s1", pid: 123, port: server.address().port, host: "127.0.0.1",
    document_fingerprint: "a".repeat(64), active_document_title: "A", active_document_path: "C:\\A.rvt" };
  const registry = { async discover() { return { sessions: [{ ...record }], ignored_records: [] }; } };
  const clients = [new RevitWebSocketClient({ registry }), new RevitWebSocketClient({ registry })];
  t.after(async () => {
    clients.forEach((c) => c.disconnect());
    for (const socket of server.clients) socket.terminate();
    await new Promise((resolve) => server.close(resolve));
  });
  return { clients, requests, record };
}
const acquire = (key) => ({ op: "acquire", scope: "elements", label: key, element_ids: [1], idempotency_key: key });

test("two MCP processes keep owner identities and scope tokens isolated on actual WebSocket messages", async (t) => {
  const { clients: [a, b], requests } = await setup(t);
  const [ar, br] = await Promise.all([a.sendCommand("work_scope", acquire("a")), b.sendCommand("work_scope", acquire("b"))]);
  await Promise.all([a.sendCommand("modify_element_parameter", { value: "A" }), b.sendCommand("modify_element_parameter", { value: "B" })]);
  const [aw, bw] = ["A", "B"].map((v) => requests.find((r) => r.params.value === v));
  assert.notEqual(aw.agent_id, bw.agent_id);
  assert.equal(aw.work_scope_token, ar.data.assignment.lease_token);
  assert.equal(bw.work_scope_token, br.data.assignment.lease_token);
  assert.equal(aw.params.work_scope_token, undefined);
  assert.equal(aw.expected_document_fingerprint, "a".repeat(64));
});

test("concurrent acquire/write/release calls within one MCP use the correct token at dispatch", async (t) => {
  const { clients: [a], requests } = await setup(t);
  const [result] = await Promise.all([
    a.sendCommand("work_scope", acquire("a")),
    a.sendCommand("modify_element_parameter", { value: "first" }),
    a.sendCommand("work_scope", { op: "release" }),
    a.sendCommand("modify_element_parameter", { value: "after-release" }),
  ]);
  assert.equal(requests.find((r) => r.params.value === "first").work_scope_token, result.data.assignment.lease_token);
  assert.equal(requests.find((r) => r.params.op === "release").work_scope_token, result.data.assignment.lease_token);
  assert.equal(requests.find((r) => r.params.value === "after-release").work_scope_token, undefined);
});

test("failed acquisition and stale writes never silently detach the existing assignment", async (t) => {
  const { clients: [a], requests } = await setup(t);
  const result = await a.sendCommand("work_scope", acquire("a"));
  assert.equal((await a.sendCommand("work_scope", acquire("conflict"))).status, "error");
  assert.equal((await a.sendCommand("modify_element_parameter", { value: "stale" })).status, "error");
  await a.sendCommand("modify_element_parameter", { value: "after-error" });
  assert.equal(requests.find((r) => r.params.value === "after-error").work_scope_token, result.data.assignment.lease_token);
});

test("target document drift is blocked and tokens never leak to a newly pinned document", async (t) => {
  const { clients: [a], record, requests } = await setup(t);
  await a.sendCommand("work_scope", acquire("a"));
  record.document_fingerprint = "b".repeat(64);
  assert.equal((await a.sendCommand("modify_element_parameter", { value: "blocked" })).error.code, "TARGET_DOCUMENT_MISMATCH");
  assert.equal(requests.some((r) => r.params.value === "blocked"), false);
  await a.selectTarget("s1");
  await a.sendCommand("modify_element_parameter", { value: "new-doc" });
  assert.equal(requests.find((r) => r.params.value === "new-doc").work_scope_token, undefined);
});

test("missing session registry does not imply unsupported reservations", async () => {
  const client = new RevitWebSocketClient({ registry: { async discover() { return { sessions: [] }; } } });
  const result = await client.sendCommand("work_scope", acquire("legacy"));
  assert.equal(result.error.code, "TARGET_SELECTION_REQUIRED");
  client.disconnect();
});

const unsupported = () => ({ code: "VALIDATION_ERROR", message: "Unknown command: 'work_scope'", suggestion: "Available commands: ping" });
test("older host status reports unsupported rather than requiring an exception approval", async (t) => {
  const { clients: [a], requests } = await setup(t, unsupported);
  const result = await a.sendCommand("work_scope", { op: "status" });
  assert.equal(result.status, "success");
  assert.equal(result.data.supported, false);
  assert.equal(result.data.reservation_required, false);
  assert.match(result.data.suggestion, /already-authorized/);
  assert.equal(result.data.assignment, undefined);
  const edit = await a.sendCommand("modify_element_parameter", { value: "legacy-approved" });
  assert.equal(edit.status, "success");
  assert.equal(requests.at(-1).work_scope_token, undefined);
});

test("unsupported acquire fails honestly and never manufactures a reservation", async (t) => {
  const { clients: [a] } = await setup(t, unsupported);
  const result = await a.sendCommand("work_scope", acquire("unsupported"));
  assert.equal(result.status, "error");
  assert.equal(result.error.code, "WORK_SCOPE_UNSUPPORTED");
  assert.equal(result.data, undefined);
});

test("supported status distinguishes disabled coordination from missing functionality", async (t) => {
  const { clients: [a] } = await setup(t);
  const result = await a.sendCommand("work_scope", { op: "status" });
  assert.equal(result.data.supported, true);
  assert.equal(result.data.coordination_enabled, false);
  assert.equal(result.data.reservation_required, false);
});

test("connection, target and reservation errors never downgrade to unsupported", async (t) => {
  let code;
  const { clients: [a] } = await setup(t, () => ({ code, message: "Unknown command: 'work_scope'", suggestion: "Resolve the actual error." }));
  for (code of ["CONNECTION_ERROR", "TIMEOUT_ERROR", "TARGET_DOCUMENT_MISMATCH", "WORK_SCOPE_STALE", "WORK_SCOPE_CONFLICT", "WORK_SCOPE_EXPIRED"]) {
    const result = await a.sendCommand("work_scope", { op: "status" });
    assert.equal(result.status, "error");
    assert.equal(result.error.code, code);
  }
});

test("unknown-command response with an existing or explicit token cannot enable legacy fallback", async (t) => {
  let fail = false;
  const { clients: [a, b], requests } = await setup(t, () => fail ? unsupported() : undefined);
  const acquired = await a.sendCommand("work_scope", acquire("a"));
  fail = true;
  assert.equal((await a.sendCommand("work_scope", { op: "status" })).status, "error");
  assert.equal((await b.sendCommand("work_scope", { op: "release", lease_token: "a".repeat(32) })).status, "error");
  await a.sendCommand("modify_element_parameter", { value: "still-guarded" });
  assert.equal(requests.at(-1).work_scope_token, acquired.data.assignment.lease_token);
});

test("malformed acquire success becomes an actionable error", async (t) => {
  const { clients: [a] } = await setup(t);
  const result = await a.sendCommand("work_scope", acquire("malformed"));
  assert.equal(result.status, "error");
  assert.match(result.error.suggestion, /same key/);
});

test("target clearing waits for already-submitted work and retains its reservation for reselect", async (t) => {
  const { clients: [a], requests } = await setup(t);
  const acquired = await a.sendCommand("work_scope", acquire("a"));
  const write = a.sendCommand("modify_element_parameter", { value: "before-clear" });
  const clear = a.clearTarget();
  assert.equal((await write).status, "success");
  await clear;
  assert.equal(a.getSelectedTarget(), null);
  await a.selectTarget("s1");
  await a.sendCommand("modify_element_parameter", { value: "after-reselect" });
  assert.equal(requests.find((r) => r.params.value === "before-clear").work_scope_token, acquired.data.assignment.lease_token);
  assert.equal(requests.find((r) => r.params.value === "after-reselect").work_scope_token, acquired.data.assignment.lease_token);
});
