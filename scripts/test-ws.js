// Direct WebSocket test — bypasses MCP TS server, talks to plugin directly
// Usage: node scripts/test-ws.js <command> [json-params]
import WebSocket from "ws";

const command = process.argv[2] || "get_project_info";
const params = process.argv[3] ? JSON.parse(process.argv[3]) : {};
const id = `test-${Date.now()}`;

const ws = new WebSocket("ws://127.0.0.1:8181/");

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
