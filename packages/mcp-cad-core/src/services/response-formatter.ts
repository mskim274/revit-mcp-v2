// Response Formatter — Shared helper for formatting CAD command responses
// into MCP-compatible text content.
//
// Harness Engineering — Tier 1: Response Size Overflow Protection
// ----------------------------------------------------------------
// Large-model queries (e.g., 396K-element Revit projects, sprawling DWG
// drawings with 10K+ blocks) can produce responses that blow up token
// costs even when the JSON is logically small. This formatter enforces
// a two-tier policy:
//
//   1. Soft limit (default 25KB): spill full payload to a temp file,
//      return a summary header + file path + first ~12KB preview.
//      Claude can then choose to read the full file only if needed.
//
//   2. Hard limit (default 500KB): same spill, plus an explicit
//      "exceeds hard limit" marker.
//
// Aligned with Claude Code's 25KB/500KB output limits and the SWE-agent
// ACI principle: paginated viewer, not full dump.

import { writeFile, mkdir } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { randomUUID } from "node:crypto";
import {
  DEFAULT_TIMEOUT_MS,
  RESPONSE_SIZE_SOFT_LIMIT,
  RESPONSE_SIZE_HARD_LIMIT,
} from "../constants.js";
import { CadWebSocketClient } from "./websocket-client.js";

type TextContent = { type: "text"; text: string };
type McpResult = { content: TextContent[] };

export interface ResponseFormatterConfig {
  // Subdir under OS temp where oversize responses get spilled. Different
  // products use different names so the temp folder doesn't collide if
  // both Revit MCP and AutoCAD MCP run on the same machine.
  // e.g. "revit-mcp-spill", "autocad-mcp-spill".
  spillDirName: string;
  // Optional override for soft limit (bytes). Defaults to RESPONSE_SIZE_SOFT_LIMIT.
  softLimit?: number;
  // Optional override for hard limit (bytes). Defaults to RESPONSE_SIZE_HARD_LIMIT.
  hardLimit?: number;
}

// Bind a config to a sendAndFormat helper. Each MCP server creates this once.
export function createResponseFormatter(config: ResponseFormatterConfig) {
  const softLimit = config.softLimit ?? RESPONSE_SIZE_SOFT_LIMIT;
  const hardLimit = config.hardLimit ?? RESPONSE_SIZE_HARD_LIMIT;

  async function sendAndFormat(
    wsClient: CadWebSocketClient,
    command: string,
    params: Record<string, unknown> = {},
    timeoutMs: number = DEFAULT_TIMEOUT_MS
  ): Promise<McpResult> {
    const response = await wsClient.sendCommand(command, params, timeoutMs);

    if (response.status === "error") {
      return {
        content: [
          {
            type: "text" as const,
            text: `Error: ${response.error?.message ?? "Unknown error"}${
              response.error?.suggestion
                ? `\nSuggestion: ${response.error.suggestion}`
                : ""
            }`,
          },
        ],
      };
    }

    const fullJson = JSON.stringify(response.data, null, 2);
    return protectAgainstOverflow(fullJson, command);
  }

  async function protectAgainstOverflow(
    fullJson: string,
    command: string
  ): Promise<McpResult> {
    const byteSize = Buffer.byteLength(fullJson, "utf8");

    if (byteSize <= softLimit) {
      return { content: [{ type: "text" as const, text: fullJson }] };
    }

    const spillPath = await spillToDisk(fullJson, command, config.spillDirName);
    const truncated = byteSize > hardLimit;
    const inlineSize = Math.min(byteSize, softLimit / 2);
    const preview = fullJson.slice(0, inlineSize);

    const summary = [
      `⚠️  Response overflow: ${formatBytes(byteSize)} exceeds soft limit (${formatBytes(softLimit)}).`,
      truncated
        ? `   Response also exceeds hard limit (${formatBytes(hardLimit)}) — full payload spilled to disk.`
        : `   Full payload spilled to disk for inspection.`,
      `   Command: ${command}`,
      `   Spill file: ${spillPath}`,
      ``,
      `── Preview (first ~${formatBytes(inlineSize)}) ──`,
      preview,
      byteSize > inlineSize ? `\n… [${formatBytes(byteSize - inlineSize)} more in spill file]` : "",
      ``,
      `💡 Tip: narrow the query (add filters, reduce 'limit', use 'summary_only: true')`,
      `   to avoid spill files. Read the full payload only if needed.`,
    ].join("\n");

    return { content: [{ type: "text" as const, text: summary }] };
  }

  return { sendAndFormat, protectAgainstOverflow };
}

async function spillToDisk(
  fullJson: string,
  command: string,
  spillDirName: string
): Promise<string> {
  const dir = join(tmpdir(), spillDirName);
  await mkdir(dir, { recursive: true });

  const safeCommand = command.replace(/[^a-zA-Z0-9_-]/g, "_");
  const timestamp = new Date().toISOString().replace(/[:.]/g, "-");
  const id = randomUUID().slice(0, 8);
  const filename = `${safeCommand}-${timestamp}-${id}.json`;
  const path = join(dir, filename);

  await writeFile(path, fullJson, "utf8");
  return path;
}

function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(2)} MB`;
}
