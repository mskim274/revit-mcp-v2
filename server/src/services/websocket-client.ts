/**
 * WebSocket Client for communicating with Revit Plugin
 *
 * Handles connection lifecycle, reconnection, request/response matching,
 * and timeout management.
 */

import WebSocket from "ws";
import { v4 as uuidv4 } from "uuid";
import {
  WS_URL,
  WS_RECONNECT_INTERVAL_MS,
  WS_MAX_RECONNECT_ATTEMPTS,
  WS_PING_INTERVAL_MS,
  DEFAULT_TIMEOUT_MS,
} from "../constants.js";
import type { CommandRequest, CommandResponse } from "../types.js";

interface PendingRequest {
  resolve: (response: CommandResponse) => void;
  reject: (error: Error) => void;
  timer: ReturnType<typeof setTimeout>;
}

export class RevitWebSocketClient {
  private ws: WebSocket | null = null;
  private pendingRequests = new Map<string, PendingRequest>();
  private reconnectAttempts = 0;
  private reconnectTimer: ReturnType<typeof setTimeout> | null = null;
  private pingTimer: ReturnType<typeof setInterval> | null = null;
  private isConnecting = false;
  private _isConnected = false;

  get isConnected(): boolean {
    return this._isConnected;
  }

  /**
   * Connect to the Revit Plugin WebSocket server
   */
  async connect(): Promise<void> {
    if (this.isConnecting || this._isConnected) return;
    this.isConnecting = true;

    return new Promise<void>((resolve, reject) => {
      try {
        this.ws = new WebSocket(WS_URL);

        this.ws.on("open", () => {
          this._isConnected = true;
          this.isConnecting = false;
          this.reconnectAttempts = 0;
          this.startPingInterval();
          console.error(`[revit-mcp] Connected to Revit Plugin at ${WS_URL}`);
          resolve();
        });

        this.ws.on("message", (data: WebSocket.Data) => {
          this.handleMessage(data.toString());
        });

        this.ws.on("close", () => {
          this._isConnected = false;
          this.isConnecting = false;
          this.stopPingInterval();
          console.error("[revit-mcp] Connection closed");
          this.scheduleReconnect();
        });

        this.ws.on("error", (error: Error) => {
          this.isConnecting = false;
          if (!this._isConnected) {
            console.error(`[revit-mcp] Connection failed: ${error.message}`);
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

  /**
   * Send a command to Revit Plugin and wait for response
   */
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
          message: "Not connected to Revit Plugin",
          recoverable: true,
          suggestion: "Ensure Revit is running with the MCP plugin loaded, then retry.",
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
              suggestion: "Check if Revit is still running.",
            },
          });
        }
      });
    });
  }

  /**
   * Disconnect from Revit Plugin
   */
  disconnect(): void {
    this.stopPingInterval();
    if (this.reconnectTimer) {
      clearTimeout(this.reconnectTimer);
      this.reconnectTimer = null;
    }
    // Reject all pending requests
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

  // ─── Private Methods ───

  private handleMessage(data: string): void {
    try {
      const response = JSON.parse(data) as CommandResponse;

      // Handle progress messages (log but don't resolve)
      if (response.status === "progress") {
        console.error(
          `[revit-mcp] Progress: ${response.progress?.message} (${response.progress?.current}/${response.progress?.total})`
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
      console.error(`[revit-mcp] Failed to parse message: ${data}`);
    }
  }

  private scheduleReconnect(): void {
    if (this.reconnectAttempts >= WS_MAX_RECONNECT_ATTEMPTS) {
      console.error(
        `[revit-mcp] Max reconnect attempts (${WS_MAX_RECONNECT_ATTEMPTS}) reached. Giving up.`
      );
      return;
    }

    this.reconnectAttempts++;
    console.error(
      `[revit-mcp] Reconnecting in ${WS_RECONNECT_INTERVAL_MS}ms (attempt ${this.reconnectAttempts}/${WS_MAX_RECONNECT_ATTEMPTS})`
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
