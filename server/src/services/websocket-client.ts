import {
  CadWebSocketClient,
  DEFAULT_TIMEOUT_MS,
  type CommandExecutionOptions,
  type CommandResponse,
  type ErrorCode,
} from "@kimminsub/mcp-cad-core";
import { WS_URL, LOG_PREFIX, getRevitAuthHeaders } from "../constants.js";
import {
  RevitSessionRegistry,
  type RevitSessionRecord,
  type SessionDiscoveryResult,
} from "./session-registry.js";

const NOT_CONNECTED_SUGGESTION =
  "Ensure Revit is running with the MCP plugin loaded, then retry.";

export interface SelectedRevitTarget {
  session_id: string;
  document_fingerprint: string;
  document_title: string;
  document_path: string;
  pid: number;
  selected_at_utc: string;
}

export interface RevitWebSocketClientOptions {
  registry?: RevitSessionRegistry;
  legacyUrl?: string;
  clientFactory?: (session: RevitSessionRecord) => CadWebSocketClient;
}

interface ManagedClient {
  endpoint: string;
  client: CadWebSocketClient;
}

/**
 * Routes MCP commands to one of the live Revit plugin sessions advertised in
 * the per-process registry. With no registry entries it behaves exactly like
 * the historical single WebSocket client on REVIT_MCP_PORT (default 8181).
 */
export class RevitWebSocketClient extends CadWebSocketClient {
  private readonly registry: RevitSessionRegistry;
  private readonly clientFactory: (
    session: RevitSessionRecord,
  ) => CadWebSocketClient;
  private readonly managedClients = new Map<string, ManagedClient>();
  private selectedTarget: SelectedRevitTarget | null = null;

  constructor(options: RevitWebSocketClientOptions = {}) {
    super({
      url: options.legacyUrl ?? WS_URL,
      logPrefix: LOG_PREFIX,
      headers: getRevitAuthHeaders,
      notConnectedSuggestion: NOT_CONNECTED_SUGGESTION,
    });
    this.registry = options.registry ?? new RevitSessionRegistry();
    this.clientFactory = options.clientFactory ?? createSessionClient;
  }

  override get isConnected(): boolean {
    if (super.isConnected) return true;
    if (this.selectedTarget) {
      return (
        this.managedClients.get(this.selectedTarget.session_id)?.client
          .isConnected ?? false
      );
    }
    for (const managed of this.managedClients.values()) {
      if (managed.client.isConnected) return true;
    }
    return false;
  }

  async getLiveSessions(): Promise<SessionDiscoveryResult> {
    const discovery = await this.registry.discover();
    if (discovery.sessions.length > 0) {
      // Cancel a legacy :8181 connection/retry once a registry-aware plugin is
      // available. Otherwise the same Revit process could receive two sockets.
      super.disconnect();
    }
    this.removeDeadClients(discovery.sessions);
    return discovery;
  }

  getSelectedTarget(): SelectedRevitTarget | null {
    return this.selectedTarget ? { ...this.selectedTarget } : null;
  }

  async selectTarget(sessionId: string): Promise<SelectedRevitTarget> {
    const normalized = sessionId.trim();
    const discovery = await this.getLiveSessions();
    const session = discovery.sessions.find(
      (candidate) => candidate.session_id === normalized,
    );
    if (!session) {
      throw new TargetSelectionError(
        "SESSION_NOT_FOUND",
        `No live Revit session has session_id '${normalized}'.`,
        "Call revit_list_sessions, then pass one of the returned session_id values.",
      );
    }

    await this.verifySessionIdentity(session);

    this.selectedTarget = createSelectedTarget(session);
    for (const [managedSessionId, managed] of this.managedClients) {
      if (managedSessionId === session.session_id) continue;
      managed.client.disconnect();
      this.managedClients.delete(managedSessionId);
    }
    return { ...this.selectedTarget };
  }

  clearTarget(): void {
    this.selectedTarget = null;
    for (const managed of this.managedClients.values()) {
      managed.client.disconnect();
    }
    this.managedClients.clear();
  }

  override async connect(): Promise<void> {
    const discovery = await this.getLiveSessions();
    if (this.selectedTarget) {
      const session = discovery.sessions.find(
        (candidate) => candidate.session_id === this.selectedTarget?.session_id,
      );
      if (!session) return;
      await this.getOrCreateClient(session).connect();
      return;
    }
    if (discovery.sessions.length === 1) {
      const session = discovery.sessions[0];
      await this.getOrCreateClient(session).connect();
      return;
    }
    if (discovery.sessions.length > 1) {
      // Ambiguous by design. A later revit_set_target call selects one without
      // requiring this server process to restart.
      return;
    }
    await super.connect();
  }

  override async sendCommand(
    command: string,
    params: Record<string, unknown> = {},
    timeoutMs: number = DEFAULT_TIMEOUT_MS,
    options: CommandExecutionOptions = {},
  ): Promise<CommandResponse> {
    let discovery: SessionDiscoveryResult;
    try {
      discovery = await this.getLiveSessions();
    } catch (error) {
      return commandError(
        "VALIDATION_ERROR",
        `Cannot safely read the Revit instance registry: ${toMessage(error)}`,
        false,
        "Restore %LOCALAPPDATA%\\RevitMCP\\instances as a normal local directory, then retry.",
      );
    }

    if (this.selectedTarget) {
      const session = discovery.sessions.find(
        (candidate) => candidate.session_id === this.selectedTarget?.session_id,
      );
      if (!session) {
        return commandError(
          "SESSION_NOT_FOUND",
          `Selected Revit session '${this.selectedTarget.session_id}' is no longer live.`,
          true,
          "Call revit_list_sessions and revit_set_target before retrying. Do not assume another Revit window is the same target.",
        );
      }
      if (
        session.document_fingerprint.toLowerCase() !==
        this.selectedTarget.document_fingerprint.toLowerCase()
      ) {
        return commandError(
          "TARGET_DOCUMENT_MISMATCH",
          `The selected Revit session changed its active document from '${this.selectedTarget.document_title}' to '${session.active_document_title}'.`,
          true,
          "Review the new active document, then explicitly call revit_set_target again if it is the intended model.",
        );
      }
      return this.sendToSession(
        session,
        this.selectedTarget.document_fingerprint,
        command,
        params,
        timeoutMs,
        options,
      );
    }

    if (discovery.sessions.length > 1) {
      return commandError(
        "TARGET_SELECTION_REQUIRED",
        `There are ${discovery.sessions.length} live Revit sessions and no target is selected.`,
        true,
        "Call revit_list_sessions, inspect document paths, then call revit_set_target with the intended session_id before running any Revit command.",
      );
    }

    if (discovery.sessions.length === 1) {
      const session = discovery.sessions[0];
      // Auto-targeting a single process is still a pin, not a moving alias.
      // Once the first command observes this document, a later tab switch must
      // fail closed exactly like an explicit revit_set_target selection.
      try {
        await this.verifySessionIdentity(session);
      } catch (error) {
        return selectionErrorResponse(error);
      }
      this.selectedTarget = createSelectedTarget(session);
      return this.sendToSession(
        session,
        this.selectedTarget.document_fingerprint,
        command,
        params,
        timeoutMs,
        options,
      );
    }

    // Backward compatibility for plugins that predate the instance registry.
    if (!super.isConnected) {
      try {
        await super.connect();
      } catch {
        // sendCommand returns the core's structured connection error.
      }
    }
    const legacyProbe = await super.sendCommand("ping", {}, 10_000);
    if (legacyProbe.status === "error") return legacyProbe;
    const legacyData = isRecord(legacyProbe.data) ? legacyProbe.data : null;
    if (
      typeof legacyData?.session_id === "string" ||
      typeof legacyData?.document_fingerprint === "string"
    ) {
      return commandError(
        "TARGET_SELECTION_REQUIRED",
        "A registry-aware Revit plugin answered on the legacy endpoint, but no live session record is available.",
        true,
        "Do not run the requested command. Restore %LOCALAPPDATA%\\RevitMCP\\instances, wait for the heartbeat, then call revit_list_sessions and revit_set_target.",
      );
    }
    if (command === "ping") return legacyProbe;
    return super.sendCommand(command, params, timeoutMs, options);
  }

  override disconnect(): void {
    for (const managed of this.managedClients.values()) {
      managed.client.disconnect();
    }
    this.managedClients.clear();
    super.disconnect();
  }

  private async sendToSession(
    session: RevitSessionRecord,
    expectedDocumentFingerprint: string,
    command: string,
    params: Record<string, unknown>,
    timeoutMs: number,
    options: CommandExecutionOptions,
  ): Promise<CommandResponse> {
    const client = this.getOrCreateClient(session);
    if (!client.isConnected) {
      try {
        await client.connect();
      } catch {
        // The shared client produces the canonical connection error below.
      }
    }
    return client.sendCommand(command, params, timeoutMs, {
      ...options,
      targetSessionId: session.session_id,
      expectedDocumentFingerprint,
    });
  }

  private async verifySessionIdentity(
    session: RevitSessionRecord,
  ): Promise<void> {
    const client = this.getOrCreateClient(session);
    try {
      if (!client.isConnected) {
        try {
          await client.connect();
        } catch {
          // The guarded ping below returns the shared structured connection error.
        }
      }

      const verification = await client.sendCommand("ping", {}, 10_000, {
        targetSessionId: session.session_id,
        expectedDocumentFingerprint: session.document_fingerprint,
      });
      if (verification.status === "error") {
        throw new TargetSelectionError(
          verification.error?.code ?? "CONNECTION_ERROR",
          verification.error?.message ??
            `Could not verify Revit session '${session.session_id}'.`,
          verification.error?.suggestion ??
            "Call revit_list_sessions and retry only after the intended Revit document is active.",
        );
      }

      const verifiedData = isRecord(verification.data)
        ? verification.data
        : null;
      const verifiedSessionId = verifiedData?.session_id;
      const verifiedFingerprint = verifiedData?.document_fingerprint;
      if (
        verifiedSessionId !== session.session_id ||
        typeof verifiedFingerprint !== "string" ||
        verifiedFingerprint.toLowerCase() !==
          session.document_fingerprint.toLowerCase()
      ) {
        throw new TargetSelectionError(
          "TARGET_SESSION_MISMATCH",
          `The endpoint for session '${session.session_id}' did not prove the registered session and document identity.`,
          "Refresh revit_list_sessions. If this repeats, restart the intended Revit process so its registry record and plugin endpoint are recreated.",
        );
      }
    } catch (error) {
      client.disconnect();
      this.managedClients.delete(session.session_id);
      throw error;
    }

    return;
  }

  private getOrCreateClient(session: RevitSessionRecord): CadWebSocketClient {
    const host = session.host ?? "127.0.0.1";
    const endpoint = `ws://${host}:${session.port}`;
    const existing = this.managedClients.get(session.session_id);
    if (existing?.endpoint === endpoint) return existing.client;
    existing?.client.disconnect();
    const client = this.clientFactory(session);
    this.managedClients.set(session.session_id, { endpoint, client });
    return client;
  }

  private removeDeadClients(liveSessions: RevitSessionRecord[]): void {
    const liveIds = new Set(liveSessions.map((session) => session.session_id));
    for (const [sessionId, managed] of this.managedClients) {
      if (!liveIds.has(sessionId)) {
        managed.client.disconnect();
        this.managedClients.delete(sessionId);
      }
    }
  }
}

export class TargetSelectionError extends Error {
  constructor(
    readonly code: ErrorCode,
    message: string,
    readonly suggestion: string,
  ) {
    super(message);
    this.name = "TargetSelectionError";
  }
}

function createSessionClient(session: RevitSessionRecord): CadWebSocketClient {
  return new CadWebSocketClient({
    url: `ws://${session.host ?? "127.0.0.1"}:${session.port}`,
    logPrefix: `${LOG_PREFIX}:${session.pid}`,
    headers: getRevitAuthHeaders,
    notConnectedSuggestion:
      `Ensure Revit PID ${session.pid} is still running with the MCP plugin loaded, ` +
      "then call revit_list_sessions before retrying.",
  });
}

function createSelectedTarget(
  session: RevitSessionRecord,
): SelectedRevitTarget {
  return {
    session_id: session.session_id,
    document_fingerprint: session.document_fingerprint,
    document_title: session.active_document_title,
    document_path: session.active_document_path,
    pid: session.pid,
    selected_at_utc: new Date().toISOString(),
  };
}

function commandError(
  code: ErrorCode,
  message: string,
  recoverable: boolean,
  suggestion: string,
): CommandResponse {
  return {
    id: "",
    status: "error",
    error: { code, message, recoverable, suggestion },
  };
}

function selectionErrorResponse(error: unknown): CommandResponse {
  if (error instanceof TargetSelectionError) {
    return commandError(error.code, error.message, true, error.suggestion);
  }
  return commandError(
    "INTERNAL_ERROR",
    `Could not verify the only registered Revit session: ${toMessage(error)}`,
    true,
    "Call revit_list_sessions and explicitly select the intended session before retrying.",
  );
}

function toMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}
