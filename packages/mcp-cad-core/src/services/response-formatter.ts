// Shared helper for formatting CAD command responses into MCP-compatible
// content, including structured errors and bounded overflow previews.

import {
  writeFile,
  mkdir,
  readdir,
  stat,
  unlink,
} from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { randomUUID } from "node:crypto";
import {
  DEFAULT_TIMEOUT_MS,
  RESPONSE_SIZE_SOFT_LIMIT,
  RESPONSE_SIZE_HARD_LIMIT,
} from "../constants.js";
import {
  CadWebSocketClient,
  type CommandExecutionOptions,
} from "./websocket-client.js";

type TextContent = { type: "text"; text: string };

export interface McpResult {
  [key: string]: unknown;
  content: TextContent[];
  isError?: boolean;
  structuredContent?: Record<string, unknown>;
}

export interface ResponseFormatterConfig {
  // Subdir under OS temp where oversize responses get spilled.
  spillDirName: string;
  softLimit?: number;
  hardLimit?: number;
  // Old spill files are opportunistically removed before a new spill.
  // Defaults to 24 hours.
  spillRetentionMs?: number;
}

const DEFAULT_SPILL_RETENTION_MS = 24 * 60 * 60 * 1000;

// Bind a config to a sendAndFormat helper. Each MCP server creates this once.
export function createResponseFormatter(config: ResponseFormatterConfig) {
  const softLimit = config.softLimit ?? RESPONSE_SIZE_SOFT_LIMIT;
  const hardLimit = config.hardLimit ?? RESPONSE_SIZE_HARD_LIMIT;
  const spillRetentionMs =
    config.spillRetentionMs ?? DEFAULT_SPILL_RETENTION_MS;

  if (!Number.isFinite(softLimit) || softLimit <= 0) {
    throw new RangeError("softLimit must be a positive finite byte count.");
  }
  if (!Number.isFinite(hardLimit) || hardLimit < softLimit) {
    throw new RangeError("hardLimit must be finite and >= softLimit.");
  }
  if (!Number.isFinite(spillRetentionMs) || spillRetentionMs < 0) {
    throw new RangeError("spillRetentionMs must be a non-negative duration.");
  }

  async function sendAndFormat(
    wsClient: CadWebSocketClient,
    command: string,
    params: Record<string, unknown> = {},
    timeoutMs: number = DEFAULT_TIMEOUT_MS,
    options: CommandExecutionOptions = {}
  ): Promise<McpResult> {
    const response = await wsClient.sendCommand(
      command,
      params,
      timeoutMs,
      options
    );

    if (response.status === "error") {
      const error = {
        code: response.error?.code ?? "INTERNAL_ERROR",
        message: response.error?.message ?? "Unknown error",
        recoverable: response.error?.recoverable ?? false,
        suggestion:
          response.error?.suggestion ??
          "Review the command inputs and current CAD application state before retrying.",
        ...(response.error?.idempotency_key
          ? { idempotency_key: response.error.idempotency_key }
          : {}),
      };
      return {
        isError: true,
        structuredContent: { error },
        content: [
          {
            type: "text" as const,
            text: JSON.stringify({ error }, null, 2),
          },
        ],
      };
    }

    // A protocol-success response is allowed to carry null data. Treat an
    // omitted data field as JSON null instead of throwing in Buffer.byteLength.
    const fullJson = JSON.stringify(response.data ?? null, null, 2) ?? "null";
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

    const inlineByteLimit = Math.max(1, Math.floor(softLimit / 2));
    const preview = truncateUtf8ByBytes(fullJson, inlineByteLimit);
    const previewBytes = Buffer.byteLength(preview, "utf8");
    const truncated = byteSize > hardLimit;

    let spillPath: string;
    try {
      spillPath = await spillToDisk(
        fullJson,
        command,
        config.spillDirName,
        spillRetentionMs
      );
    } catch (error) {
      const spillError =
        error instanceof Error ? error.message : String(error);
      const warning = {
        code: "SPILL_ERROR",
        message:
          "The CAD command succeeded, but its full response could not be written to the overflow spill file.",
        recoverable: true,
        suggestion:
          "Do not blindly retry a write command. Use the preview, verify model state, and rerun a narrower query if more detail is required.",
        response_bytes: byteSize,
        preview_bytes: previewBytes,
        spill_error: spillError,
      };
      const summary = [
        "⚠️ Response overflow; spill-to-disk failed.",
        `   Command: ${command}`,
        `   Response: ${formatBytes(byteSize)}`,
        `   Spill error: ${spillError}`,
        "",
        `── Preview (first ${formatBytes(previewBytes)}) ──`,
        preview,
        `\n… [${formatBytes(byteSize - previewBytes)} unavailable outside this preview]`,
        "",
        "The command itself succeeded. Verify state before retrying any write.",
      ].join("\n");
      return {
        structuredContent: { warning },
        content: [{ type: "text" as const, text: summary }],
      };
    }

    const overflow = {
      command,
      response_bytes: byteSize,
      preview_bytes: previewBytes,
      exceeds_hard_limit: truncated,
      spill_file: spillPath,
    };
    const summary = [
      `⚠️ Response overflow: ${formatBytes(byteSize)} exceeds soft limit (${formatBytes(softLimit)}).`,
      truncated
        ? `   Response also exceeds hard limit (${formatBytes(hardLimit)}); full payload was spilled to disk.`
        : "   Full payload was spilled to disk for inspection.",
      `   Command: ${command}`,
      `   Spill file: ${spillPath}`,
      "",
      `── Preview (first ${formatBytes(previewBytes)}) ──`,
      preview,
      byteSize > previewBytes
        ? `\n… [${formatBytes(byteSize - previewBytes)} more in spill file]`
        : "",
      "",
      "Tip: narrow the query, reduce detail/limit, or use summary mode to avoid spill files.",
    ].join("\n");

    return {
      structuredContent: { overflow },
      content: [{ type: "text" as const, text: summary }],
    };
  }

  return { sendAndFormat, protectAgainstOverflow };
}

async function spillToDisk(
  fullJson: string,
  command: string,
  spillDirName: string,
  spillRetentionMs: number
): Promise<string> {
  const dir = join(tmpdir(), spillDirName);
  await mkdir(dir, { recursive: true });
  await cleanupOldSpills(dir, spillRetentionMs);

  const safeCommand = command.replace(/[^a-zA-Z0-9_-]/g, "_");
  const timestamp = new Date().toISOString().replace(/[:.]/g, "-");
  const id = randomUUID().slice(0, 8);
  const filename = `${safeCommand}-${timestamp}-${id}.json`;
  const path = join(dir, filename);

  await writeFile(path, fullJson, { encoding: "utf8", flag: "wx" });
  return path;
}

async function cleanupOldSpills(
  dir: string,
  retentionMs: number
): Promise<void> {
  try {
    const entries = await readdir(dir, { withFileTypes: true });
    const cutoff = Date.now() - retentionMs;
    await Promise.allSettled(
      entries
        .filter((entry) => entry.isFile() && entry.name.endsWith(".json"))
        .map(async (entry) => {
          const path = join(dir, entry.name);
          const info = await stat(path);
          if (info.mtimeMs < cutoff) await unlink(path);
        })
    );
  } catch {
    // Cleanup is best-effort and must not make the current response fail.
  }
}

function truncateUtf8ByBytes(value: string, maxBytes: number): string {
  const bytes = Buffer.from(value, "utf8");
  if (bytes.length <= maxBytes) return value;

  // If the first excluded byte is a continuation byte, the boundary falls
  // inside a multi-byte code point. Move back to that code point's leading
  // byte instead of decoding an invalid suffix as U+FFFD.
  let end = maxBytes;
  while (end > 0 && (bytes[end] & 0xc0) === 0x80) end--;
  return bytes.subarray(0, end).toString("utf8");
}

function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(2)} MB`;
}
