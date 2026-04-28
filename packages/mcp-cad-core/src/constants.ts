// Product-neutral defaults shared by all MCP↔CAD bridges.
// Product-specific values (host, port, server name, env-var names, spill dir)
// live in each consumer (server/src/constants.ts in the Revit MCP server).

// WebSocket connection settings
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

// Response size overflow thresholds (Harness Engineering — Tier 1).
// Above the soft limit, the response is spilled to a temp file and only a
// preview returned inline. Above the hard limit, an explicit truncation
// marker is added too. Protects against token-cost blowups on large-model
// queries (e.g., 396K-element Revit projects).
export const RESPONSE_SIZE_SOFT_LIMIT = 25_000;
export const RESPONSE_SIZE_HARD_LIMIT = 500_000;
