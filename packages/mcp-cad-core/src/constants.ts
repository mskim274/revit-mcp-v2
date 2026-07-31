// Product-neutral defaults shared by all MCP↔CAD bridges.
// Product-specific values (host, port, server name, env-var names, spill dir)
// live in each consumer (server/src/constants.ts in the Revit MCP server).

// WebSocket connection settings
export const WS_RECONNECT_INTERVAL_MS = 5000;
// Reconnect forever, but cap exponential backoff at this interval. Desktop
// MCP servers often start before the CAD host and must still connect when the
// user opens it minutes or hours later.
export const WS_MAX_RECONNECT_INTERVAL_MS = 60_000;
// Kept for source compatibility with early consumers. It now caps the
// exponential-backoff exponent rather than stopping reconnection permanently.
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

/**
 * Parse an explicitly configured TCP port without parseInt's partial-input
 * behavior (for example, "8182oops" must not silently become port 8182).
 */
export function parseTcpPort(
  configuredValue: string | undefined,
  defaultPort: number,
  environmentVariableName: string,
): number {
  const value =
    configuredValue === undefined
      ? String(defaultPort)
      : configuredValue.trim();
  if (!/^\d+$/.test(value)) {
    throw new Error(
      `${environmentVariableName} must be an integer from 1 to 65535; ` +
        `received ${JSON.stringify(configuredValue)}.`,
    );
  }

  const port = Number(value);
  if (!Number.isSafeInteger(port) || port < 1 || port > 65_535) {
    throw new Error(
      `${environmentVariableName} must be an integer from 1 to 65535; ` +
        `received ${JSON.stringify(configuredValue)}.`,
    );
  }
  return port;
}
