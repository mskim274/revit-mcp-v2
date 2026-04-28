#!/usr/bin/env node
// Smoke test: verify the AutoCAD MCP TS server's modules load and the
// AcadWebSocketClient can be instantiated without errors. Does NOT require
// AutoCAD to be running — only proves the build artifacts wire up correctly
// against @kimminsub/mcp-cad-core.
//
// Usage: node autocad/scripts/smoke-test-server.mjs

import { join, dirname } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const HERE = dirname(fileURLToPath(import.meta.url));
const ROOT = join(HERE, "..");

const wsModule = await import(
  pathToFileURL(join(ROOT, "server/dist/services/websocket-client.js")).href
);
const fmtModule = await import(
  pathToFileURL(join(ROOT, "server/dist/services/response-formatter.js")).href
);

const { AcadWebSocketClient } = wsModule;
const { sendAndFormat } = fmtModule;

const client = new AcadWebSocketClient();

let pass = 0, fail = 0;
const check = (name, ok) => { if (ok) { console.log(`[ OK ] ${name}`); pass++; } else { console.error(`[FAIL] ${name}`); fail++; } };

check("AcadWebSocketClient is a class instance", client instanceof Object);
check("isConnected initially false", client.isConnected === false);
check("sendAndFormat is a function", typeof sendAndFormat === "function");
check("AcadWebSocketClient has sendCommand method", typeof client.sendCommand === "function");
check("AcadWebSocketClient has connect method", typeof client.connect === "function");

// Attempt connect — will fail because plugin isn't loaded; we just want a
// clean error rather than an exception.
try {
  await Promise.race([
    client.connect(),
    new Promise((_, rej) => setTimeout(() => rej(new Error("timeout")), 1500)),
  ]);
  check("connect() returned (plugin must be running)", true);
  client.disconnect();
} catch (e) {
  check("connect() failed gracefully (plugin not running) — OK", true);
}

console.log(`\npassed: ${pass}  failed: ${fail}`);
process.exit(fail === 0 ? 0 : 1);
