import { open, lstat, readdir } from "node:fs/promises";
import { basename, join } from "node:path";
import { z } from "zod";
import {
  INSTANCE_REGISTRY_DIR,
  INSTANCE_STALE_AFTER_MS,
} from "../constants.js";

const MAX_REGISTRY_FILE_BYTES = 64 * 1024;
const MAX_FUTURE_CLOCK_SKEW_MS = 10_000;
const SESSION_FILE_PATTERN = /^(\d+)\.json$/;

const timestampSchema = z
  .string()
  .datetime({ offset: true })
  .refine(
    (value) => Number.isFinite(Date.parse(value)),
    "Expected an ISO-8601 UTC timestamp.",
  );

const loopbackHostSchema = z.literal("127.0.0.1").optional();

const registryRecordSchema = z
  .object({
    schema_version: z.literal(1),
    session_id: z
      .string()
      .trim()
      .min(1)
      .max(128)
      .regex(/^[A-Za-z0-9._:-]+$/),
    pid: z.number().int().positive().max(2_147_483_647),
    host: loopbackHostSchema,
    port: z.number().int().min(1).max(65_535),
    revit_version: z.string().trim().min(1).max(64),
    revit_build: z.string().trim().max(256),
    started_at_utc: timestampSchema,
    last_seen_utc: timestampSchema,
    active_document_title: z.string().trim().min(1).max(1024),
    active_document_path: z.string().max(32_768),
    document_fingerprint: z
      .string()
      .trim()
      .regex(/^[a-fA-F0-9]{64}$/, "Expected a SHA-256 fingerprint."),
  })
  .strict();

export interface RevitSessionRecord {
  schema_version: 1;
  session_id: string;
  pid: number;
  host?: "127.0.0.1";
  port: number;
  revit_version: string;
  revit_build: string;
  started_at_utc: string;
  last_seen_utc: string;
  active_document_title: string;
  active_document_path: string;
  document_fingerprint: string;
}

export interface IgnoredRegistryEntry {
  file: string;
  reason: string;
}

export interface SessionDiscoveryResult {
  sessions: RevitSessionRecord[];
  ignored_entry_count: number;
  ignored_entries: IgnoredRegistryEntry[];
}

export interface RevitSessionRegistryOptions {
  directory?: string;
  staleAfterMs?: number;
  now?: () => number;
  isPidAlive?: (pid: number) => boolean;
}

export class RevitSessionRegistry {
  readonly directory: string;
  private readonly staleAfterMs: number;
  private readonly now: () => number;
  private readonly isPidAlive: (pid: number) => boolean;

  constructor(options: RevitSessionRegistryOptions = {}) {
    this.directory = options.directory ?? INSTANCE_REGISTRY_DIR;
    this.staleAfterMs = options.staleAfterMs ?? INSTANCE_STALE_AFTER_MS;
    this.now = options.now ?? Date.now;
    this.isPidAlive = options.isPidAlive ?? defaultIsPidAlive;
  }

  async discover(): Promise<SessionDiscoveryResult> {
    let directoryInfo;
    try {
      directoryInfo = await lstat(this.directory);
    } catch (error) {
      if (isNodeError(error, "ENOENT")) {
        return {
          sessions: [],
          ignored_entry_count: 0,
          ignored_entries: [],
        };
      }
      throw error;
    }

    if (directoryInfo.isSymbolicLink() || !directoryInfo.isDirectory()) {
      throw new Error(
        `Revit instance registry is not a safe directory: ${this.directory}`,
      );
    }

    const entries = await readdir(this.directory, { withFileTypes: true });
    const sessions: RevitSessionRecord[] = [];
    const ignored: IgnoredRegistryEntry[] = [];

    for (const entry of entries) {
      const match = SESSION_FILE_PATTERN.exec(entry.name);
      if (!match) {
        if (entry.name.endsWith(".json")) {
          ignored.push({
            file: entry.name,
            reason: "Registry filename must be <pid>.json.",
          });
        }
        continue;
      }

      if (!entry.isFile() || entry.isSymbolicLink()) {
        ignored.push({
          file: entry.name,
          reason: "Registry entry is not a regular file.",
        });
        continue;
      }

      const filePath = join(this.directory, entry.name);
      try {
        const record = await this.readRecord(filePath);
        const filenamePid = Number(match[1]);
        if (!Number.isSafeInteger(filenamePid) || filenamePid !== record.pid) {
          throw new Error("Filename PID does not match the record PID.");
        }

        const seenAt = Date.parse(record.last_seen_utc);
        const ageMs = this.now() - seenAt;
        if (ageMs < -MAX_FUTURE_CLOCK_SKEW_MS) {
          throw new Error("Heartbeat timestamp is unexpectedly in the future.");
        }
        if (ageMs > this.staleAfterMs) {
          throw new Error("Heartbeat is stale.");
        }
        let pidAlive: boolean;
        try {
          pidAlive = this.isPidAlive(record.pid);
        } catch {
          throw new Error(
            "Could not verify whether the Revit process is alive.",
          );
        }
        if (!pidAlive) {
          throw new Error("Revit process is no longer running.");
        }

        sessions.push(record);
      } catch (error) {
        ignored.push({
          file: basename(filePath),
          reason: error instanceof Error ? error.message : String(error),
        });
      }
    }

    sessions.sort((left, right) => left.pid - right.pid);
    return {
      sessions,
      ignored_entry_count: ignored.length,
      ignored_entries: ignored.slice(0, 50),
    };
  }

  private async readRecord(filePath: string): Promise<RevitSessionRecord> {
    const before = await lstat(filePath);
    if (
      before.isSymbolicLink() ||
      !before.isFile() ||
      before.size <= 0 ||
      before.size > MAX_REGISTRY_FILE_BYTES
    ) {
      throw new Error("Registry entry has an unsafe file type or size.");
    }

    const handle = await open(filePath, "r");
    try {
      const opened = await handle.stat();
      if (
        !opened.isFile() ||
        opened.size <= 0 ||
        opened.size > MAX_REGISTRY_FILE_BYTES
      ) {
        throw new Error("Registry entry changed or has an unsafe size.");
      }
      const raw = (await handle.readFile("utf8")).replace(/^\uFEFF/, "");
      const parsed: unknown = JSON.parse(raw);
      return registryRecordSchema.parse(parsed) as RevitSessionRecord;
    } finally {
      await handle.close();
    }
  }
}

function defaultIsPidAlive(pid: number): boolean {
  try {
    process.kill(pid, 0);
    return true;
  } catch (error) {
    return isNodeError(error, "EPERM");
  }
}

function isNodeError(error: unknown, code: string): boolean {
  return (
    error instanceof Error &&
    "code" in error &&
    (error as NodeJS.ErrnoException).code === code
  );
}
