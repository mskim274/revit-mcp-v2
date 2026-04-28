#!/usr/bin/env node
// Phase-1 regression test: drive the SAME WebSocket commands through the
// rebuilt server's shims (RevitWebSocketClient + sendAndFormat), and diff
// the resulting JSON payloads against the baseline captured by
// scripts/test-ws.js (which goes plugin-direct).
//
// This is what proves that swapping `services/*` to thin re-exports of
// @kimminsub/mcp-cad-core preserved behavior end-to-end through the TS layer.
//
// Usage: node scripts/verify-server-shim.mjs

import { readFileSync, mkdirSync, writeFileSync } from "node:fs";
import { join, dirname } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const ROOT = join(dirname(fileURLToPath(import.meta.url)), "..");
const BASELINE = join(ROOT, "작업자료/2026-04-28/snapshots/baseline-pre-refactor");
const CURRENT = join(ROOT, "작업자료/2026-04-28/snapshots/post-phase1-server-shim");
mkdirSync(CURRENT, { recursive: true });

// Import the rebuilt server's shims — exact same module path that index.ts uses.
// pathToFileURL is required on Windows: dynamic import() rejects bare drive paths.
const wsModule = await import(
  pathToFileURL(join(ROOT, "server/dist/services/websocket-client.js")).href
);
const fmtModule = await import(
  pathToFileURL(join(ROOT, "server/dist/services/response-formatter.js")).href
);
const { RevitWebSocketClient } = wsModule;
const { sendAndFormat } = fmtModule;

const client = new RevitWebSocketClient();
await client.connect();

const CASES = [
  ["01_ping", "ping", {}],
  ["02_get_levels", "get_levels", {}],
  ["03_get_grids", "get_grids", {}],
  ["04_get_all_categories", "get_all_categories", {}],
  ["05_query_walls_summary", "query_elements", { category: "Walls", summary_only: true }],
  ["06_get_views_limit10", "get_views", { limit: 10 }],
  ["07_query_walls_limit5", "query_elements", { category: "Walls", summary_only: false, limit: 5 }],
];

function stripVolatile(o) {
  if (o === null || typeof o !== "object") return o;
  if (Array.isArray(o)) return o.map(stripVolatile);
  const out = {};
  for (const [k, v] of Object.entries(o)) {
    if (k === "id" || k === "timestamp") continue;
    out[k] = stripVolatile(v);
  }
  return out;
}

let failed = 0;
for (const [label, command, params] of CASES) {
  const resp = await client.sendCommand(command, params);
  writeFileSync(join(CURRENT, `${label}.json`), JSON.stringify(resp, null, 2));

  const oldRaw = readFileSync(join(BASELINE, `${label}.json`), "utf8");
  const oldObj = stripVolatile(JSON.parse(oldRaw));
  const newObj = stripVolatile(resp);

  const oldStr = JSON.stringify(oldObj, null, 2);
  const newStr = JSON.stringify(newObj, null, 2);
  if (oldStr === newStr) {
    console.log(`[ OK ] ${label}`);
  } else {
    console.error(`[FAIL] ${label}`);
    const a = oldStr.split("\n");
    const b = newStr.split("\n");
    for (let i = 0; i < Math.max(a.length, b.length); i++) {
      if (a[i] !== b[i]) {
        console.error(`  line ${i + 1}:`);
        console.error(`    - ${a[i] ?? "(missing)"}`);
        console.error(`    + ${b[i] ?? "(missing)"}`);
        break;
      }
    }
    failed++;
  }
}

// Smoke-test the response-formatter shim too: it must still produce an
// MCP-shaped { content: [{type:"text", text}] } envelope. Use a tiny ping
// payload so we don't hit the overflow path.
const fmtResult = await sendAndFormat(client, "ping", {}, 10000);
const fmtOk =
  Array.isArray(fmtResult?.content) &&
  fmtResult.content.length === 1 &&
  fmtResult.content[0].type === "text" &&
  typeof fmtResult.content[0].text === "string" &&
  fmtResult.content[0].text.includes("revit_version");
if (fmtOk) {
  console.log(`[ OK ] sendAndFormat (response-formatter shim)`);
} else {
  console.error(`[FAIL] sendAndFormat — unexpected shape:`);
  console.error(JSON.stringify(fmtResult, null, 2).slice(0, 500));
  failed++;
}

client.disconnect();

if (failed) {
  console.error(`\n${failed} mismatch(es). Phase 1 introduced a regression.`);
  process.exit(1);
}
console.log("\nServer shim parity verified — Phase 1 refactor preserves behavior.");
process.exit(0);
