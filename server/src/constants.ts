/**
 * Revit MCP Server Constants
 */

// WebSocket connection to Revit Plugin
export const WS_HOST = process.env.REVIT_MCP_HOST ?? "127.0.0.1";
export const WS_PORT = parseInt(process.env.REVIT_MCP_PORT ?? "8181", 10);
export const WS_URL = `ws://${WS_HOST}:${WS_PORT}`;

// Connection settings
export const WS_RECONNECT_INTERVAL_MS = 5000;
export const WS_MAX_RECONNECT_ATTEMPTS = 10;
export const WS_PING_INTERVAL_MS = 30000;

// Command timeouts
export const DEFAULT_TIMEOUT_MS = 30000;
export const EXPORT_TIMEOUT_MS = 120000;
export const BATCH_TIMEOUT_MS = 120000;

// Pagination defaults
export const DEFAULT_PAGE_SIZE = 50;
export const MAX_PAGE_SIZE = 200;

// Server info
export const SERVER_NAME = "revit-mcp-server";
export const SERVER_VERSION = "0.1.0";
