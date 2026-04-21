/**
 * Response Formatter — Shared helper for formatting Revit command responses
 * into MCP-compatible text content.
 *
 * Harness Engineering — Tier 1: Response Size Overflow Protection
 * ------------------------------------------------------------------
 * Large-model queries (e.g., 396K-element projects) can produce responses
 * that blow up token costs even when the JSON is logically small. This
 * formatter enforces a two-tier policy:
 *
 *   1. Soft limit (RESPONSE_SIZE_SOFT_LIMIT, default 25KB):
 *      Response is spilled to a temp file on disk. Only a summary header
 *      + file path + first N entries are returned inline. Claude can then
 *      choose to read the full file only if needed.
 *
 *   2. Hard limit (RESPONSE_SIZE_HARD_LIMIT, default 500KB):
 *      Absolute cap. Response is truncated with an explicit message.
 *
 * This pattern is consistent with:
 *   - Claude Code's 25KB/500KB soft/hard output limits
 *   - SWE-agent ACI principle: paginated file viewer, not full dump
 */

import { writeFile, mkdir } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { randomUUID } from "node:crypto";
import {
  DEFAULT_TIMEOUT_MS,
  RESPONSE_SIZE_SOFT_LIMIT,
  RESPONSE_SIZE_HARD_LIMIT,
  RESPONSE_SPILL_DIR,
} from "../constants.js";
import type { RevitWebSocketClient } from "./websocket-client.js";

type TextContent = { type: "text"; text: string };
type McpResult = { content: TextContent[] };

/**
 * Send a command through the WebSocket client and format the response into
 * MCP text content. Applies overflow protection on large payloads.
 */
export async function sendAndFormat(
  wsClient: RevitWebSocketClient,
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

/**
 * Protect against oversize responses.
 * - Below soft limit: return as-is.
 * - Between soft and hard limit: spill to temp file, return summary.
 * - Above hard limit: spill to temp file, return truncated summary.
 *
 * Exported for testing and for callers that build their own JSON payload.
 */
export async function protectAgainstOverflow(
  fullJson: string,
  command: string
): Promise<McpResult> {
  const byteSize = Buffer.byteLength(fullJson, "utf8");

  // Path A: well within limits — return directly
  if (byteSize <= RESPONSE_SIZE_SOFT_LIMIT) {
    return { content: [{ type: "text" as const, text: fullJson }] };
  }

  // Path B: oversize — spill to disk, return compact summary
  const spillPath = await spillToDisk(fullJson, command);
  const truncated = byteSize > RESPONSE_SIZE_HARD_LIMIT;
  const inlineSize = Math.min(byteSize, RESPONSE_SIZE_SOFT_LIMIT / 2); // ~12KB preview
  const preview = fullJson.slice(0, inlineSize);

  const summary = [
    `⚠️  Response overflow: ${formatBytes(byteSize)} exceeds soft limit (${formatBytes(RESPONSE_SIZE_SOFT_LIMIT)}).`,
    truncated
      ? `   Response also exceeds hard limit (${formatBytes(RESPONSE_SIZE_HARD_LIMIT)}) — full payload spilled to disk.`
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

/**
 * Write oversize response to a temp file.
 * Returns absolute path. Filename pattern: <command>-<timestamp>-<uuid>.json
 */
async function spillToDisk(fullJson: string, command: string): Promise<string> {
  const dir = join(tmpdir(), RESPONSE_SPILL_DIR);
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
