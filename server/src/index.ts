#!/usr/bin/env node

/**
 * Revit MCP Server — Entry Point
 *
 * TypeScript MCP server that bridges Claude Desktop with Autodesk Revit
 * via WebSocket connection to the Revit Plugin.
 *
 * Transport: stdio (MCP standard)
 * Internal: WebSocket to Revit Plugin
 */

import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { SERVER_NAME, SERVER_VERSION } from "./constants.js";
import { RevitWebSocketClient } from "./services/websocket-client.js";
import { registerUtilityTools } from "./tools/utility.js";
import { registerQueryTools } from "./tools/query.js";
import { registerCreateTools } from "./tools/create.js";
import { registerModifyTools } from "./tools/modify.js";
import { registerViewTools } from "./tools/view.js";

async function main(): Promise<void> {
  // Initialize MCP Server
  const server = new McpServer({
    name: SERVER_NAME,
    version: SERVER_VERSION,
  });

  // Initialize WebSocket client for Revit Plugin communication
  const wsClient = new RevitWebSocketClient();

  // Register all tools
  registerUtilityTools(server, wsClient);
  registerQueryTools(server, wsClient);
  registerCreateTools(server, wsClient);
  registerModifyTools(server, wsClient);
  registerViewTools(server, wsClient);

  // TODO: Sprint 5 — registerExportTools(server, wsClient);
  // TODO: Sprint 5 — registerAdvancedTools(server, wsClient);

  // Attempt initial WebSocket connection (non-blocking)
  wsClient.connect().catch((error) => {
    console.error(
      `[revit-mcp] Initial connection failed: ${error instanceof Error ? error.message : "Unknown error"}. Will retry automatically.`
    );
  });

  // Start stdio transport for MCP protocol
  const transport = new StdioServerTransport();
  await server.connect(transport);

  console.error(`[revit-mcp] ${SERVER_NAME} v${SERVER_VERSION} started (stdio transport)`);

  // Graceful shutdown
  process.on("SIGINT", () => {
    console.error("[revit-mcp] Shutting down...");
    wsClient.disconnect();
    process.exit(0);
  });

  process.on("SIGTERM", () => {
    console.error("[revit-mcp] Shutting down...");
    wsClient.disconnect();
    process.exit(0);
  });
}

main().catch((error) => {
  console.error("[revit-mcp] Fatal error:", error);
  process.exit(1);
});
