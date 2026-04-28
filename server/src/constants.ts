// Revit-specific constants. Generic timeouts / pagination defaults / overflow
// limits live in @kimminsub/mcp-cad-core.

const HOST = process.env.REVIT_MCP_HOST ?? "127.0.0.1";
const PORT = parseInt(process.env.REVIT_MCP_PORT ?? "8181", 10);

export const WS_URL = `ws://${HOST}:${PORT}`;

// Stderr log prefix and oversize-response spill subdir.
// Spill dir is product-specific so Revit MCP and AutoCAD MCP don't collide
// in %TEMP% if both run on the same machine.
export const LOG_PREFIX = "revit-mcp";
export const RESPONSE_SPILL_DIR = "revit-mcp-spill";

export const SERVER_NAME = "revit-mcp-server";
export const SERVER_VERSION = "0.2.0";
