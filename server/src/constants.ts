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

// Response size overflow thresholds (Harness Engineering — Tier 1)
// When a response exceeds the soft limit, it is spilled to a temp file and
// only a summary + file path is returned inline. Protects against token-cost
// blowups on large-model queries (e.g., 396K-element projects).
export const RESPONSE_SIZE_SOFT_LIMIT = 25_000; // ~25 KB ≈ 6K tokens
export const RESPONSE_SIZE_HARD_LIMIT = 500_000; // 500 KB absolute cap
export const RESPONSE_SPILL_DIR = "revit-mcp-spill"; // subdir under OS temp

// Server info
export const SERVER_NAME = "revit-mcp-server";
export const SERVER_VERSION = "0.2.0";
