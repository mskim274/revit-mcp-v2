// Direct WebSocket test — bypasses MCP TS server, talks to plugin directly.
// Usage: node scripts/test-ws.js <command> [json-params]
//
// Picks the port from MCP_PORT (or REVIT_MCP_PORT for legacy compat).
// Default 8181 (Revit). For AutoCAD: MCP_PORT=8182 node scripts/test-ws.js ping
import WebSocket from "ws";
import { readFileSync } from "node:fs";
import { homedir } from "node:os";
import { join } from "node:path";

const command = process.argv[2] || "get_project_info";
const params = process.argv[3] ? JSON.parse(process.argv[3]) : {};
const id = `test-${Date.now()}`;

const port = process.env.MCP_PORT || process.env.REVIT_MCP_PORT || process.env.AUTOCAD_MCP_PORT || "8181";
const tokenPath = join(
  process.env.LOCALAPPDATA ?? join(homedir(), "AppData", "Local"),
  "RevitMCP",
  "auth-token"
);
let authToken = process.env.REVIT_MCP_AUTH_TOKEN?.trim();
if (!authToken) {
  try {
    authToken = readFileSync(tokenPath, "utf8").trim();
  } catch {
    // Report one actionable error below.
  }
}
if (!authToken) {
  console.error(
    `[AUTH] No bridge token found. Start the Revit/AutoCAD plugin first, ` +
      `or set REVIT_MCP_AUTH_TOKEN. Expected file: ${tokenPath}`
  );
  process.exit(2);
}

const ws = new WebSocket(`ws://127.0.0.1:${port}/`, {
  headers: { Authorization: `Bearer ${authToken}` },
});

const timeout = setTimeout(() => {
  console.error("[TIMEOUT] no response in 30s");
  process.exit(1);
}, 30000);

ws.on("open", () => {
  const request = { id, command, params, timeout_ms: 25000 };
  console.error(`[SEND] ${JSON.stringify(request)}`);
  ws.send(JSON.stringify(request));
});

ws.on("message", (data) => {
  clearTimeout(timeout);
  const response = JSON.parse(data.toString());
  console.log(JSON.stringify(response, null, 2));
  ws.close();
});

ws.on("error", (e) => {
  console.error(`[ERROR] ${e.message}`);
  process.exit(1);
});

ws.on("unexpected-response", (_request, response) => {
  console.error(
    `[HTTP ${response.statusCode}] WebSocket upgrade rejected. ` +
      `If this is 401, make sure the CLI and plugin use the same bearer token.`
  );
  process.exit(1);
});
