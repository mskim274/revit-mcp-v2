// Revit-specific constants. Generic timeouts / pagination defaults / overflow
// limits live in @kimminsub/mcp-cad-core.

import { readFileSync } from "node:fs";
import { homedir } from "node:os";
import { join } from "node:path";
import { parseTcpPort } from "@kimminsub/mcp-cad-core/constants";

const HOST = process.env.REVIT_MCP_HOST ?? "127.0.0.1";
const PORT = parseTcpPort(
  process.env.REVIT_MCP_PORT,
  8181,
  "REVIT_MCP_PORT",
);

export const WS_URL = `ws://${HOST}:${PORT}`;

const LOCAL_APP_DATA =
  process.env.LOCALAPPDATA ?? join(homedir(), "AppData", "Local");
export const AUTH_TOKEN_FILE = join(
  LOCAL_APP_DATA,
  "RevitMCP",
  "auth-token"
);

// Environment configuration wins. Otherwise re-read the plugin-managed token
// file for every WebSocket attempt so an MCP server started before Revit can
// authenticate as soon as the plugin creates the file.
export function getRevitAuthHeaders():
  | Record<string, string>
  | undefined {
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

// Stderr log prefix and oversize-response spill subdir.
// Spill dir is product-specific so Revit MCP and AutoCAD MCP don't collide
// in %TEMP% if both run on the same machine.
export const LOG_PREFIX = "revit-mcp";
export const RESPONSE_SPILL_DIR = "revit-mcp-spill";

export const SERVER_NAME = "revit-mcp-server";

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
    // The package smoke test catches missing package metadata. Keep startup
    // diagnosable instead of throwing from module initialization.
  }
  return "0.0.0-unknown";
}

export const SERVER_VERSION = readPackageVersion();
