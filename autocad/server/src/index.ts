#!/usr/bin/env node

// AutoCAD MCP Server — entry point.
// Same shape as revit-mcp-v2's server: stdio MCP transport, WebSocket out
// to the AutoCAD plugin. Sister project, sharing @kimminsub/mcp-cad-core
// for the WebSocket client, response-formatter overflow spill, pagination,
// and protocol types.

import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { SERVER_NAME, SERVER_VERSION } from "./constants.js";
import { AcadWebSocketClient } from "./services/websocket-client.js";
import { registerUtilityTools } from "./tools/utility.js";
import { registerQueryTools } from "./tools/query.js";
import { registerCreateTools } from "./tools/create.js";

async function main(): Promise<void> {
  const server = new McpServer({
    name: SERVER_NAME,
    version: SERVER_VERSION,
  });

  const wsClient = new AcadWebSocketClient();

  registerUtilityTools(server, wsClient);
  registerQueryTools(server, wsClient);
  registerCreateTools(server, wsClient);

  wsClient.connect().catch((error) => {
    console.error(
      `[autocad-mcp] Initial connection failed: ${error instanceof Error ? error.message : "Unknown error"}. Will retry automatically.`
    );
  });

  const transport = new StdioServerTransport();
  await server.connect(transport);

  console.error(`[autocad-mcp] ${SERVER_NAME} v${SERVER_VERSION} started (stdio transport)`);

  process.on("SIGINT", () => { wsClient.disconnect(); process.exit(0); });
  process.on("SIGTERM", () => { wsClient.disconnect(); process.exit(0); });
}

main().catch((error) => {
  console.error("[autocad-mcp] Fatal error:", error);
  process.exit(1);
});
