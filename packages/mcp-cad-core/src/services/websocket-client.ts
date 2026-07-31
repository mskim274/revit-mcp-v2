// Generic CAD plugin WebSocket client. Used by Revit MCP and AutoCAD MCP
// servers. The wire protocol (CommandRequest / CommandResponse) is identical
// across products — only connection/auth configuration and log prefixes differ.

import WebSocket from "ws";
import { randomUUID } from "node:crypto";
import {
  WS_RECONNECT_INTERVAL_MS,
  WS_MAX_RECONNECT_ATTEMPTS,
  WS_MAX_RECONNECT_INTERVAL_MS,
  WS_PING_INTERVAL_MS,
  DEFAULT_TIMEOUT_MS,
} from "../constants.js";
import type { CommandRequest, CommandResponse } from "../types.js";

type HeaderProvider =
  | Record<string, string>
  | (() => Record<string, string> | undefined);

export interface CadWebSocketClientConfig {
  url: string;
  // Prefix for stderr log lines, e.g. "[revit-mcp]" or "[autocad-mcp]".
  // Without brackets — the client adds them.
  logPrefix: string;
  // Hint shown in the suggestion field when the client is not connected.
  notConnectedSuggestion?: string;
  // Called for every connection attempt so credentials created after MCP
  // startup (for example when Revit first launches) are picked up on retry.
  headers?: HeaderProvider;
  // Overrides are primarily useful for deterministic tests.
  reconnectIntervalMs?: number;
  maxReconnectIntervalMs?: number;
  pingIntervalMs?: number;
}

export interface CommandExecutionOptions {
  // Side-effect timeouts are unknown outcomes: the CAD host may commit after
  // the MCP-side timer fires. This changes the recovery guidance accordingly.
  sideEffect?: boolean;
  timeoutSuggestion?: string;
}

interface PendingRequest {
  resolve: (response: CommandResponse) => void;
  timer: ReturnType<typeof setTimeout>;
  command: string;
  sideEffect: boolean;
  idempotencyKey?: string;
}

export class CadWebSocketClient {
  private ws: WebSocket | null = null;
  private pendingRequests = new Map<string, PendingRequest>();
  private reconnectAttempts = 0;
  private reconnectTimer: ReturnType<typeof setTimeout> | null = null;
  private pingTimer: ReturnType<typeof setInterval> | null = null;
  private connectPromise: Promise<void> | null = null;
  private commandQueue: Promise<void> = Promise.resolve();
  private intentionallyClosed = false;
  private _isConnected = false;
  private readonly url: string;
  private readonly logTag: string;
  private readonly notConnectedSuggestion: string;
  private readonly headers?: HeaderProvider;
  private readonly reconnectIntervalMs: number;
  private readonly maxReconnectIntervalMs: number;
  private readonly pingIntervalMs: number;

  constructor(config: CadWebSocketClientConfig) {
    this.url = config.url;
    this.logTag = `[${config.logPrefix}]`;
    this.notConnectedSuggestion =
      config.notConnectedSuggestion ??
      "Ensure the CAD application is running with the MCP plugin loaded, then retry.";
    this.headers = config.headers;
    this.reconnectIntervalMs =
      config.reconnectIntervalMs ?? WS_RECONNECT_INTERVAL_MS;
    this.maxReconnectIntervalMs =
      config.maxReconnectIntervalMs ?? WS_MAX_RECONNECT_INTERVAL_MS;
    this.pingIntervalMs = config.pingIntervalMs ?? WS_PING_INTERVAL_MS;
  }

  get isConnected(): boolean {
    return (
      this._isConnected &&
      this.ws !== null &&
      this.ws.readyState === WebSocket.OPEN
    );
  }

  connect(): Promise<void> {
    if (this.isConnected) return Promise.resolve();
    if (this.connectPromise) return this.connectPromise;

    this.intentionallyClosed = false;

    let socket: WebSocket;
    try {
      const headers =
        typeof this.headers === "function" ? this.headers() : this.headers;
      socket = new WebSocket(
        this.url,
        headers && Object.keys(headers).length > 0 ? { headers } : undefined
      );
      this.ws = socket;
    } catch (error) {
      const connectionError = toError(error);
      this.scheduleReconnect();
      return Promise.reject(connectionError);
    }

    let settled = false;
    let connectionError: Error | null = null;
    const attempt = new Promise<void>((resolve, reject) => {
      const resolveOnce = (): void => {
        if (settled) return;
        settled = true;
        resolve();
      };
      const rejectOnce = (error: Error): void => {
        if (settled) return;
        settled = true;
        reject(error);
      };

      socket.on("open", () => {
        if (this.ws !== socket) {
          socket.close();
          rejectOnce(new Error("Connection superseded by a newer attempt"));
          return;
        }
        this._isConnected = true;
        this.reconnectAttempts = 0;
        this.clearReconnectTimer();
        this.startPingInterval();
        console.error(`${this.logTag} Connected to plugin at ${this.url}`);
        resolveOnce();
      });

      socket.on("message", (data: WebSocket.Data) => {
        this.handleMessage(data.toString());
      });

      socket.on("close", () => {
        const isCurrentSocket = this.ws === socket;
        if (isCurrentSocket) {
          this.ws = null;
          this._isConnected = false;
          this.stopPingInterval();
          this.failPendingRequests(
            "Connection closed while a command was in progress",
            true
          );
        }

        rejectOnce(
          connectionError ??
            new Error("WebSocket connection closed before opening")
        );

        if (isCurrentSocket) {
          console.error(`${this.logTag} Connection closed`);
          this.scheduleReconnect();
        }
      });

      socket.on("error", (error: Error) => {
        connectionError = error;
        if (this.ws === socket) {
          this._isConnected = false;
          console.error(`${this.logTag} Connection failed: ${error.message}`);
        }
        // `ws` emits close after error. Reconnect is scheduled only from the
        // close handler. Keeping the attempt promise alive until that close
        // also prevents a caller from starting a second overlapping attempt.
      });
    });

    this.connectPromise = attempt;
    attempt.then(
      () => {
        if (this.connectPromise === attempt) this.connectPromise = null;
      },
      () => {
        if (this.connectPromise === attempt) this.connectPromise = null;
      }
    );
    return attempt;
  }

  sendCommand(
    command: string,
    params: Record<string, unknown> = {},
    timeoutMs: number = DEFAULT_TIMEOUT_MS,
    options: CommandExecutionOptions = {}
  ): Promise<CommandResponse> {
    // The plugin processes one request at a time per WebSocket connection.
    // Serialize here as well so a queued command's timeout starts when it is
    // actually sent, not while an earlier Revit API operation is still running.
    const task = this.commandQueue.then(() =>
      this.sendCommandNow(command, params, timeoutMs, options)
    );
    this.commandQueue = task.then(
      () => undefined,
      () => undefined
    );
    return task;
  }

  disconnect(): void {
    this.intentionallyClosed = true;
    this.clearReconnectTimer();
    this.stopPingInterval();
    this.failPendingRequests("Connection closed by MCP server shutdown", false);

    const socket = this.ws;
    this.ws = null;
    this._isConnected = false;
    if (socket && socket.readyState !== WebSocket.CLOSED) {
      socket.close();
    }
  }

  private sendCommandNow(
    command: string,
    params: Record<string, unknown>,
    timeoutMs: number,
    options: CommandExecutionOptions
  ): Promise<CommandResponse> {
    const socket = this.ws;
    if (!this.isConnected || !socket || socket.readyState !== WebSocket.OPEN) {
      return Promise.resolve({
        id: "",
        status: "error",
        error: {
          code: "CONNECTION_ERROR",
          message: "Not connected to CAD plugin",
          recoverable: true,
          suggestion: this.notConnectedSuggestion,
        },
      });
    }

    const id = randomUUID();
    const hasIdempotencyKey = Object.prototype.hasOwnProperty.call(
      params,
      "idempotency_key"
    );
    const sideEffect =
      options.sideEffect ?? hasIdempotencyKey;
    const suppliedKey = params.idempotency_key;
    let idempotencyKey: string | undefined;
    let requestParams = params;

    if (
      typeof suppliedKey === "string" &&
      suppliedKey.trim().length > 0 &&
      suppliedKey.trim().length <= 512
    ) {
      idempotencyKey = suppliedKey.trim();
    } else if (
      sideEffect &&
      (!hasIdempotencyKey || suppliedKey === undefined)
    ) {
      // The CAD plugins otherwise fall back to the transient wire request id.
      // Generate an explicit key so an unknown-outcome error can expose it to
      // the caller for a safe, identical retry.
      idempotencyKey = randomUUID();
      requestParams = {
        ...params,
        idempotency_key: idempotencyKey,
      };
    }

    const request: CommandRequest = {
      id,
      command,
      params: requestParams,
      timeout_ms: timeoutMs,
    };

    return new Promise<CommandResponse>((resolve) => {
      const timer = setTimeout(() => {
        const pending = this.pendingRequests.get(id);
        if (!pending || !this.pendingRequests.delete(id)) return;
        resolve(
          this.buildTransportError(
            id,
            pending,
            "TIMEOUT_ERROR",
            `Command '${command}' timed out after ${timeoutMs}ms`,
            true,
            options.timeoutSuggestion ??
              "Narrow the query, reduce its limit/detail, or increase the command timeout before retrying."
          )
        );
      }, timeoutMs);

      this.pendingRequests.set(id, {
        resolve,
        timer,
        command,
        sideEffect,
        idempotencyKey,
      });

      try {
        socket.send(JSON.stringify(request), (error) => {
          if (!error) return;
          const pending = this.pendingRequests.get(id);
          if (!pending) return;
          this.resolvePending(
            id,
            this.buildTransportError(
              id,
              pending,
              "CONNECTION_ERROR",
              `Failed to send command: ${error.message}`,
              true,
              this.notConnectedSuggestion
            )
          );
        });
      } catch (error) {
        const sendError = toError(error);
        const pending = this.pendingRequests.get(id);
        if (!pending) return;
        this.resolvePending(
          id,
          this.buildTransportError(
            id,
            pending,
            "CONNECTION_ERROR",
            `Failed to send command: ${sendError.message}`,
            true,
            this.notConnectedSuggestion
          )
        );
      }
    });
  }

  private handleMessage(data: string): void {
    try {
      const parsed: unknown = JSON.parse(data);
      if (!isCommandResponse(parsed)) {
        console.error(
          `${this.logTag} Ignored malformed response: ${truncateForLog(data)}`
        );
        return;
      }

      if (parsed.status === "progress") {
        console.error(
          `${this.logTag} Progress: ${parsed.progress?.message ?? "Working"} (${parsed.progress?.current ?? "?"}/${parsed.progress?.total ?? "?"})`
        );
        return;
      }

      this.resolvePending(parsed.id, parsed);
    } catch (error) {
      console.error(
        `${this.logTag} Failed to parse response (${toError(error).message}): ${truncateForLog(data)}`
      );
    }
  }

  private resolvePending(id: string, response: CommandResponse): void {
    const pending = this.pendingRequests.get(id);
    if (!pending) return;
    clearTimeout(pending.timer);
    this.pendingRequests.delete(id);
    pending.resolve(this.attachSideEffectRecovery(response, pending));
  }

  private failPendingRequests(message: string, recoverable: boolean): void {
    for (const [id, pending] of this.pendingRequests) {
      clearTimeout(pending.timer);
      pending.resolve(
        this.buildTransportError(
          id,
          pending,
          "CONNECTION_ERROR",
          message,
          recoverable,
          recoverable ? this.notConnectedSuggestion : undefined
        )
      );
    }
    this.pendingRequests.clear();
  }

  private buildTransportError(
    id: string,
    pending: PendingRequest,
    code: "CONNECTION_ERROR" | "TIMEOUT_ERROR",
    message: string,
    recoverable: boolean,
    querySuggestion?: string
  ): CommandResponse {
    const error: NonNullable<CommandResponse["error"]> = {
      code,
      message,
      recoverable,
      suggestion: pending.sideEffect
        ? sideEffectRecoverySuggestion(
            pending.command,
            pending.idempotencyKey
          )
        : querySuggestion,
    };
    if (pending.sideEffect && pending.idempotencyKey) {
      error.idempotency_key = pending.idempotencyKey;
    }
    return { id, status: "error", error };
  }

  private attachSideEffectRecovery(
    response: CommandResponse,
    pending: PendingRequest
  ): CommandResponse {
    if (
      response.status !== "error" ||
      !response.error ||
      !pending.sideEffect ||
      !pending.idempotencyKey
    ) {
      return response;
    }

    const uncertain =
      response.error.code === "TIMEOUT_ERROR" ||
      response.error.code === "CONNECTION_ERROR" ||
      response.error.code === "SERVER_SHUTDOWN" ||
      response.error.code === "INTERNAL_ERROR";
    return {
      ...response,
      error: {
        ...response.error,
        idempotency_key: pending.idempotencyKey,
        suggestion: uncertain
          ? sideEffectRecoverySuggestion(
              pending.command,
              pending.idempotencyKey
            )
          : response.error.suggestion,
      },
    };
  }

  private scheduleReconnect(): void {
    if (
      this.intentionallyClosed ||
      this.isConnected ||
      this.reconnectTimer !== null
    ) {
      return;
    }

    this.reconnectAttempts++;
    const exponent = Math.min(
      Math.max(this.reconnectAttempts - 1, 0),
      WS_MAX_RECONNECT_ATTEMPTS - 1
    );
    const delay = Math.min(
      this.reconnectIntervalMs * 2 ** exponent,
      this.maxReconnectIntervalMs
    );

    console.error(
      `${this.logTag} Reconnecting in ${delay}ms (attempt ${this.reconnectAttempts})`
    );

    this.reconnectTimer = setTimeout(() => {
      this.reconnectTimer = null;
      if (this.intentionallyClosed || this.isConnected) return;
      this.connect().catch(() => {
        // Error is logged by connect(); close schedules the next single retry.
      });
    }, delay);
  }

  private clearReconnectTimer(): void {
    if (!this.reconnectTimer) return;
    clearTimeout(this.reconnectTimer);
    this.reconnectTimer = null;
  }

  private startPingInterval(): void {
    this.stopPingInterval();
    this.pingTimer = setInterval(() => {
      if (this.ws?.readyState === WebSocket.OPEN) {
        this.ws.ping();
      }
    }, this.pingIntervalMs);
  }

  private stopPingInterval(): void {
    if (!this.pingTimer) return;
    clearInterval(this.pingTimer);
    this.pingTimer = null;
  }
}

function isCommandResponse(value: unknown): value is CommandResponse {
  if (!value || typeof value !== "object") return false;
  const candidate = value as Partial<CommandResponse>;
  return (
    typeof candidate.id === "string" &&
    (candidate.status === "success" ||
      candidate.status === "error" ||
      candidate.status === "progress")
  );
}

function truncateForLog(value: string, maxChars = 1000): string {
  return value.length <= maxChars ? value : `${value.slice(0, maxChars)}…`;
}

function toError(value: unknown): Error {
  return value instanceof Error ? value : new Error(String(value));
}

function sideEffectRecoverySuggestion(
  command: string,
  idempotencyKey?: string
): string {
  const retryGuidance = idempotencyKey
    ? "If an identical retry is necessary, pass the exact value from error.idempotency_key."
    : "If an identical retry is necessary, reuse the exact same non-empty idempotency_key.";
  return (
    `The side-effect command '${command}' may still complete or may already ` +
    "have committed. Verify the model/output before retrying. " +
    retryGuidance
  );
}
