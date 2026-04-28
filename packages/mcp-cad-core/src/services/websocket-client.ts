// Generic CAD plugin WebSocket client. Used by Revit MCP and AutoCAD MCP
// servers. The wire protocol (CommandRequest / CommandResponse) is identical
// across products — only the URL and log prefix differ.

import WebSocket from "ws";
import { v4 as uuidv4 } from "uuid";
import {
  WS_RECONNECT_INTERVAL_MS,
  WS_MAX_RECONNECT_ATTEMPTS,
  WS_PING_INTERVAL_MS,
  DEFAULT_TIMEOUT_MS,
} from "../constants.js";
import type { CommandRequest, CommandResponse } from "../types.js";

export interface CadWebSocketClientConfig {
  url: string;
  // Prefix for stderr log lines, e.g. "[revit-mcp]" or "[autocad-mcp]".
  // Without brackets — the client adds them.
  logPrefix: string;
  // Hint shown in the suggestion field when the client is not connected.
  // e.g. "Ensure Revit is running with the MCP plugin loaded".
  notConnectedSuggestion?: string;
}

interface PendingRequest {
  resolve: (response: CommandResponse) => void;
  reject: (error: Error) => void;
  timer: ReturnType<typeof setTimeout>;
}

export class CadWebSocketClient {
  private ws: WebSocket | null = null;
  private pendingRequests = new Map<string, PendingRequest>();
  private reconnectAttempts = 0;
  private reconnectTimer: ReturnType<typeof setTimeout> | null = null;
  private pingTimer: ReturnType<typeof setInterval> | null = null;
  private isConnecting = false;
  private _isConnected = false;
  private readonly url: string;
  private readonly logTag: string;
  private readonly notConnectedSuggestion: string;

  constructor(config: CadWebSocketClientConfig) {
    this.url = config.url;
    this.logTag = `[${config.logPrefix}]`;
    this.notConnectedSuggestion =
      config.notConnectedSuggestion ??
      "Ensure the CAD application is running with the MCP plugin loaded, then retry.";
  }

  get isConnected(): boolean {
    return this._isConnected;
  }

  async connect(): Promise<void> {
    if (this.isConnecting || this._isConnected) return;
    this.isConnecting = true;

    return new Promise<void>((resolve, reject) => {
      try {
        this.ws = new WebSocket(this.url);

        this.ws.on("open", () => {
          this._isConnected = true;
          this.isConnecting = false;
          this.reconnectAttempts = 0;
          this.startPingInterval();
          console.error(`${this.logTag} Connected to plugin at ${this.url}`);
          resolve();
        });

        this.ws.on("message", (data: WebSocket.Data) => {
          this.handleMessage(data.toString());
        });

        this.ws.on("close", () => {
          this._isConnected = false;
          this.isConnecting = false;
          this.stopPingInterval();
          console.error(`${this.logTag} Connection closed`);
          this.scheduleReconnect();
        });

        this.ws.on("error", (error: Error) => {
          this.isConnecting = false;
          if (!this._isConnected) {
            console.error(`${this.logTag} Connection failed: ${error.message}`);
            this.scheduleReconnect();
            reject(error);
          }
        });
      } catch (error) {
        this.isConnecting = false;
        reject(error);
      }
    });
  }

  async sendCommand(
    command: string,
    params: Record<string, unknown> = {},
    timeoutMs: number = DEFAULT_TIMEOUT_MS
  ): Promise<CommandResponse> {
    if (!this._isConnected || !this.ws) {
      return {
        id: "",
        status: "error",
        error: {
          code: "CONNECTION_ERROR",
          message: "Not connected to CAD plugin",
          recoverable: true,
          suggestion: this.notConnectedSuggestion,
        },
      };
    }

    const id = uuidv4();
    const request: CommandRequest = {
      id,
      command,
      params,
      timeout_ms: timeoutMs,
    };

    return new Promise<CommandResponse>((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pendingRequests.delete(id);
        resolve({
          id,
          status: "error",
          error: {
            code: "TIMEOUT_ERROR",
            message: `Command '${command}' timed out after ${timeoutMs}ms`,
            recoverable: true,
            suggestion:
              "Try reducing the scope with the 'limit' parameter, or use 'summary_only: true' for large datasets.",
          },
        });
      }, timeoutMs);

      this.pendingRequests.set(id, { resolve, reject, timer });

      this.ws!.send(JSON.stringify(request), (error) => {
        if (error) {
          clearTimeout(timer);
          this.pendingRequests.delete(id);
          resolve({
            id,
            status: "error",
            error: {
              code: "CONNECTION_ERROR",
              message: `Failed to send command: ${error.message}`,
              recoverable: true,
              suggestion: "Check if the CAD application is still running.",
            },
          });
        }
      });
    });
  }

  disconnect(): void {
    this.stopPingInterval();
    if (this.reconnectTimer) {
      clearTimeout(this.reconnectTimer);
      this.reconnectTimer = null;
    }
    for (const [id, pending] of this.pendingRequests) {
      clearTimeout(pending.timer);
      pending.resolve({
        id,
        status: "error",
        error: {
          code: "CONNECTION_ERROR",
          message: "Connection closed",
          recoverable: false,
        },
      });
    }
    this.pendingRequests.clear();

    if (this.ws) {
      this.ws.close();
      this.ws = null;
    }
    this._isConnected = false;
  }

  private handleMessage(data: string): void {
    try {
      const response = JSON.parse(data) as CommandResponse;

      if (response.status === "progress") {
        console.error(
          `${this.logTag} Progress: ${response.progress?.message} (${response.progress?.current}/${response.progress?.total})`
        );
        return;
      }

      const pending = this.pendingRequests.get(response.id);
      if (pending) {
        clearTimeout(pending.timer);
        this.pendingRequests.delete(response.id);
        pending.resolve(response);
      }
    } catch (error) {
      console.error(`${this.logTag} Failed to parse message: ${data}`);
    }
  }

  private scheduleReconnect(): void {
    if (this.reconnectAttempts >= WS_MAX_RECONNECT_ATTEMPTS) {
      console.error(
        `${this.logTag} Max reconnect attempts (${WS_MAX_RECONNECT_ATTEMPTS}) reached. Giving up.`
      );
      return;
    }

    this.reconnectAttempts++;
    console.error(
      `${this.logTag} Reconnecting in ${WS_RECONNECT_INTERVAL_MS}ms (attempt ${this.reconnectAttempts}/${WS_MAX_RECONNECT_ATTEMPTS})`
    );

    this.reconnectTimer = setTimeout(() => {
      this.connect().catch(() => {
        // Error handled in connect()
      });
    }, WS_RECONNECT_INTERVAL_MS);
  }

  private startPingInterval(): void {
    this.pingTimer = setInterval(() => {
      if (this.ws?.readyState === WebSocket.OPEN) {
        this.ws.ping();
      }
    }, WS_PING_INTERVAL_MS);
  }

  private stopPingInterval(): void {
    if (this.pingTimer) {
      clearInterval(this.pingTimer);
      this.pingTimer = null;
    }
  }
}
