import test from "node:test";
import assert from "node:assert/strict";
import { once } from "node:events";
import { randomUUID } from "node:crypto";
import {
  access,
  mkdir,
  rm,
  utimes,
  writeFile,
} from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { WebSocketServer } from "ws";

import {
  CadWebSocketClient,
  CursorValidationError,
  createCursor,
  createResponseFormatter,
  parseCursor,
  parseTcpPort,
} from "../../dist/index.js";

async function startServer(onConnection) {
  const server = new WebSocketServer({ host: "127.0.0.1", port: 0 });
  if (onConnection) server.on("connection", onConnection);
  await once(server, "listening");
  const address = server.address();
  assert.equal(typeof address, "object");
  return {
    server,
    url: `ws://127.0.0.1:${address.port}`,
  };
}

async function stopServer(server) {
  for (const socket of server.clients) socket.terminate();
  if (server._state === 2) return;
  await new Promise((resolve) => server.close(resolve));
}

function responseFor(request, data = { ok: true }) {
  return JSON.stringify({
    id: request.id,
    status: "success",
    data,
  });
}

test("connect is shared, sends the exact auth header, and intentional close does not reconnect", async (t) => {
  let connectionCount = 0;
  let authorization;
  const { server, url } = await startServer((socket, request) => {
    connectionCount++;
    authorization = request.headers.authorization;
    socket.on("message", (data) => {
      const command = JSON.parse(data.toString());
      socket.send(responseFor(command));
    });
  });
  t.after(() => stopServer(server));

  const client = new CadWebSocketClient({
    url,
    logPrefix: "core-test",
    headers: () => ({ Authorization: "Bearer exact-test-token" }),
    reconnectIntervalMs: 20,
    maxReconnectIntervalMs: 40,
    pingIntervalMs: 60_000,
  });
  t.after(() => client.disconnect());

  const firstConnect = client.connect();
  const secondConnect = client.connect();
  assert.strictEqual(firstConnect, secondConnect);
  await Promise.all([firstConnect, secondConnect]);

  assert.equal(connectionCount, 1);
  assert.equal(authorization, "Bearer exact-test-token");
  assert.equal((await client.sendCommand("ping")).status, "success");

  client.disconnect();
  await new Promise((resolve) => setTimeout(resolve, 100));
  assert.equal(connectionCount, 1);
});

test("commands are sequential and each timeout starts when the command is sent", async (t) => {
  let active = 0;
  let maxActive = 0;
  const received = [];
  const { server, url } = await startServer((socket) => {
    socket.on("message", (data) => {
      const request = JSON.parse(data.toString());
      received.push(request.command);
      active++;
      maxActive = Math.max(maxActive, active);
      setTimeout(() => {
        active--;
        socket.send(responseFor(request, { command: request.command }));
      }, 180);
    });
  });
  t.after(() => stopServer(server));

  const client = new CadWebSocketClient({
    url,
    logPrefix: "core-test",
    pingIntervalMs: 60_000,
  });
  t.after(() => client.disconnect());
  await client.connect();

  const started = Date.now();
  const [first, second] = await Promise.all([
    client.sendCommand("first", {}, 300),
    client.sendCommand("second", {}, 300),
  ]);

  assert.equal(first.status, "success");
  assert.equal(second.status, "success");
  assert.deepEqual(received, ["first", "second"]);
  assert.equal(maxActive, 1);
  assert.ok(
    Date.now() - started >= 330,
    "the second command should be sent only after the first response"
  );
});

test("socket closure resolves an in-flight command immediately", async (t) => {
  const { server, url } = await startServer((socket) => {
    socket.once("message", () => socket.close(1011, "test close"));
  });
  t.after(() => stopServer(server));

  const client = new CadWebSocketClient({
    url,
    logPrefix: "core-test",
    reconnectIntervalMs: 10_000,
    pingIntervalMs: 60_000,
  });
  t.after(() => client.disconnect());
  await client.connect();

  const started = Date.now();
  const response = await client.sendCommand("long_query", {}, 5000);
  assert.equal(response.status, "error");
  assert.equal(response.error?.code, "CONNECTION_ERROR");
  assert.ok(Date.now() - started < 1000);
});

test("a dropped connection creates only one reconnect attempt", async (t) => {
  let connectionCount = 0;
  let firstSocket;
  const { server, url } = await startServer((socket) => {
    connectionCount++;
    if (connectionCount === 1) firstSocket = socket;
  });
  t.after(() => stopServer(server));

  const client = new CadWebSocketClient({
    url,
    logPrefix: "core-test",
    reconnectIntervalMs: 20,
    maxReconnectIntervalMs: 20,
    pingIntervalMs: 60_000,
  });
  t.after(() => client.disconnect());
  await client.connect();

  firstSocket.terminate();
  const deadline = Date.now() + 1000;
  while (connectionCount < 2 && Date.now() < deadline) {
    await new Promise((resolve) => setTimeout(resolve, 10));
  }
  assert.equal(connectionCount, 2);
  await new Promise((resolve) => setTimeout(resolve, 80));
  assert.equal(connectionCount, 2);
});

test("side-effect timeout explains the unknown outcome and exact-key retry", async (t) => {
  let receivedKey;
  const { server, url } = await startServer((socket) => {
    socket.on("message", (data) => {
      receivedKey = JSON.parse(data.toString()).params.idempotency_key;
      // Deliberately leave the command unanswered.
    });
  });
  t.after(() => stopServer(server));

  const client = new CadWebSocketClient({
    url,
    logPrefix: "core-test",
    pingIntervalMs: 60_000,
  });
  t.after(() => client.disconnect());
  await client.connect();

  const response = await client.sendCommand(
    "create_item",
    {},
    25,
    { sideEffect: true }
  );
  assert.equal(response.status, "error");
  assert.equal(response.error?.code, "TIMEOUT_ERROR");
  assert.match(receivedKey, /^[0-9a-f-]{36}$/);
  assert.equal(response.error?.idempotency_key, receivedKey);
  assert.match(response.error?.suggestion ?? "", /may already have committed/i);
  assert.match(response.error?.suggestion ?? "", /error\.idempotency_key/);
});

test("a dropped side-effect connection preserves its generated retry key", async (t) => {
  let receivedKey;
  const { server, url } = await startServer((socket) => {
    socket.once("message", (data) => {
      receivedKey = JSON.parse(data.toString()).params.idempotency_key;
      socket.terminate();
    });
  });
  t.after(() => stopServer(server));

  const client = new CadWebSocketClient({
    url,
    logPrefix: "core-test",
    reconnectIntervalMs: 10_000,
    pingIntervalMs: 60_000,
  });
  t.after(() => client.disconnect());
  await client.connect();

  const response = await client.sendCommand(
    "create_item",
    { idempotency_key: undefined },
    5000
  );
  assert.equal(response.status, "error");
  assert.equal(response.error?.code, "CONNECTION_ERROR");
  assert.equal(response.error?.idempotency_key, receivedKey);
  assert.match(response.error?.suggestion ?? "", /error\.idempotency_key/);
});

test("response formatter emits MCP errors and handles missing success data", async () => {
  const formatter = createResponseFormatter({
    spillDirName: `mcp-core-test-${randomUUID()}`,
  });
  const errorClient = {
    async sendCommand() {
      return {
        id: "1",
        status: "error",
        error: {
          code: "BAD_INPUT",
          message: "invalid",
          recoverable: true,
          suggestion: "fix it",
          idempotency_key: "retry-key",
        },
      };
    },
  };
  const errorResult = await formatter.sendAndFormat(
    errorClient,
    "bad_command"
  );
  assert.equal(errorResult.isError, true);
  assert.deepEqual(errorResult.structuredContent, {
    error: {
      code: "BAD_INPUT",
      message: "invalid",
      recoverable: true,
      suggestion: "fix it",
      idempotency_key: "retry-key",
    },
  });

  const emptyClient = {
    async sendCommand() {
      return { id: "2", status: "success" };
    },
  };
  const emptyResult = await formatter.sendAndFormat(
    emptyClient,
    "empty_command"
  );
  assert.equal(emptyResult.content[0].text, "null");
});

test("overflow preview is UTF-8 safe and old spill files are removed", async (t) => {
  const spillDirName = `mcp-core-test-${randomUUID()}`;
  const spillDir = join(tmpdir(), spillDirName);
  await mkdir(spillDir, { recursive: true });
  const oldFile = join(spillDir, "old.json");
  await writeFile(oldFile, "{}");
  const oldTime = new Date(Date.now() - 60_000);
  await utimes(oldFile, oldTime, oldTime);
  t.after(() => rm(spillDir, { recursive: true, force: true }));

  const formatter = createResponseFormatter({
    spillDirName,
    softLimit: 20,
    hardLimit: 40,
    spillRetentionMs: 1000,
  });
  const result = await formatter.protectAgainstOverflow(
    "가".repeat(30),
    "unicode"
  );

  assert.doesNotMatch(result.content[0].text, /\uFFFD/);
  const spillFile = result.structuredContent?.overflow?.spill_file;
  assert.equal(typeof spillFile, "string");
  await access(spillFile);
  await assert.rejects(access(oldFile));
});

test("overflow spill failure still returns a bounded warning", async () => {
  const formatter = createResponseFormatter({
    spillDirName: `invalid\u0000${randomUUID()}`,
    softLimit: 8,
    hardLimit: 16,
  });
  const result = await formatter.protectAgainstOverflow(
    "x".repeat(100),
    "spill_failure"
  );

  assert.equal(result.isError, undefined);
  assert.equal(result.structuredContent?.warning?.code, "SPILL_ERROR");
  assert.ok(Buffer.byteLength(result.content[0].text, "utf8") < 2000);
});

test("pagination accepts documented cursors and rejects malformed input", () => {
  const encoded = createCursor(42);
  assert.equal(parseCursor(encoded), 42);
  assert.equal(parseCursor("42"), 42);
  assert.equal(parseCursor(), 0);
  assert.throws(() => parseCursor("not-a-cursor"), CursorValidationError);
  assert.throws(
    () => parseCursor(Buffer.from("offset:-1").toString("base64")),
    CursorValidationError
  );
  assert.throws(() => createCursor(-1), RangeError);
});

test("TCP port configuration rejects partial and out-of-range values", () => {
  assert.equal(parseTcpPort(undefined, 8181, "REVIT_MCP_PORT"), 8181);
  assert.equal(parseTcpPort(" 8182 ", 8181, "AUTOCAD_MCP_PORT"), 8182);

  for (const invalid of ["", "0", "65536", "-1", "8182oops", "8.182"]) {
    assert.throws(
      () => parseTcpPort(invalid, 8181, "AUTOCAD_MCP_PORT"),
      /AUTOCAD_MCP_PORT must be an integer from 1 to 65535/,
    );
  }
});
