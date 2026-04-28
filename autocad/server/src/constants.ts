// AutoCAD-specific constants. Generic timeouts / pagination defaults / overflow
// limits live in @kimminsub/mcp-cad-core.

const HOST = process.env.AUTOCAD_MCP_HOST ?? "127.0.0.1";
const PORT = parseInt(process.env.AUTOCAD_MCP_PORT ?? "8182", 10);

export const WS_URL = `ws://${HOST}:${PORT}`;

// Per-product log prefix and overflow spill subdir, so Revit and AutoCAD
// servers don't collide in stderr or %TEMP%.
export const LOG_PREFIX = "autocad-mcp";
export const RESPONSE_SPILL_DIR = "autocad-mcp-spill";

export const SERVER_NAME = "autocad-mcp-server";
export const SERVER_VERSION = "0.1.0";
