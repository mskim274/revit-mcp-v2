// AutoCAD-specific constants. Generic timeouts / pagination defaults / overflow
// limits live in @kimminsub/mcp-cad-core.

import { readFileSync } from "node:fs";
import { homedir } from "node:os";
import { join } from "node:path";
import { parseTcpPort } from "@kimminsub/mcp-cad-core/constants";

const HOST = process.env.AUTOCAD_MCP_HOST ?? "127.0.0.1";
const PORT = parseTcpPort(
  process.env.AUTOCAD_MCP_PORT,
  8182,
  "AUTOCAD_MCP_PORT",
);

export const WS_URL = `ws://${HOST}:${PORT}`;

const LOCAL_APP_DATA =
  process.env.LOCALAPPDATA ?? join(homedir(), "AppData", "Local");
export const AUTH_TOKEN_FILE = join(
  LOCAL_APP_DATA,
  "RevitMCP",
  "auth-token"
);

// Revit and AutoCAD intentionally share the same local bridge credential.
// Re-read it on every connection attempt so a CAD plugin started later can
// create the token without requiring an MCP server restart.
export function getCadAuthHeaders(): Record<string, string> | undefined {
  const environmentToken = process.env.REVIT_MCP_AUTH_TOKEN?.trim();
  if (environmentToken) {
    return { Authorization: `Bearer ${environmentToken}` };
  }

  try {
    const fileToken = readFileSync(AUTH_TOKEN_FILE, "utf8").trim();
    return fileToken
      ? { Authorization: `Bearer ${fileToken}` }
      : undefined;
  } catch {
    return undefined;
  }
}

// Per-product log prefix and overflow spill subdir, so Revit and AutoCAD
// servers don't collide in stderr or %TEMP%.
export const LOG_PREFIX = "autocad-mcp";
export const RESPONSE_SPILL_DIR = "autocad-mcp-spill";

export const SERVER_NAME = "autocad-mcp-server";

function readPackageVersion(): string {
  try {
    const packageJson = JSON.parse(
      readFileSync(new URL("../package.json", import.meta.url), "utf8")
    ) as { version?: unknown };
    if (
      typeof packageJson.version === "string" &&
      /^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$/.test(packageJson.version)
    ) {
      return packageJson.version;
    }
  } catch {
    // A missing package.json is a packaging error; the fallback keeps the
    // bridge startable and visibly marks the bad package.
  }
  return "0.0.0-unknown";
}

export const SERVER_VERSION = readPackageVersion();
